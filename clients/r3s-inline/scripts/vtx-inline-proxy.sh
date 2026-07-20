#!/bin/bash
set -e

# All site-specific values come from environment variables.
# Set them in /etc/vertex.env and load via systemd EnvironmentFile.
BROKER_LIST=${VTX_BROKER:?"VTX_BROKER not set"}
IFS=',' read -ra BROKER_IPS <<< "$BROKER_LIST"
LAN=${VTX_LAN_SUBNET:?"VTX_LAN_SUBNET not set"}
WAN_IFACE=${VTX_WAN_IFACE:?"VTX_WAN_IFACE not set"}
# Inline-specific: LAN-side interface (eth1 on NanoPi R3S). Required for the
# FORWARD-chain ACCEPT rules because in inline-mode R3S itself forwards LAN
# traffic, whereas Mikrotik-mode R3S was forwarding only its own packets.
LAN_IFACE=${VTX_LAN_IFACE:?"VTX_LAN_IFACE not set"}

TUN=${VTX_TUN_IFACE:-tun0}
TABLE=100
MARK=1
RU_URL="https://www.ipdeny.com/ipblocks/data/aggregated/ru-aggregated.zone"
RU_SET="ru-nets"
RU_FILE="/var/lib/vtx-ru-nets.zone"

cleanup() {
    ip rule del fwmark $MARK lookup $TABLE 2>/dev/null || true
    ip route flush table $TABLE 2>/dev/null || true
    iptables -t mangle -F VTX_FORWARD 2>/dev/null || true
    iptables -t mangle -D PREROUTING -j VTX_FORWARD 2>/dev/null || true
    iptables -t mangle -X VTX_FORWARD 2>/dev/null || true
    ipset destroy $RU_SET 2>/dev/null || true
    iptables -t nat -D POSTROUTING -o $WAN_IFACE -s $LAN -d 10.0.0.0/8 -j RETURN 2>/dev/null || true
    iptables -t nat -D POSTROUTING -o $WAN_IFACE -s $LAN -d 172.16.0.0/12 -j RETURN 2>/dev/null || true
    iptables -t nat -D POSTROUTING -o $WAN_IFACE -s $LAN -d 192.168.0.0/16 -j RETURN 2>/dev/null || true
    iptables -t nat -D POSTROUTING -o $WAN_IFACE -s $LAN -j MASQUERADE 2>/dev/null || true
    # Inline-specific: FORWARD ACCEPT rules (drop on cleanup)
    iptables -D FORWARD -i $LAN_IFACE -o $WAN_IFACE -j ACCEPT 2>/dev/null || true
    iptables -D FORWARD -i $LAN_IFACE -o $TUN -j ACCEPT 2>/dev/null || true
    iptables -D FORWARD -i $WAN_IFACE -o $LAN_IFACE -m conntrack --ctstate RELATED,ESTABLISHED -j ACCEPT 2>/dev/null || true
    iptables -D FORWARD -i $TUN -o $LAN_IFACE -m conntrack --ctstate RELATED,ESTABLISHED -j ACCEPT 2>/dev/null || true
    # Inline-specific: MSS clamp on forwarded traffic via WAN
    iptables -t mangle -D FORWARD -o $WAN_IFACE -p tcp --tcp-flags SYN,RST SYN -j TCPMSS --clamp-mss-to-pmtu 2>/dev/null || true
}

load_ru_nets() {
    curl -sf "$RU_URL" -o "${RU_FILE}.new" && mv "${RU_FILE}.new" "$RU_FILE" || true
    if [ ! -f "$RU_FILE" ]; then
        echo "WARNING: no RU nets file, all traffic goes through tunnel"
        return 1
    fi

    # Batch load via ipset restore (8500+ entries in ~1s vs ~47s with individual adds)
    {
        echo "create ${RU_SET}-new hash:net maxelem 16384"
        sed '/^$/d; s/^/add '"${RU_SET}-new"' /' "$RU_FILE"
    } | ipset restore -!
    ipset create "$RU_SET" hash:net maxelem 16384 2>/dev/null || true
    ipset swap "${RU_SET}-new" "$RU_SET"
    ipset destroy "${RU_SET}-new"
    COUNT=$(ipset list "$RU_SET" -t | grep "Number of entries" | awk '{print $NF}')
    echo "Loaded $COUNT RU subnets"
}

setup() {
    # Wait for TUN to be created and configured by gateway
    # (MQTT handshake + TUN IP setup takes a few seconds)
    echo "Waiting for $TUN with IP..."
    for i in $(seq 1 30); do
        ip addr show "$TUN" 2>/dev/null | grep -q "inet " && break
        sleep 1
    done
    if ! ip addr show "$TUN" 2>/dev/null | grep -q "inet "; then
        echo "ERROR: $TUN not configured after 30s"
        exit 1
    fi
    echo "$TUN is ready"

    # Auto-detect TUN gateway and subnet from interface (supports auto-select exit)
    # Gateway = .1 of TUN IP (e.g. 10.9.1.2/24 → 10.9.1.1, matches exit's tun-ip .1)
    TUN_IP=$(ip -4 addr show "$TUN" | awk '/inet / {print $2}')
    TUN_GW=$(echo "$TUN_IP" | sed 's|\.[0-9]*/.*|.1|')
    TUN_SUBNET=$(ip -4 route show dev "$TUN" proto kernel | awk '{print $1; exit}')
    if [ -z "$TUN_GW" ] || [ -z "$TUN_SUBNET" ]; then
        echo "ERROR: Cannot determine TUN_GW or TUN_SUBNET from $TUN"
        exit 1
    fi
    echo "TUN gateway=$TUN_GW subnet=$TUN_SUBNET"

    sysctl -q -w net.ipv4.ip_forward=1

    # Disable ICMP redirects (gateway must not redirect LAN clients)
    sysctl -q -w net.ipv4.conf.all.send_redirects=0
    sysctl -q -w net.ipv4.conf.$WAN_IFACE.send_redirects=0
    sysctl -q -w net.ipv4.conf.$TUN.send_redirects=0
    sysctl -q -w net.ipv4.conf.all.accept_redirects=0

    # Loose reverse path filter for TUN
    sysctl -q -w net.ipv4.conf.$TUN.rp_filter=0
    sysctl -q -w net.ipv4.conf.all.rp_filter=0

    # Socket buffers for VPN throughput
    sysctl -q -w net.core.rmem_max=16777216
    sysctl -q -w net.core.wmem_max=16777216
    sysctl -q -w net.core.rmem_default=1048576
    sysctl -q -w net.core.wmem_default=1048576
    sysctl -q -w net.ipv4.tcp_rmem="4096 1048576 16777216"
    sysctl -q -w net.ipv4.tcp_wmem="4096 1048576 16777216"
    sysctl -q -w net.core.netdev_max_backlog=10000

    # Increase TUN queue length (default 500 causes TX drops at burst)
    ip link set dev $TUN txqueuelen 2000

    # RPS/RFS: distribute packet processing across all CPU cores
    # Without this, all NET_RX softirqs go to CPU0 (hardware IRQ affinity)
    # Measured improvement: CPU busy 75%→60% at same throughput (~88 Mbps)
    sysctl -q -w net.core.rps_sock_flow_entries=4096
    # Inline-specific defensive: pppoe0 may not expose sysfs queues; guard each path.
    if [ -d /sys/class/net/$WAN_IFACE/queues/rx-0 ]; then
        echo f    > /sys/class/net/$WAN_IFACE/queues/rx-0/rps_cpus    || true
        echo 2048 > /sys/class/net/$WAN_IFACE/queues/rx-0/rps_flow_cnt || true
    else
        echo "Skipping RPS for $WAN_IFACE (no sysfs queue, likely PPPoE)"
    fi
    if [ -d /sys/class/net/$TUN/queues/rx-0 ]; then
        echo f    > /sys/class/net/$TUN/queues/rx-0/rps_cpus    || true
        echo 2048 > /sys/class/net/$TUN/queues/rx-0/rps_flow_cnt || true
    else
        echo "Skipping RPS for $TUN (no sysfs queue)"
    fi

    ip rule add fwmark $MARK lookup $TABLE
    ip route add default via $TUN_GW dev $TUN table $TABLE

    load_ru_nets || true

    iptables -t mangle -N VTX_FORWARD
    iptables -t mangle -A PREROUTING -j VTX_FORWARD

    # Skip: private networks
    iptables -t mangle -A VTX_FORWARD -d 10.0.0.0/8 -j RETURN
    iptables -t mangle -A VTX_FORWARD -d 172.16.0.0/12 -j RETURN
    iptables -t mangle -A VTX_FORWARD -d 192.168.0.0/16 -j RETURN

    # Skip: loopback, link-local, multicast, broadcast
    iptables -t mangle -A VTX_FORWARD -d 127.0.0.0/8 -j RETURN
    iptables -t mangle -A VTX_FORWARD -d 169.254.0.0/16 -j RETURN
    iptables -t mangle -A VTX_FORWARD -d 224.0.0.0/4 -j RETURN
    iptables -t mangle -A VTX_FORWARD -d 255.255.255.255/32 -j RETURN

    # Skip: traffic arriving from TUN (prevent re-marking)
    iptables -t mangle -A VTX_FORWARD -i $TUN -j RETURN

    # Skip: broker IPs (comma-separated VTX_BROKER)
    for ip in "${BROKER_IPS[@]}"; do
        iptables -t mangle -A VTX_FORWARD -d "$ip" -j RETURN
    done

    # Skip: own TUN traffic
    iptables -t mangle -A VTX_FORWARD -s $TUN_SUBNET -j RETURN

    # Skip: RU destinations -> direct (split routing)
    if ipset list $RU_SET >/dev/null 2>&1; then
        iptables -t mangle -A VTX_FORWARD -m set --match-set $RU_SET dst -j RETURN
    fi

    # Mark everything else -> tunnel
    iptables -t mangle -A VTX_FORWARD -j MARK --set-mark $MARK

    # MASQUERADE direct (RU) traffic so upstream sees src=R3S (vpn-exclude)
    # Skip private destinations — they go direct on L2, no MASQUERADE needed
    iptables -t nat -A POSTROUTING -o $WAN_IFACE -s $LAN -d 10.0.0.0/8 -j RETURN
    iptables -t nat -A POSTROUTING -o $WAN_IFACE -s $LAN -d 172.16.0.0/12 -j RETURN
    iptables -t nat -A POSTROUTING -o $WAN_IFACE -s $LAN -d 192.168.0.0/16 -j RETURN
    iptables -t nat -A POSTROUTING -o $WAN_IFACE -s $LAN -j MASQUERADE

    # Inline-specific edit #2 — MSS clamp for forwarded TCP on WAN.
    # Required because pppoe0 MTU = 1492 (vs internet 1500), and ICMP "frag
    # needed" is often filtered by RU ISPs. Without MSS clamp, downstream
    # devices send full-size segments that get black-holed on RU→exit path.
    # This is FORWARD-chain (forwarded traffic); wan-up.sh handles POSTROUTING
    # for R3S-self traffic separately.
    iptables -t mangle -A FORWARD -o $WAN_IFACE -p tcp --tcp-flags SYN,RST SYN \
        -j TCPMSS --clamp-mss-to-pmtu

    # Inline-specific edit #3 — FORWARD ACCEPT rules.
    # Mikrotik-mode R3S did not need these because it forwarded only its own
    # packets and FORWARD default-policy ACCEPT was inherited from Ubuntu base.
    # In inline-mode R3S forwards packets originating on eth1 (the downstream
    # router), so we explicitly accept the four direction pairs we care about:
    # LAN→WAN, LAN→TUN, and return paths via conntrack.
    # Explicit ACCEPT rules survive any future FORWARD default DROP policy.
    iptables -A FORWARD -i $LAN_IFACE -o $WAN_IFACE -j ACCEPT
    iptables -A FORWARD -i $LAN_IFACE -o $TUN -j ACCEPT
    iptables -A FORWARD -i $WAN_IFACE -o $LAN_IFACE -m conntrack --ctstate RELATED,ESTABLISHED -j ACCEPT
    iptables -A FORWARD -i $TUN -o $LAN_IFACE -m conntrack --ctstate RELATED,ESTABLISHED -j ACCEPT
}

update() {
    load_ru_nets
}

case "${1:-setup}" in
    setup) cleanup; setup; echo "Split routing enabled: RU direct, rest via tunnel";;
    cleanup) cleanup; echo "Transparent proxy disabled";;
    update) update;;
esac

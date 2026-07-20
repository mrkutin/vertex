#!/bin/bash
set -e

# All site-specific values come from environment variables.
# Set them in /etc/vertex.env and load via systemd EnvironmentFile.
LAN=${VTX_LAN_SUBNET:?"VTX_LAN_SUBNET not set"}
WAN_IFACE=${VTX_WAN_IFACE:?"VTX_WAN_IFACE not set"}

TUN=${VTX_TUN_IFACE:-tun0}
TABLE=100
MARK=1
RU_URL="https://www.ipdeny.com/ipblocks/data/aggregated/ru-aggregated.zone"
RU_SET="ru-nets"
RU_FILE="/var/lib/vtx-ru-nets.zone"

VERTEX_YAML=${VTX_YAML_PATH:-/etc/vertex.yaml}
DISCOVERY_CACHE=${VTX_CACHE_PATH:-/etc/vertex/discovery-cache.json}

# resolve_broker_ips prints one IPv4 per line. Source order:
#   1. VTX_BROKER env  (explicit override; DNS skipped)
#   2. SRV _mqtt._tcp.<domain> → A   (canonical, matches vtx-gateway)
#   3. discovery-cache.json brokers[].url → A  (gateway-written, fresh)
#   4. /etc/vertex.yaml brokers: → A  (bootstrap fallback)
# Hard-fails if nothing resolved.
# Note: yaml fallback expects block-list form (`brokers:` on its own line, then
# `- mqtts://...` entries). Inline flow form `brokers: [a, b]` is not supported.
# IPv6 (AAAA) is intentionally ignored; warns to stderr if AAAA exists for a host —
# means broker has dual-stack and v6 traffic will loop back through TUN.
# TODO: when _vtx-backup._tcp.<domain> goes live in DNS, add a second SRV lookup
#       on the backup domain between step 2 and 3 (chain-of-trust fallback).
_resolve_hosts_to_ips() {
    while read -r h; do
        [ -z "$h" ] && continue
        local v6
        v6=$(dig +short +timeout=3 +tries=2 AAAA "$h" 2>/dev/null | head -1 || true)
        [ -n "$v6" ] && echo "[vtx-proxy] WARN: ignoring IPv6 for $h ($v6) — broker v6 traffic will loop" >&2
        dig +short +timeout=3 +tries=2 A "$h" 2>/dev/null | grep -E '^[0-9.]+$' || true
    done | sort -u
}

resolve_broker_ips() {
    # 1. env override (accepts space or comma separator)
    if [ -n "${VTX_BROKER:-}" ]; then
        echo "[vtx-proxy] broker IPs from: env-override (VTX_BROKER)" >&2
        echo "$VTX_BROKER" | tr ', ' '\n' | grep -E '^[0-9.]+$' | sort -u
        return 0
    fi

    if ! command -v dig >/dev/null 2>&1; then
        echo "ERROR: dig not found. Install: apt install dnsutils" >&2
        exit 1
    fi

    local domain="${VTX_DOMAIN:-}"
    if [ -z "$domain" ] && [ -r "$VERTEX_YAML" ]; then
        domain=$(awk '/^domain:[[:space:]]/ {print $2; exit}' "$VERTEX_YAML")
    fi
    domain="${domain:-vertices.ru}"

    # 2. SRV
    local targets ips
    targets=$(dig +short +timeout=3 +tries=2 SRV "_mqtt._tcp.$domain" 2>/dev/null \
                | awk '{print $4}' | sed 's/\.$//' | grep -v '^$' | sort -u || true)
    if [ -n "$targets" ]; then
        ips=$(echo "$targets" | _resolve_hosts_to_ips)
        if [ -n "$ips" ]; then
            echo "[vtx-proxy] broker IPs from: SRV _mqtt._tcp.$domain" >&2
            echo "$ips"
            return 0
        fi
    fi

    # 3. discovery cache (json written by vtx-gateway)
    if [ -r "$DISCOVERY_CACHE" ]; then
        local hosts
        hosts=$(grep -oE '"url"[[:space:]]*:[[:space:]]*"[^"]+"' "$DISCOVERY_CACHE" \
                  | sed -E 's/.*"(mqtts?|wss?):\/\/([^:/"]+).*/\2/' \
                  | sort -u || true)
        if [ -n "$hosts" ]; then
            ips=$(echo "$hosts" | _resolve_hosts_to_ips)
            if [ -n "$ips" ]; then
                echo "[vtx-proxy] broker IPs from: cache ($DISCOVERY_CACHE)" >&2
                echo "$ips"
                return 0
            fi
        fi
    fi

    # 4. yaml bootstrap (block-list form only; see header note)
    if [ -r "$VERTEX_YAML" ]; then
        local hosts
        hosts=$(awk '
            /^brokers:[[:space:]]*$/ { in_blk=1; next }
            in_blk && /^[^[:space:]-]/ { in_blk=0 }
            in_blk { print }
        ' "$VERTEX_YAML" \
          | grep -oE '(mqtts?|wss?)://[^:/"[:space:]]+' \
          | sed -E 's|.*://||' | sort -u || true)
        if [ -n "$hosts" ]; then
            ips=$(echo "$hosts" | _resolve_hosts_to_ips)
            if [ -n "$ips" ]; then
                echo "[vtx-proxy] broker IPs from: yaml-bootstrap ($VERTEX_YAML)" >&2
                echo "$ips"
                return 0
            fi
        fi
    fi

    echo "ERROR: cannot resolve broker IPs (DNS down, no cache, no yaml brokers)" >&2
    exit 1
}

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
    echo f > /sys/class/net/$WAN_IFACE/queues/rx-0/rps_cpus
    echo 2048 > /sys/class/net/$WAN_IFACE/queues/rx-0/rps_flow_cnt
    echo f > /sys/class/net/$TUN/queues/rx-0/rps_cpus
    echo 2048 > /sys/class/net/$TUN/queues/rx-0/rps_flow_cnt

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

    # Skip: broker IPs (resolved via SRV / cache / yaml / env override)
    mapfile -t BROKER_IPS < <(resolve_broker_ips)
    if [ ${#BROKER_IPS[@]} -eq 0 ]; then
        echo "ERROR: no broker IPs to bypass" >&2
        exit 1
    fi
    for ip in "${BROKER_IPS[@]}"; do
        iptables -t mangle -A VTX_FORWARD -d "$ip" -j RETURN
    done
    echo "Broker bypass: ${BROKER_IPS[*]}"

    # Skip: own TUN traffic
    iptables -t mangle -A VTX_FORWARD -s $TUN_SUBNET -j RETURN

    # Skip: RU destinations -> direct (split routing)
    if ipset list $RU_SET >/dev/null 2>&1; then
        iptables -t mangle -A VTX_FORWARD -m set --match-set $RU_SET dst -j RETURN
    fi

    # Mark everything else -> tunnel
    iptables -t mangle -A VTX_FORWARD -j MARK --set-mark $MARK

    # MASQUERADE direct (RU) traffic so Mikrotik sees src=R3S (vpn-exclude)
    # Skip private destinations — they go direct on L2, no MASQUERADE needed
    iptables -t nat -A POSTROUTING -o $WAN_IFACE -s $LAN -d 10.0.0.0/8 -j RETURN
    iptables -t nat -A POSTROUTING -o $WAN_IFACE -s $LAN -d 172.16.0.0/12 -j RETURN
    iptables -t nat -A POSTROUTING -o $WAN_IFACE -s $LAN -d 192.168.0.0/16 -j RETURN
    iptables -t nat -A POSTROUTING -o $WAN_IFACE -s $LAN -j MASQUERADE
}

update() {
    load_ru_nets
}

case "${1:-setup}" in
    setup) cleanup; setup; echo "Split routing enabled: RU direct, rest via tunnel";;
    cleanup) cleanup; echo "Transparent proxy disabled";;
    update) update;;
    resolve) resolve_broker_ips;;
esac

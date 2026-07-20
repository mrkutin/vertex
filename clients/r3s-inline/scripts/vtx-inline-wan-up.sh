#!/bin/bash
# Vertex R3S-inline WAN bring-up.
# Called by vtx-inline-wan.service ExecStart. Reads VTX_WAN_MODE from
# /etc/vertex-inline/inline.env (loaded by systemd via EnvironmentFile=) and
# brings up either pppoe0 (PPPoE) or eth0 (Ethernet+DHCP). Blocks until WAN
# has a default route (timeout 60s).
#
# Env is provided by systemd EnvironmentFile=; we don't `.`-source the file
# ourselves to avoid `set -eu` hard-failing on malformed entries — systemd's
# parsing is the single source of truth.
set -eu -o pipefail

: "${VTX_WAN_MODE:?VTX_WAN_MODE not set (EnvironmentFile= broken?)}"

wait_for_default_route() {
    local dev="$1"
    for _ in $(seq 1 60); do
        ip -4 route show default | grep -q "dev $dev" && return 0
        sleep 1
    done
    return 1
}

# Idempotent MSS clamp for self-traffic on PPPoE (apt/curl/vtx-gateway → broker).
# Uses mangle.OUTPUT chain — fires only for locally-originated packets, so it
# doesn't double-clamp forwarded traffic (which vtx-inline-proxy.sh clamps via
# mangle.FORWARD). Clean separation: OUTPUT = self, FORWARD = downstream.
mss_clamp_pppoe_apply() {
    iptables -t mangle -C OUTPUT -o pppoe0 -p tcp --tcp-flags SYN,RST SYN \
        -j TCPMSS --clamp-mss-to-pmtu 2>/dev/null || \
    iptables -t mangle -A OUTPUT -o pppoe0 -p tcp --tcp-flags SYN,RST SYN \
        -j TCPMSS --clamp-mss-to-pmtu
}

# Clean up the OTHER mode in case user switches modes between runs.
cleanup_other_mode() {
    case "$1" in
        pppoe)
            # Stale DHCP drop-in from previous dhcp-mode run, if any.
            if [ -e /etc/systemd/network/10-vtx-wan.network ]; then
                rm -f /etc/systemd/network/10-vtx-wan.network
                networkctl reload 2>/dev/null || true
                networkctl reconfigure eth0 2>/dev/null || true
            fi
            ;;
        dhcp)
            # Stale pppd / pppoe0 session, if any.
            poff vtx-isp 2>/dev/null || true
            iptables -t mangle -D OUTPUT -o pppoe0 -p tcp --tcp-flags SYN,RST SYN \
                -j TCPMSS --clamp-mss-to-pmtu 2>/dev/null || true
            ;;
    esac
}

case "$VTX_WAN_MODE" in
    pppoe)
        cleanup_other_mode pppoe
        [ -r /etc/ppp/peers/vtx-isp ] || {
            echo "ERROR: /etc/ppp/peers/vtx-isp missing (bootstrap incomplete?)"
            exit 1
        }
        echo "Starting PPPoE on eth0 (peer=vtx-isp)..."
        # `pon` is part of the ppp package; it daemonizes pppd. `persist` in the peer
        # config makes pppd auto-redial on link drop.
        pon vtx-isp
        if ! wait_for_default_route pppoe0; then
            echo "ERROR: pppoe0 default route did not appear within 60s"
            poff vtx-isp 2>/dev/null || true
            exit 1
        fi
        mss_clamp_pppoe_apply
        echo "PPPoE up: $(ip -4 addr show pppoe0 | awk '/inet /{print $2}')"
        ;;
    dhcp)
        cleanup_other_mode dhcp
        echo "Starting Ethernet+DHCP on eth0..."
        install -m 0644 /etc/vertex-inline/wan-dhcp.network \
            /etc/systemd/network/10-vtx-wan.network
        networkctl reload
        networkctl reconfigure eth0
        if ! wait_for_default_route eth0; then
            echo "ERROR: eth0 default route did not appear within 60s"
            exit 1
        fi
        # No MSS clamp on eth0: MTU 1500, kernel default MSS is fine.
        echo "DHCP up: $(ip -4 addr show eth0 | awk '/inet /{print $2}')"
        ;;
    *)
        echo "ERROR: unknown VTX_WAN_MODE=$VTX_WAN_MODE (expected: pppoe|dhcp)"
        exit 1
        ;;
esac

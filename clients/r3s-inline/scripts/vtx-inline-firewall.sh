#!/bin/bash
# Vertex R3S-inline INPUT firewall + sysctl hardening.
# Locks down WAN-side: only conntrack-return and rate-limited ICMP echo allowed.
# eth1 (LAN-side) trusted — bootstrap web-UI + SSH for admin lives there.
# Also disables IPv6 globally (R3S is single-purpose appliance; v6 would expose
# sshd on :: if ISP gives SLAAC) and turns on net.ipv4.ip_forward (we removed
# it from networkd-lan.network — it's a global toggle, not per-interface).
set -eu

: "${VTX_WAN_IFACE:?VTX_WAN_IFACE not set}"
: "${VTX_LAN_IFACE:?VTX_LAN_IFACE not set}"

SYSCTL_PERSIST=/etc/sysctl.d/99-vtx-inline.conf

apply() {
    # Sysctls (apply immediately + persist for next boot).
    # This file is owned by vtx-inline-firewall — install.sh must not also
    # write to it (single source of truth).
    sysctl -q -w net.ipv4.ip_forward=1
    sysctl -q -w net.ipv6.conf.all.disable_ipv6=1
    sysctl -q -w net.ipv6.conf.default.disable_ipv6=1
    cat > "$SYSCTL_PERSIST" <<EOF
# Managed by vtx-inline-firewall — do not edit by hand.
net.ipv4.ip_forward=1
net.ipv6.conf.all.disable_ipv6=1
net.ipv6.conf.default.disable_ipv6=1
EOF

    iptables -N VTX_INPUT 2>/dev/null || iptables -F VTX_INPUT
    # Make sure our chain is first in INPUT so DROPs land before any other rule.
    iptables -C INPUT -j VTX_INPUT 2>/dev/null || iptables -I INPUT 1 -j VTX_INPUT

    # 1. lo — always trusted
    iptables -A VTX_INPUT -i lo -j ACCEPT

    # 2. Return traffic (conntrack) — apt update via WAN, SRV lookups, etc.
    iptables -A VTX_INPUT -m conntrack --ctstate RELATED,ESTABLISHED -j ACCEPT

    # 3. LAN-side trusted (bootstrap UI, SSH for admin)
    iptables -A VTX_INPUT -i "$VTX_LAN_IFACE" -j ACCEPT

    # 4. ICMP echo on WAN — rate-limited to keep ping diagnostics but prevent floods
    iptables -A VTX_INPUT -i "$VTX_WAN_IFACE" -p icmp --icmp-type echo-request \
        -m limit --limit 10/sec --limit-burst 20 -j ACCEPT

    # 5. Final catch-all DROP — covers $VTX_WAN_IFACE AND anything else (e.g.
    # eth0 in PPPoE mode if ISP weirdly sends DHCP on the raw wire). This is
    # the load-bearing rule — explicit, no `-i` filter.
    iptables -A VTX_INPUT -j DROP

    echo "Firewall applied: WAN=$VTX_WAN_IFACE LAN=$VTX_LAN_IFACE (admin ports closed on WAN, IPv6 disabled)"
}

flush() {
    iptables -D INPUT -j VTX_INPUT 2>/dev/null || true
    iptables -F VTX_INPUT 2>/dev/null || true
    iptables -X VTX_INPUT 2>/dev/null || true
    rm -f "$SYSCTL_PERSIST"
    # Don't reverse sysctls — they're harmless to leave (ip_forward may be
    # needed for other tools; v6-disable persists until reboot, then loads
    # from defaults). Persistence file removed.
    echo "Firewall flushed"
}

case "${1:-apply}" in
    apply) flush; apply ;;
    flush) flush ;;
    *) echo "Usage: $0 apply|flush"; exit 1 ;;
esac

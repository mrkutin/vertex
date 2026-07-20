#!/bin/bash
# Vertex R3S-inline WAN tear-down. Called by vtx-inline-wan.service ExecStop.
#
# Best-effort: tears down BOTH modes regardless of VTX_WAN_MODE — we don't
# trust env on stop (file may have been deleted, malformed, or mode just changed).
# This script is for unit-stop / reinstall flow, NOT steady-state — on next
# `systemctl start vtx-inline-wan`, wan-up.sh brings the chosen mode up fresh.
set -u

# Tear down PPPoE (idempotent: `poff` returns 0 if no session).
poff vtx-isp 2>/dev/null || true

# Drop PPPoE MSS-clamp rule (idempotent). OUTPUT chain — see wan-up.sh.
iptables -t mangle -D OUTPUT -o pppoe0 -p tcp --tcp-flags SYN,RST SYN \
    -j TCPMSS --clamp-mss-to-pmtu 2>/dev/null || true

# Tear down DHCP drop-in (idempotent).
rm -f /etc/systemd/network/10-vtx-wan.network
networkctl reload 2>/dev/null || true
networkctl reconfigure eth0 2>/dev/null || true

exit 0

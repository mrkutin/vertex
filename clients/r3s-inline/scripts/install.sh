#!/bin/bash
# Vertex R3S-inline installer. Run as root on a fresh Ubuntu 24.04 ARM64 R3S.
#
# Sets up the box for the inline variant: dependencies, systemd-networkd as
# the network backend (replaces netplan), unit files, scripts, configs,
# binaries, and ip-up/down hooks. After this completes the box is ready for
# first boot — when user plugs in and powers on, vtx-inline-setup serves the
# bootstrap web-UI on http://192.168.42.1/.
#
# Expects the caller to have placed the build payload at the current working
# directory (the dir that contains this script). The Makefile `build-inline`
# target arranges this layout in dist/r3s-inline/payload/.
set -euo pipefail

[ "$(id -u)" = "0" ] || { echo "ERROR: run as root"; exit 1; }
[ -f vtx-gateway ] || { echo "ERROR: vtx-gateway binary missing (run make build-inline)"; exit 1; }
[ -f vtx-inline-setup ] || { echo "ERROR: vtx-inline-setup binary missing"; exit 1; }

# ── Hard guard: refuse to clobber the production Mikrotik-R3S installation ─
# Production R3S has /etc/vertex.yaml and vtx-gateway.service enabled. If both
# present, this box runs the Mikrotik variant — installing inline on top would
# wreck it (different systemd unit, different config path, different topology).
if [ -f /etc/vertex.yaml ] && systemctl is-enabled --quiet vtx-gateway.service 2>/dev/null; then
    cat >&2 <<'EOF'
ERROR: production vtx-gateway is installed on this box (Mikrotik R3S).
Inline-R3S is mutually exclusive — running both would corrupt routing.

If you really want to convert this box to inline-mode:
  1. systemctl disable --now vtx-gateway.service
  2. /usr/local/bin/vtx-proxy.sh cleanup  (if installed)
  3. mv /etc/vertex.yaml /etc/vertex.yaml.bak
  4. re-run this install.sh
EOF
    exit 1
fi

echo "==> Installing Vertex R3S-inline"

# 1. Packages
# We do NOT install iptables-persistent: vtx-inline-router.service re-applies
# routing/NAT/MARK on every start via ExecStartPost=vtx-inline-proxy.sh setup,
# and vtx-inline-firewall.service re-applies INPUT rules. Persistence file
# would only fight us on upgrade.
echo "==> apt update + install dependencies"
DEBIAN_FRONTEND=noninteractive apt-get update -q
DEBIAN_FRONTEND=noninteractive apt-get install -yq --no-install-recommends \
    dnsmasq ipset iptables ppp pppoe curl ca-certificates

# 2. Conflicting services — mask BEFORE first invocation so apt-upgrade
#    postinst hooks cannot re-enable them. `mask --now` stops AND masks atomically.
# systemd-resolved listens on 127.0.0.53:53 and rewrites /etc/resolv.conf. R3S
# is DNS-neutral (dnsmasq uses port=0, no local resolver).
echo "==> Mask systemd-resolved (DNS-neutral)"
systemctl mask --now systemd-resolved.service 2>/dev/null || true

if [ -L /etc/resolv.conf ]; then
    rm -f /etc/resolv.conf
fi
cat > /etc/resolv.conf <<'EOF'
# Managed by Vertex R3S-inline. R3S itself talks to upstream via 1.1.1.1 only
# for local utilities (apt update, etc.). Downstream clients receive 1.1.1.1
# directly via dnsmasq DHCP option 6 — R3S does not proxy DNS.
nameserver 1.1.1.1
nameserver 1.0.0.1
EOF
if ! chattr +i /etc/resolv.conf 2>/dev/null; then
    echo "WARN: chattr +i /etc/resolv.conf failed (fs may not support immutable bit) — apt postinst may rewrite DNS"
fi

# 3. Disable cloud-init network management.
# cloud-init's cc_netplan module REGENERATES /etc/netplan/50-cloud-init.yaml on
# every boot if not explicitly disabled. Just moving /etc/netplan away is not
# enough — cloud-init recreates the dir + yaml on next boot and races with our
# /etc/systemd/network/20-vtx-lan.network.
echo "==> Disable cloud-init network regeneration"
mkdir -p /etc/cloud/cloud.cfg.d
cat > /etc/cloud/cloud.cfg.d/99-vtx-inline-disable-network.cfg <<'EOF'
# Managed by Vertex R3S-inline installer.
network: {config: disabled}
EOF

# 4. Replace netplan with systemd-networkd.
# After cloud-init network is disabled, any existing netplan yaml is dormant
# (no auto-apply on boot), but to avoid manual `netplan apply` confusion we
# move it aside.
echo "==> Move netplan aside (using systemd-networkd directly)"
if [ -d /etc/netplan ] && ls /etc/netplan/*.yaml >/dev/null 2>&1; then
    mv /etc/netplan /etc/netplan.disabled
    mkdir -p /etc/netplan
fi
systemctl enable systemd-networkd

# 5. Disable packaged dnsmasq auto-start. We can't `mask` it: vtx-inline-lan
#    has `Wants=dnsmasq.service` to pull a fresh invocation with our config,
#    and Wants= against a masked unit is a no-op. `disable --now` just removes
#    the WantedBy=multi-user.target link. Apt-upgrade may re-enable, but
#    boot-time auto-start remains gated by our enable-state.
systemctl disable --now dnsmasq.service 2>/dev/null || true

# 5. Install binaries
echo "==> Install binaries to /usr/local/bin/"
install -m 0755 vtx-gateway          /usr/local/bin/vtx-gateway
install -m 0755 vtx-inline-setup     /usr/local/bin/vtx-inline-setup
install -m 0755 scripts/vtx-inline-proxy.sh    /usr/local/bin/vtx-inline-proxy.sh
install -m 0755 scripts/vtx-inline-wan-up.sh   /usr/local/bin/vtx-inline-wan-up.sh
install -m 0755 scripts/vtx-inline-wan-down.sh /usr/local/bin/vtx-inline-wan-down.sh
install -m 0755 scripts/vtx-inline-firewall.sh /usr/local/bin/vtx-inline-firewall.sh

# 6. ppp ip-up/ip-down hooks
echo "==> Install pppd hooks"
install -m 0755 scripts/ip-up-vtx-inline   /etc/ppp/ip-up.d/00-vtx-inline
install -m 0755 scripts/ip-down-vtx-inline /etc/ppp/ip-down.d/00-vtx-inline

# 7. Configs (canonical copies live under /etc/vertex-inline/, our central dir)
echo "==> Install configs"
mkdir -p /etc/vertex-inline
install -m 0644 configs/dnsmasq.inline.conf /etc/vertex-inline/dnsmasq.inline.conf
install -m 0644 configs/networkd-wan-dhcp.network /etc/vertex-inline/wan-dhcp.network
# Note: the PPPoE template is embedded into vtx-inline-setup (cmd/setup/templates/).
# We do not deploy a separate template file — bootstrap renders from embed.

# networkd: drop /etc/systemd/network/ profile for eth1 (LAN).
install -m 0644 configs/networkd-lan.network /etc/systemd/network/20-vtx-lan.network

# dnsmasq picks up our config via symlink. Symlink (not copy) so reapply of
# vtx-inline-lan.service uses the latest /etc/vertex-inline/ source.
mkdir -p /etc/dnsmasq.d
ln -sf /etc/vertex-inline/dnsmasq.inline.conf /etc/dnsmasq.d/vtx-inline.conf

# 8. systemd units
echo "==> Install systemd units"
install -m 0644 systemd/vtx-inline-lan.service       /etc/systemd/system/
install -m 0644 systemd/vtx-inline-setup.service     /etc/systemd/system/
install -m 0644 systemd/vtx-inline-wan.service       /etc/systemd/system/
install -m 0644 systemd/vtx-inline-firewall.service  /etc/systemd/system/
install -m 0644 systemd/vtx-inline-router.service    /etc/systemd/system/

# 9. Reload + enable always-on units. setup is enabled too — it self-disables
# via sentinel after successful bootstrap (ConditionPathExists guards next boot).
# wan/firewall/router are enabled but won't start without .setup-done.
systemctl daemon-reload
systemctl enable vtx-inline-lan.service
systemctl enable vtx-inline-setup.service
systemctl enable vtx-inline-wan.service
systemctl enable vtx-inline-firewall.service
systemctl enable vtx-inline-router.service

# 10. Log + sanity prints
mkdir -p /var/log/vtx-inline
{
    echo "=== Vertex R3S-inline installed at $(date -u +%FT%TZ) ==="
    echo "WAN type: chosen at first-boot via web-UI"
    echo "LAN: 192.168.42.1/24 on eth1, dnsmasq DHCP .50–.200"
    echo "Setup URL on first boot: http://192.168.42.1/"
    echo "Units enabled: vtx-inline-{lan,setup,wan,firewall,router}.service"
} > /var/log/vtx-inline/install.log

echo "==> Install complete"
cat <<EOF

Next steps:
  1. Power off:    poweroff
  2. Insert SD card into the destination R3S, plug ISP cable to eth0 and
     user's router WAN to eth1, then power on.
  3. From a device behind the user's router, open http://192.168.42.1/
     and fill the bootstrap form. After ~60s VPN is up.

Install log: /var/log/vtx-inline/install.log
EOF

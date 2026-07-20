#!/usr/bin/env bash
# restore-broker.sh — re-create a Mosquitto broker from a backup made by
# backup-broker.sh.
#
# Usage: scripts/restore-broker.sh <broker-id> <ssh-host> [backup-dir]
#
# Default backup-dir: ~/Backups/vertex/<broker-id>-broker/latest
#
# Expectations on the target host (SSH alias <ssh-host>):
#   - passwordless sudo (or interactive sudo will prompt)
#   - Debian/Ubuntu — uses apt for package install
#   - DNS A record for <broker-id>'s FQDN already points at this host
#     (required for Let's Encrypt re-issue; check with `dig +short A <fqdn>`)
#
# Cert strategy: by default re-issue via certbot (clean 90-day cycle with
# auto-renew). Pass --reuse-cert to install the snapshot cert as-is (faster,
# but cert expires on the backup's original schedule and certbot needs to
# be told about it for future renewal).
#
# The script does NOT touch DNS SRV records — see RESTORE.md in the backup
# directory for the manual SRV/A checklist.

set -euo pipefail

REUSE_CERT=0
ARGS=()
for arg in "$@"; do
  case "$arg" in
    --reuse-cert) REUSE_CERT=1 ;;
    *) ARGS+=("$arg") ;;
  esac
done
set -- "${ARGS[@]}"

if [ $# -lt 2 ]; then
  echo "Usage: $0 [--reuse-cert] <broker-id> <ssh-host> [backup-dir]" >&2
  exit 1
fi

BROKER_ID="$1"
SSH_HOST="$2"
BACKUP_DIR="${3:-${HOME}/Backups/vertex/${BROKER_ID}-broker/latest}"

REAL_BACKUP="$(cd "${BACKUP_DIR}" && pwd -P)"
HOST_FQDN="${BROKER_HOST_FQDN:-mqtt-${BROKER_ID}.vertices.ru}"

# Sanity: the backup's renewal.conf must reference the same FQDN.
if [ ! -f "${REAL_BACKUP}/letsencrypt/renewal/${HOST_FQDN}.conf" ]; then
  echo "ERROR: ${REAL_BACKUP} does not contain letsencrypt/renewal/${HOST_FQDN}.conf" >&2
  echo "       Set BROKER_HOST_FQDN=<fqdn> if the host differs from mqtt-${BROKER_ID}.vertices.ru" >&2
  exit 1
fi

# Sanity: target host must resolve the FQDN to itself before LE re-issue.
if [ "${REUSE_CERT}" = 0 ]; then
  TARGET_IP="$(ssh "${SSH_HOST}" 'curl -s ifconfig.me 2>/dev/null || hostname -I | awk "{print \$1}"')"
  DNS_IP="$(dig +short A "${HOST_FQDN}" | tail -1)"
  if [ -z "${DNS_IP}" ]; then
    echo "ERROR: DNS A record ${HOST_FQDN} is not set. Add it (pointing at ${TARGET_IP}) and re-run." >&2
    exit 1
  fi
  if [ "${DNS_IP}" != "${TARGET_IP}" ]; then
    echo "ERROR: DNS A ${HOST_FQDN} → ${DNS_IP} but ${SSH_HOST} reports public IP ${TARGET_IP}" >&2
    echo "       Wait for DNS propagation or pass --reuse-cert to skip LE re-issue." >&2
    exit 1
  fi
fi

echo "==> Restoring broker ${BROKER_ID} (${HOST_FQDN}) on ${SSH_HOST} from ${REAL_BACKUP}"

# Stage payload on remote
echo "==> Staging payload"
ssh "${SSH_HOST}" 'rm -rf /tmp/vtx-broker-restore && mkdir -p /tmp/vtx-broker-restore'

scp -q -r "${REAL_BACKUP}/mosquitto"   "${SSH_HOST}:/tmp/vtx-broker-restore/"
scp -q -r "${REAL_BACKUP}/letsencrypt" "${SSH_HOST}:/tmp/vtx-broker-restore/"
[ -d "${REAL_BACKUP}/systemd" ] && \
  scp -q -r "${REAL_BACKUP}/systemd" "${SSH_HOST}:/tmp/vtx-broker-restore/" || true

# Run install on remote
echo "==> Installing"
ssh "${SSH_HOST}" "sudo BROKER_ID='${BROKER_ID}' HOST_FQDN='${HOST_FQDN}' REUSE_CERT='${REUSE_CERT}' bash -s" <<'REMOTE'
set -euo pipefail
cd /tmp/vtx-broker-restore

# 1. Packages
if ! command -v mosquitto >/dev/null 2>&1 || ! command -v certbot >/dev/null 2>&1; then
  echo "    Installing mosquitto + certbot"
  apt-get update -qq
  apt-get install -y -qq mosquitto certbot
fi

# 2. TLS cert
if [ "${REUSE_CERT}" = 1 ]; then
  echo "    Installing backup cert (reuse mode)"
  install -d -m 0755 "/etc/letsencrypt/archive/${HOST_FQDN}"
  install -d -m 0755 "/etc/letsencrypt/live/${HOST_FQDN}"
  install -d -m 0755 /etc/letsencrypt/renewal
  cp letsencrypt/archive/*.pem  "/etc/letsencrypt/archive/${HOST_FQDN}/"
  cp letsencrypt/renewal/${HOST_FQDN}.conf "/etc/letsencrypt/renewal/${HOST_FQDN}.conf"
  ( cd "/etc/letsencrypt/live/${HOST_FQDN}"
    for f in cert chain fullchain privkey; do
      target="$(ls "../../archive/${HOST_FQDN}/${f}"*.pem 2>/dev/null | sort -V | tail -1)"
      ln -sf "${target}" "${f}.pem"
    done
  )
else
  if [ -d "/etc/letsencrypt/live/${HOST_FQDN}" ]; then
    echo "    Existing LE cert for ${HOST_FQDN} found — using it"
  else
    echo "    Requesting fresh LE cert via standalone HTTP-01"
    systemctl stop mosquitto 2>/dev/null || true  # free port 80 if mosquitto bound
    certbot certonly --standalone --non-interactive --agree-tos \
      --register-unsafely-without-email \
      -d "${HOST_FQDN}"
  fi
fi

# 3. Mosquitto configs
echo "    Installing mosquitto configs"
install -m 0644 mosquitto/mosquitto.conf /etc/mosquitto/mosquitto.conf
install -d -m 0755 /etc/mosquitto/conf.d
install -m 0644 "mosquitto/conf.d/${BROKER_ID}.conf" "/etc/mosquitto/conf.d/${BROKER_ID}.conf" 2>/dev/null || \
  install -m 0644 mosquitto/conf.d/vertex.conf /etc/mosquitto/conf.d/vertex.conf
install -m 0640 -o root -g mosquitto mosquitto/bt_passwd /etc/mosquitto/bt_passwd
install -m 0640 -o root -g mosquitto mosquitto/bt_acl    /etc/mosquitto/bt_acl

# 4. LE permission for mosquitto group (cert + key read access)
chgrp -R mosquitto /etc/letsencrypt/live /etc/letsencrypt/archive
chmod g+rx /etc/letsencrypt/live /etc/letsencrypt/archive

# 5. Bind privilege for :443
setcap CAP_NET_BIND_SERVICE=+eip /usr/sbin/mosquitto

# 6. Systemd override (optional)
if [ -f systemd/override.conf ]; then
  install -d /etc/systemd/system/mosquitto.service.d
  install -m 0644 systemd/override.conf /etc/systemd/system/mosquitto.service.d/override.conf
fi

# 7. Reload + start
systemctl daemon-reload
systemctl enable mosquitto
systemctl restart mosquitto
sleep 3
systemctl is-active mosquitto
REMOTE

echo "==> Verifying"
ssh "${SSH_HOST}" 'sudo journalctl -u mosquitto --since "30 sec ago" --no-pager | tail -10'

cat <<EOF

==> Restore complete on ${SSH_HOST}.

Manual follow-ups (see ${REAL_BACKUP}/RESTORE.md for full detail):

  1. Verify bt_passwd / bt_acl freshness — if they're older than the
     current canonical (yc/sber may have new users since this snapshot),
     sync from a live broker:

       ssh yc 'sudo cat /etc/mosquitto/bt_passwd' \\
         | ssh ${SSH_HOST} 'sudo tee /etc/mosquitto/bt_passwd >/dev/null'
       ssh yc 'sudo cat /etc/mosquitto/bt_acl' \\
         | ssh ${SSH_HOST} 'sudo tee /etc/mosquitto/bt_acl >/dev/null'
       ssh ${SSH_HOST} 'sudo systemctl reload mosquitto'

  2. Add SRV records in vertices.ru DNS panel:

       _mqtt._tcp  SRV  <prio>     0  8883  ${HOST_FQDN}.
       _mqtt._tcp  SRV  <prio+30>  0  443   ${HOST_FQDN}.

     Pick a priority lower (better) than backup brokers, or higher if
     tertiary. Restart exits (vtx-exit on ams + sto) to pick up the
     new SRV at the next reconnect.

  3. (Optional) Mikrotik mangle skip rule for the new public IP if the
     home R3S split routing relies on it.
EOF

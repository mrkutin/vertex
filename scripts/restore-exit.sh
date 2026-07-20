#!/usr/bin/env bash
# restore-exit.sh — re-create an exit node from a backup made by backup-exit.sh.
#
# Usage: scripts/restore-exit.sh <exit-id> <ssh-host> [backup-dir]
#
# Default backup-dir: ~/Backups/vertex/<exit-id>-exit/latest
#
# Expectations on the target host (SSH alias <ssh-host>):
#   - passwordless sudo, or interactive sudo will prompt
#   - Debian/Ubuntu with iptables / systemd available
#   - User can write to /usr/local/bin, /etc, /var/lib via sudo
#
# This script does NOT touch broker MQTT users or DNS — see RESTORE.md in
# the backup directory for the manual checklist.

set -euo pipefail

if [ $# -lt 2 ]; then
  echo "Usage: $0 <exit-id> <ssh-host> [backup-dir]" >&2
  exit 1
fi

EXIT_ID="$1"
SSH_HOST="$2"
BACKUP_DIR="${3:-${HOME}/Backups/vertex/${EXIT_ID}-exit/latest}"

# Resolve symlink to absolute (so logs show real timestamp)
REAL_BACKUP="$(cd "${BACKUP_DIR}" && pwd -P)"

# Repo root — vtx-exit binary lives in dist/exit/linux-amd64/
REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"

# Sanity: backup must contain the right exit
BACKUP_ID="$(grep '^id:' "${REAL_BACKUP}/vertex.yaml" | awk '{print $2}')"
if [ "${BACKUP_ID}" != "${EXIT_ID}" ]; then
  echo "ERROR: backup id=${BACKUP_ID} does not match requested exit-id=${EXIT_ID}" >&2
  exit 1
fi

echo "==> Restoring exit ${EXIT_ID} on ${SSH_HOST} from ${REAL_BACKUP}"

# 1. Build the binary (uses current repo VERSION; respects user's branch)
if [ ! -x "${REPO_ROOT}/dist/exit/linux-amd64/vtx-exit" ]; then
  echo "==> Building vtx-exit (linux-amd64)"
  ( cd "${REPO_ROOT}" && make build-exit )
fi

# 2. Stage payload in a single tmp dir on the remote, atomic move into place
echo "==> Staging payload on ${SSH_HOST}"
ssh "${SSH_HOST}" 'rm -rf /tmp/vtx-restore && mkdir -p /tmp/vtx-restore'

scp -q "${REPO_ROOT}/dist/exit/linux-amd64/vtx-exit"  "${SSH_HOST}:/tmp/vtx-restore/vtx-exit"
scp -q "${REAL_BACKUP}/vertex.yaml"                   "${SSH_HOST}:/tmp/vtx-restore/vertex.yaml"
scp -q "${REAL_BACKUP}/vtx-devices.json"              "${SSH_HOST}:/tmp/vtx-restore/vtx-devices.json"
scp -q "${REAL_BACKUP}/vtx-exit.service"              "${SSH_HOST}:/tmp/vtx-restore/vtx-exit.service"
[ -f "${REAL_BACKUP}/bt-stats.json" ] && \
  scp -q "${REAL_BACKUP}/bt-stats.json" "${SSH_HOST}:/tmp/vtx-restore/bt-stats.json" || true

# 3. Install on remote
echo "==> Installing"
ssh "${SSH_HOST}" 'sudo bash -s' <<'REMOTE'
set -euo pipefail
cd /tmp/vtx-restore

install -m 0755 -o root -g root vtx-exit          /usr/local/bin/vtx-exit
install -m 0644 -o root -g root vertex.yaml       /etc/vertex.yaml
install -m 0644 -o root -g root vtx-exit.service  /etc/systemd/system/vtx-exit.service
install -d -m 0755 -o root -g root /var/lib
install -m 0600 -o root -g root vtx-devices.json  /var/lib/vtx-devices.json
[ -f bt-stats.json ] && install -m 0644 -o root -g root bt-stats.json /var/lib/bt-stats.json || true

systemctl daemon-reload
systemctl enable vtx-exit
systemctl restart vtx-exit
sleep 3
systemctl is-active vtx-exit
REMOTE

echo "==> Verifying"
ssh "${SSH_HOST}" 'sudo journalctl -u vtx-exit --since "30 sec ago" --no-pager | grep -E "Transport ready|Connected to broker" | tail -5'

cat <<EOF

==> Restore complete on ${SSH_HOST}.

Next steps (if this is a fresh restore — skip if exit was just bounced):

  1. Add MQTT user on brokers (only if it was removed during decommission):
       see ${REAL_BACKUP}/RESTORE.md step 3 — copy-paste the for-loop.

  2. Add DNS records in Timeweb panel:
       see ${REAL_BACKUP}/RESTORE.md step 4.

  3. Update Mikrotik skip rule for the new public IP if you do split routing.

  4. Update CLAUDE.md table of servers with the new IP.

To watch clients pick it up:
  ssh r3s 'journalctl -u vtx-gateway -f | grep "discovery"'
EOF

#!/usr/bin/env bash
# backup-exit.sh — snapshot an exit's runtime state to ~/Backups/vertex/<id>-exit/<date>/
#
# Usage: scripts/backup-exit.sh <exit-id> <ssh-host>
# Example: scripts/backup-exit.sh mtl aws
#
# Pulls:
#   /etc/vertex.yaml                          (id, user, pass, dh-private-key, country, ACL flags)
#   /var/lib/vtx-devices.json                 (TOFU device registrations)
#   /etc/systemd/system/vtx-exit.service      (unit file)
#   /var/lib/bt-stats.json                    (historical stats, best-effort)
#
# Also pulls the running binary version for parity. Writes RESTORE.md with
# the runbook needed to re-create this exit elsewhere.
#
# The backup directory contains the MQTT password and DH private key — keep
# it on a trusted host (Mac with FileVault) and don't commit to git.

set -euo pipefail

if [ $# -lt 2 ]; then
  echo "Usage: $0 <exit-id> <ssh-host>" >&2
  exit 1
fi

EXIT_ID="$1"
SSH_HOST="$2"
DATE="$(date +%Y-%m-%d-%H%M)"
ROOT="${HOME}/Backups/vertex/${EXIT_ID}-exit"
DEST="${ROOT}/${DATE}"

mkdir -p "${DEST}"
chmod 700 "${ROOT}"

echo "==> Snapshotting ${EXIT_ID} exit from ${SSH_HOST} → ${DEST}"

# Pull via `ssh sudo cat` — exit runs as root and writes 0600 files.
pull() {
  local remote="$1" local_name="$2" optional="${3:-}"
  if ssh "${SSH_HOST}" "sudo cat ${remote}" > "${DEST}/${local_name}" 2>/dev/null; then
    return 0
  fi
  if [ -n "${optional}" ]; then
    rm -f "${DEST}/${local_name}"
    echo "    (skipped optional: ${remote})"
    return 0
  fi
  echo "ERROR: failed to pull ${remote}" >&2
  exit 1
}

# Required
pull /etc/vertex.yaml                      vertex.yaml
pull /var/lib/vtx-devices.json             vtx-devices.json

# Unit file may live in /etc or /lib
if ! ssh "${SSH_HOST}" 'sudo test -f /etc/systemd/system/vtx-exit.service'; then
  pull /lib/systemd/system/vtx-exit.service vtx-exit.service
else
  pull /etc/systemd/system/vtx-exit.service vtx-exit.service
fi

# Optional
pull /var/lib/bt-stats.json bt-stats.json optional

# Capture running binary version
ssh "${SSH_HOST}" '/usr/local/bin/vtx-exit --version' > "${DEST}/binary-version.txt" 2>/dev/null || true

# Generate RESTORE.md
PASS=$(grep '^pass:' "${DEST}/vertex.yaml" | awk '{print $2}')
DH=$(grep '^dh-private-key:' "${DEST}/vertex.yaml" | awk '{print $2}')
TUN=$(grep '^tun-ip:' "${DEST}/vertex.yaml" | awk '{print $2}')
COUNTRY=$(grep '^country:' "${DEST}/vertex.yaml" | awk '{print $2}')
USER_NAME=$(grep '^user:' "${DEST}/vertex.yaml" | awk '{print $2}')

cat > "${DEST}/RESTORE.md" <<EOF
# Restoring exit \`${EXIT_ID}\`

Snapshot taken ${DATE} from \`${SSH_HOST}\`. The files in this directory
fully describe a running vtx-exit node — to bring it back, place them on a
fresh Linux host, ensure DNS + broker MQTT user are in place, and start the
service.

## What's in this backup

| File | Destination on target | Contents |
|------|------------------------|----------|
| \`vertex.yaml\`         | \`/etc/vertex.yaml\`                       | id, user, pass, DH private key, country |
| \`vtx-devices.json\`    | \`/var/lib/vtx-devices.json\`              | TOFU device registrations (optional — TOFU re-registers on first connect if missing) |
| \`vtx-exit.service\`    | \`/etc/systemd/system/vtx-exit.service\`   | systemd unit |
| \`bt-stats.json\`       | \`/var/lib/bt-stats.json\`                 | historical stats (optional) |
| \`binary-version.txt\`  | —                                         | which vtx-exit version was running |

## Snapshot identity

- Exit ID: \`${EXIT_ID}\`
- MQTT user: \`${USER_NAME}\`
- MQTT password: \`${PASS}\`
- TUN subnet: \`${TUN}\`
- Country: \`${COUNTRY}\`
- DH private key: \`${DH}\`

## Restore procedure

1. **Provision a new Linux host.** Recommended: AWS \`ca-central-1\` t3.micro
   (this snapshot is from Montreal). Any Debian/Ubuntu host with NET_ADMIN
   and \`iptables\` works.

2. **Update SSH alias.** In \`~/.ssh/config\`, point host \`${EXIT_ID}\` (or
   any alias of your choice) at the new IP.

3. **Add the MQTT user back to all brokers.** From the project repo on the
   workstation that has SSH access to the brokers:

       PASS='${PASS}'
       ACL=\$'\\nuser ${USER_NAME}\\ntopic read vpn/${EXIT_ID}/+/out\\ntopic write vpn/${EXIT_ID}/+/in\\ntopic read vpn/${EXIT_ID}/control/join\\ntopic write vpn/${EXIT_ID}/+/control\\ntopic write discovery/exits/${EXIT_ID}\\ntopic write discovery/ping/${EXIT_ID}/+'
       for H in yc sber; do
         ssh \$H "sudo mosquitto_passwd -b /etc/mosquitto/bt_passwd ${USER_NAME} \$PASS && \\
                 echo '\$ACL' | sudo tee -a /etc/mosquitto/bt_acl >/dev/null && \\
                 sudo systemctl reload mosquitto"
       done

4. **Add DNS records** in the \`vertices.ru\` zone (Timeweb panel):

   - SRV \`_vtx-exit._tcp.vertices.ru\` → \`${EXIT_ID}.exit.vertices.ru.\` prio=10 weight=0 port=1
   - TXT \`${EXIT_ID}.exit.vertices.ru\` → \`"<City>, <Country>"\` (for the snapshot: \`"Montreal, Canada"\`)

5. **Push restore via Makefile:**

       make restore-${EXIT_ID} HOST=<ssh-alias>

   That target does the SCP + systemd dance using the most recent snapshot.

6. **Verify:**

       ssh <host> 'sudo journalctl -u vtx-exit --since "30 sec ago" | grep "Transport ready"'

   Then check on R3S or any client:

       ssh r3s 'journalctl -u vtx-gateway --since "60 sec ago" | grep "exit ${EXIT_ID}"'

   You should see \`[discovery] exit ${EXIT_ID}: country=${COUNTRY} clients=0/...\`.

## Caveats

- This backup contains the **MQTT password** and **DH private key**. Keep
  it on encrypted storage. Never commit to git.
- On the broker side, the snapshot password's hash is preserved by
  \`mosquitto_passwd -b\` (re-hashing the same plaintext yields the broker a
  valid record). Clients that joined before decommissioning will reconnect
  cleanly using their cached identity keys (TOFU has been preserved).
- If the original DH key is restored unchanged, **existing client sessions
  do not need to renegotiate** — discovery announces the same DH pubkey,
  and clients reuse cached session keys until next rotation.
EOF

# Update 'latest' symlink for the Makefile target
ln -sfn "${DEST}" "${ROOT}/latest"

chmod 600 "${DEST}"/*
chmod 700 "${DEST}"

echo "==> Done. Backup at: ${DEST}"
echo "    latest symlink: ${ROOT}/latest"
ls -la "${DEST}"

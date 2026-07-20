#!/usr/bin/env bash
# backup-broker.sh — snapshot a Mosquitto broker's runtime state to
# ~/Backups/vertex/<id>-broker/<date>/
#
# Usage: scripts/backup-broker.sh <broker-id> <ssh-host>
# Example: scripts/backup-broker.sh twb twb
#
# Pulls:
#   /etc/mosquitto/mosquitto.conf
#   /etc/mosquitto/conf.d/vertex.conf       (listeners, certs, ACL paths)
#   /etc/mosquitto/bt_passwd                (synced — canonical lives elsewhere)
#   /etc/mosquitto/bt_acl                   (synced)
#   /etc/letsencrypt/live/<host>/*          (cert symlinks)
#   /etc/letsencrypt/archive/<host>/*       (actual cert files)
#   /etc/letsencrypt/renewal/<host>.conf
#   /etc/systemd/system/mosquitto.service.d/override.conf  (optional)
#
# Writes RESTORE.md with the full procedure for spinning the broker back up
# on a fresh VM. Brokers are zero-knowledge relays — no per-broker secrets
# beyond the TLS cert (which can also be re-issued from Let's Encrypt).

set -euo pipefail

if [ $# -lt 2 ]; then
  echo "Usage: $0 <broker-id> <ssh-host>" >&2
  exit 1
fi

BROKER_ID="$1"
SSH_HOST="$2"
DATE="$(date +%Y-%m-%d-%H%M)"
ROOT="${HOME}/Backups/vertex/${BROKER_ID}-broker"
DEST="${ROOT}/${DATE}"

# Resolve broker hostname for the cert path. Default convention: mqtt-<id>.vertices.ru.
HOST_FQDN="${BROKER_HOST_FQDN:-mqtt-${BROKER_ID}.vertices.ru}"

mkdir -p "${DEST}/letsencrypt/live" "${DEST}/letsencrypt/archive" "${DEST}/letsencrypt/renewal" "${DEST}/mosquitto/conf.d" "${DEST}/systemd"
chmod 700 "${ROOT}"

echo "==> Snapshotting ${BROKER_ID} broker from ${SSH_HOST} → ${DEST}"

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

# Mosquitto core configs
pull /etc/mosquitto/mosquitto.conf            mosquitto/mosquitto.conf
pull /etc/mosquitto/conf.d/vertex.conf        mosquitto/conf.d/vertex.conf
pull /etc/mosquitto/bt_passwd                 mosquitto/bt_passwd
pull /etc/mosquitto/bt_acl                    mosquitto/bt_acl

# Let's Encrypt cert (live + archive — live are symlinks pointing into archive)
for f in cert.pem chain.pem fullchain.pem privkey.pem; do
  # archive holds the actual material — versioned numerically (e.g. cert1.pem)
  ssh "${SSH_HOST}" "sudo ls /etc/letsencrypt/archive/${HOST_FQDN}/" 2>/dev/null \
    | grep -E "^${f%.pem}[0-9]+\.pem$" \
    | while read -r archive_name; do
        pull "/etc/letsencrypt/archive/${HOST_FQDN}/${archive_name}" "letsencrypt/archive/${archive_name}"
      done
  pull "/etc/letsencrypt/live/${HOST_FQDN}/${f}" "letsencrypt/live/${f}"
done
pull "/etc/letsencrypt/renewal/${HOST_FQDN}.conf" "letsencrypt/renewal/${HOST_FQDN}.conf"

# Systemd override (optional — present if anyone added ExecStartPre, etc.)
pull /etc/systemd/system/mosquitto.service.d/override.conf systemd/override.conf optional

# Mosquitto version (for parity)
ssh "${SSH_HOST}" 'mosquitto -h 2>&1 | head -1' > "${DEST}/mosquitto-version.txt" 2>/dev/null || true

# Generate RESTORE.md
LISTENERS=$(grep -E "^listener" "${DEST}/mosquitto/conf.d/vertex.conf" | awk '{print $2}' | paste -sd, -)
ORIG_IP=$(ssh "${SSH_HOST}" 'curl -s -4 ifconfig.me 2>/dev/null || hostname -I | awk "{print \$1}"' 2>/dev/null || echo "<unknown>")

cat > "${DEST}/RESTORE.md" <<EOF
# Restoring broker \`${BROKER_ID}\` (\`${HOST_FQDN}\`)

Snapshot taken ${DATE} from \`${SSH_HOST}\` (IP at snapshot time: ${ORIG_IP}).

**Quick restore:** add DNS A \`${HOST_FQDN}\` → \`<new-IP>\` and run
\`make restore-${BROKER_ID} HOST=<ssh-alias>\` from the repo root. The
target re-issues a fresh Let's Encrypt cert; pass \`REUSE_CERT=1\` to skip
that and install this snapshot's cert as-is.

The broker is a vanilla Mosquitto 2.x — no Go binary, no DH key, no
identity DB. The interesting state is just the TLS cert and the
listener/auth config. \`bt_passwd\` and \`bt_acl\` here are snapshots of the
synced canonical files; in steady state they're identical across all
brokers and you can also copy from any live broker (e.g. \`yc\`, \`sber\`)
when restoring.

## What's in this backup

| File | Destination on target |
|------|------------------------|
| \`mosquitto/mosquitto.conf\`         | \`/etc/mosquitto/mosquitto.conf\` |
| \`mosquitto/conf.d/vertex.conf\`     | \`/etc/mosquitto/conf.d/vertex.conf\` (listeners ${LISTENERS}, TLS, ACL refs) |
| \`mosquitto/bt_passwd\`              | \`/etc/mosquitto/bt_passwd\` |
| \`mosquitto/bt_acl\`                 | \`/etc/mosquitto/bt_acl\` |
| \`letsencrypt/live/*\`               | \`/etc/letsencrypt/live/${HOST_FQDN}/*\` (symlinks → archive) |
| \`letsencrypt/archive/*\`            | \`/etc/letsencrypt/archive/${HOST_FQDN}/*\` (numbered cert material) |
| \`letsencrypt/renewal/${HOST_FQDN}.conf\` | \`/etc/letsencrypt/renewal/${HOST_FQDN}.conf\` |
| \`systemd/override.conf\` (optional) | \`/etc/systemd/system/mosquitto.service.d/override.conf\` |

## Restore procedure

1. **Provision a Linux VM.** Any hosting; the original was Timeweb Moscow,
   IP ${ORIG_IP}. Open inbound TCP ${LISTENERS}. Ensure the VM can resolve
   \`${HOST_FQDN}\` (the cert SAN), or temporarily edit /etc/hosts for the
   Let's Encrypt HTTP-01 challenge.

2. **Install Mosquitto 2.x and Let's Encrypt client:**

       sudo apt update
       sudo apt install -y mosquitto certbot

3. **Restore A record DNS:** point \`${HOST_FQDN}\` at the new public IP.
   Wait for propagation before the Let's Encrypt step.

4. **TLS cert — two options:**

   a) **Reuse backup cert** (faster, but cert expires on schedule, no
      auto-renew until step 4b):

         sudo mkdir -p /etc/letsencrypt/{live,archive,renewal}/${HOST_FQDN}
         sudo cp letsencrypt/archive/* /etc/letsencrypt/archive/${HOST_FQDN}/
         # Recreate symlinks (live/* → archive/*N.pem where N is highest)
         cd /etc/letsencrypt/live/${HOST_FQDN}
         for f in cert chain fullchain privkey; do
           # Pick the highest-numbered file
           target=\$(ls ../../archive/${HOST_FQDN}/\${f}*.pem | sort -V | tail -1)
           sudo ln -sf "\${target}" "\${f}.pem"
         done
         sudo cp letsencrypt/renewal/${HOST_FQDN}.conf /etc/letsencrypt/renewal/

   b) **Re-issue from Let's Encrypt** (cleaner — fresh 90-day cert):

         sudo certbot certonly --standalone -d ${HOST_FQDN}

5. **Restore Mosquitto configs:**

       sudo cp mosquitto/mosquitto.conf /etc/mosquitto/mosquitto.conf
       sudo install -d -m 0755 -o root -g root /etc/mosquitto/conf.d
       sudo cp mosquitto/conf.d/vertex.conf /etc/mosquitto/conf.d/vertex.conf
       sudo install -m 0640 -o root -g mosquitto mosquitto/bt_passwd /etc/mosquitto/bt_passwd
       sudo install -m 0640 -o root -g mosquitto mosquitto/bt_acl /etc/mosquitto/bt_acl

   If snapshot \`bt_passwd\` or \`bt_acl\` are stale (a user added/removed since
   this snapshot), pull fresh from any live broker:

       ssh yc 'sudo cat /etc/mosquitto/bt_passwd' | sudo tee /etc/mosquitto/bt_passwd >/dev/null
       ssh yc 'sudo cat /etc/mosquitto/bt_acl'    | sudo tee /etc/mosquitto/bt_acl    >/dev/null

6. **Restore systemd override** (if present in backup):

       sudo install -d /etc/systemd/system/mosquitto.service.d
       sudo cp systemd/override.conf /etc/systemd/system/mosquitto.service.d/
       sudo systemctl daemon-reload

7. **Open the cert read access for mosquitto group** (Let's Encrypt
   defaults restrict /etc/letsencrypt/live/<host>):

       sudo chgrp -R mosquitto /etc/letsencrypt/live /etc/letsencrypt/archive
       sudo chmod g+rx /etc/letsencrypt/live /etc/letsencrypt/archive

8. **Setcap on the binary** for binding port 443 without root:

       sudo setcap CAP_NET_BIND_SERVICE=+eip /usr/sbin/mosquitto

9. **Start and verify:**

       sudo systemctl enable mosquitto
       sudo systemctl start mosquitto
       sudo journalctl -u mosquitto --since "30 sec ago" --no-pager
       mosquitto_sub -h ${HOST_FQDN} -p 8883 --capath /etc/ssl/certs -u vtx-exit-sto -P <pass> -t 'discovery/exits/+' -W 5

10. **Re-add DNS SRV records** in the \`vertices.ru\` zone:

        _mqtt._tcp  SRV  <prio>  0  8883  ${HOST_FQDN}.
        _mqtt._tcp  SRV  <prio+30>  0  443   ${HOST_FQDN}.

    Pick a priority lower (better) than the current backup brokers, or
    higher (worse) if this is just a tertiary failover.

11. **(Optional) Mikrotik mangle skip rule** for the new IP if the home
    R3S uses split routing.

## Caveats

- This backup includes the **TLS private key**. Keep on encrypted storage.
  Never commit to git.
- \`bt_passwd\` contains \`mosquitto_passwd\` \$7\$ hashes — not plaintext, but
  worth protecting like a password file.
- Let's Encrypt rate limits: 5 duplicate certs per host per week. If
  you're iterating restore attempts, prefer the backup cert (option 4a)
  during testing.
EOF

# Update 'latest' symlink
ln -sfn "${DEST}" "${ROOT}/latest"

find "${DEST}" -type f -exec chmod 600 {} \;
find "${DEST}" -type d -exec chmod 700 {} \;

echo "==> Done. Backup at: ${DEST}"
echo "    latest symlink: ${ROOT}/latest"
find "${DEST}" -type f | sed "s|${DEST}/||" | sort

# Deployment Guide — Vertex

Step-by-step instructions for deploying Vertex infrastructure (binaries `vtx-*`, configs `/etc/vertex.*`) on clean Ubuntu 22.04/24.04 servers.

## Table of Contents

- [Minimal Setup (1 Broker + 1 Exit)](#minimal-setup)
- [Add a New Broker](#add-a-new-broker)
- [Add a New Exit](#add-a-new-exit)
- [Connect a Client (Desktop)](#connect-a-client)
- [Connect a Gateway (Router)](#connect-a-gateway)
- [Credential Management](#credential-management)
- [Maintenance](#maintenance)

---

## Minimal Setup

Minimum infrastructure: **1 broker + 1 exit** on separate servers. The broker can run on the same server as the exit, but separate servers are recommended for reliability.

### 1. Deploy the Broker

**Server requirements**: Ubuntu 22.04+, public IP, ports 8883 (MQTTS) and 443 (WSS) open, domain name with DNS A-record pointing to server IP.

#### Install Mosquitto

```bash
apt update && apt install -y mosquitto mosquitto-clients certbot
systemctl stop mosquitto
```

#### Get TLS Certificate (Let's Encrypt)

```bash
# Replace mqtt.example.com with your domain
certbot certonly --standalone -d mqtt.example.com

# Mosquitto needs read access to certs
chmod 750 /etc/letsencrypt/live /etc/letsencrypt/archive
chgrp mosquitto /etc/letsencrypt/live /etc/letsencrypt/archive
```

#### Configure Mosquitto

```bash
cat > /etc/mosquitto/conf.d/vertex.conf << 'EOF'
per_listener_settings false
allow_anonymous false

# MQTTS listener (primary)
listener 8883
certfile /etc/letsencrypt/live/mqtt.example.com/fullchain.pem
keyfile /etc/letsencrypt/live/mqtt.example.com/privkey.pem

# WSS listener (DPI fallback, direct on 443)
# Requires: setcap CAP_NET_BIND_SERVICE=+ep /usr/sbin/mosquitto
listener 443
protocol websockets
certfile /etc/letsencrypt/live/mqtt.example.com/fullchain.pem
keyfile /etc/letsencrypt/live/mqtt.example.com/privkey.pem

# Auth
password_file /etc/mosquitto/vtx_passwd
acl_file /etc/mosquitto/vtx_acl

# Performance tuning for VPN traffic
max_inflight_messages 0
max_queued_messages 0
max_packet_size 1700
persistence false
EOF
```

#### Create Empty Auth Files

```bash
touch /etc/mosquitto/vtx_passwd /etc/mosquitto/vtx_acl
chown mosquitto:mosquitto /etc/mosquitto/vtx_passwd /etc/mosquitto/vtx_acl
```

#### Start Mosquitto

```bash
systemctl start mosquitto
systemctl enable mosquitto

# Verify it's listening on 8883 and 443
ss -tlnp | grep -E '8883|443'
```

#### Allow Mosquitto to Bind Port 443

Port 443 is privileged (<1024). Mosquitto PPA 2.1.x uses built-in WebSocket support
(not libwebsockets) and can bind it with `setcap`:

```bash
setcap CAP_NET_BIND_SERVICE=+ep /usr/sbin/mosquitto

# Also allow port 443 in firewall (if policy DROP)
iptables -A INPUT -p tcp --dport 443 -j ACCEPT
iptables-save > /etc/iptables/rules.v4
```

#### Deploy vtx-admin

Build on your dev machine and copy to broker:

```bash
# On dev machine
make build-admin
scp dist/admin/linux-amd64/vtx-admin BROKER_HOST:/usr/local/bin/vtx-admin
ssh BROKER_HOST "chmod +x /usr/local/bin/vtx-admin"
```

#### Create Exit User

```bash
# On the broker server
vtx-admin add-exit --id=myexit

# Output will show:
#   Exit user vtx-exit-myexit created.
#   Exit config:
#     vtx-exit --id=myexit --brokers=mqtts://mqtt-yc.vertices.ru:8883 --pass=GENERATED_PASSWORD
#
# Save the password — you'll need it for the exit server config.
```

#### Create Client User

```bash
# Single-exit client:
vtx-admin add --name=laptop --exits=myexit

# Multi-exit client (if you plan to add more exits later):
vtx-admin add --name=laptop --exits=myexit,future-exit

# Output will show the generated password. Save it.
```

#### Auto-Renew TLS Certs

```bash
# certbot auto-renew is installed by default, but Mosquitto needs a reload after renewal
cat > /etc/letsencrypt/renewal-hooks/post/mosquitto.sh << 'EOF'
#!/bin/bash
systemctl reload mosquitto
EOF
chmod +x /etc/letsencrypt/renewal-hooks/post/mosquitto.sh
```

### 2. Deploy the Exit

**Server requirements**: Ubuntu 22.04+, public IP, outbound internet access (this is where VPN traffic exits).

#### Install Dependencies

```bash
apt update && apt install -y iptables iproute2
```

#### Deploy Binary

```bash
# On dev machine
make build-exit
scp dist/exit/linux-amd64/vtx-exit EXIT_HOST:/usr/local/bin/vtx-exit
ssh EXIT_HOST "chmod +x /usr/local/bin/vtx-exit"
```

#### Generate DH Keypair

```bash
# On any machine — generates X25519 keypair for exit config
vtx-admin gen-dh-key
# Or manually:
openssl rand -hex 32
# The private key goes in exit config; public key is auto-published via discovery
```

#### Create YAML Config

```bash
# On the exit server
cat > /etc/vertex.yaml << 'EOF'
brokers:
  - mqtts://mqtt.example.com:8883
id: myexit
tun-ip: 10.9.0.1/24
user: vtx-exit-myexit
pass: "PASSWORD_FROM_VTX_ADMIN"
dh-private-key: "GENERATED_DH_PRIVATE_KEY"
country: US
max-clients: 50
stats-file: /var/lib/vtx-stats.json
EOF

chmod 600 /etc/vertex.yaml
```

**TUN subnet allocation**: each exit gets a unique /24 subnet. First exit: `10.9.0.1/24`, second: `10.9.1.1/24`, etc.

#### Create systemd Service

```bash
cat > /etc/systemd/system/vtx-exit.service << 'EOF'
[Unit]
Description=Vertex Exit Node
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
ExecStartPre=/bin/sleep 3
ExecStart=/usr/local/bin/vtx-exit --config=/etc/vertex.yaml
Restart=on-failure
RestartSec=5
LimitNOFILE=65536

[Install]
WantedBy=multi-user.target
EOF
```

The `ExecStartPre=sleep 3` is critical: without it, if the service restarts too fast, the broker may not have cleaned up the previous MQTT session, causing a client-ID collision and immediate disconnect.

#### Start the Exit

```bash
systemctl daemon-reload
systemctl enable vtx-exit
systemctl start vtx-exit

# Check logs
journalctl -u vtx-exit -f
# Should show:
#   E2E encryption enabled (ChaCha20-Poly1305)
#   Connected to broker 0: mqtts://mqtt.example.com:8883
#   NAT configured on vtx0
#   Transport ready on 1 broker(s), accepting clients on vpn/myexit/+/out
```

#### Verify

```bash
# TUN interface created
ip addr show vtx0

# NAT rules in place
iptables -t nat -L POSTROUTING -n | grep MASQUERADE
iptables -L FORWARD -n | grep vtx0

# IP forwarding enabled
sysctl net.ipv4.ip_forward
```

### 3. Connect from Client

See [Connect a Client](#connect-a-client) section below.

---

## Add a New Broker

Adding a second broker provides failover: clients auto-reconnect to the next broker if the primary goes down (~100ms switchover).

### On the New Broker Server

Follow the same steps as [Deploy the Broker](#1-deploy-the-broker):

1. Install Mosquitto + certbot
2. Get TLS cert for the new domain (e.g. `mqtt2.example.com`)
3. Create the same `vertex.conf`
4. Create empty `vtx_passwd` and `vtx_acl`

### Sync Credentials from Primary Broker

```bash
# On the primary broker (where vtx-admin has been creating users)
vtx-admin sync --brokers=NEW_BROKER_SSH_HOST

# This SCPs vtx_passwd + vtx_acl and reloads Mosquitto on the new broker.
# Requires SSH key access from primary broker to new broker.
```

### Update All Exits

Each exit must connect to **all** brokers. Update YAML config on every exit:

```yaml
brokers:
  - mqtts://mqtt.example.com:8883
  - mqtts://mqtt2.example.com:8883     # add this line
```

Then restart:

```bash
systemctl restart vtx-exit
# Verify it connects to both:
journalctl -u vtx-exit | grep "Connected to broker"
# Should show:
#   Connected to broker 0: mqtts://mqtt.example.com:8883
#   Connected to broker 1: mqtts://mqtt2.example.com:8883
```

### Update Clients

Add the new broker URL to client configs. Order matters — MQTTS on all brokers first, then WSS fallback:

```yaml
brokers:
  - mqtts://mqtt.example.com:8883       # primary (MQTT/TLS)
  - mqtts://mqtt2.example.com:8883      # second broker (MQTT/TLS)
  - wss://mqtt.example.com:443          # WSS fallback (DPI)
  - wss://mqtt2.example.com:443         # WSS fallback (DPI)
```

Or via CLI flags:

```bash
sudo ./vtx-client --brokers=mqtts://mqtt.example.com:8883,mqtts://mqtt2.example.com:8883,wss://mqtt.example.com:443,wss://mqtt2.example.com:443 ...
```

### Future Credential Syncs

After adding/removing users on the primary broker, sync to all:

```bash
vtx-admin sync --brokers=broker2-host,broker3-host
```

---

## Add a New Exit

### 1. Create Exit User on the Primary Broker

```bash
# On the primary broker
vtx-admin add-exit --id=newexit
# Save the generated password
```

### 2. Sync Credentials to All Brokers

```bash
vtx-admin sync --brokers=broker2-host
```

### 3. Deploy the Exit Server

Follow [Deploy the Exit](#2-deploy-the-exit) steps:

1. Install dependencies
2. Copy `vtx-exit` binary
3. Generate a new E2E key (`openssl rand -hex 32`)
4. Create `/etc/vertex.yaml` with **all broker URLs** and the next available TUN subnet:

```yaml
brokers:
  - mqtts://mqtt.example.com:8883
  - mqtts://mqtt2.example.com:8883
id: newexit
tun-ip: 10.9.1.1/24               # next available /24
user: vtx-exit-newexit
pass: "PASSWORD_FROM_VTX_ADMIN"
dh-private-key: "NEW_DH_PRIVATE_KEY"
country: DE
max-clients: 50
```

5. Create systemd service, start it

### 4. Grant Existing Clients Access

Clients need ACL permission for the new exit. Two options:

**Option A**: Recreate client with multi-exit ACL (wildcard):

```bash
# On primary broker
vtx-admin remove --name=laptop
vtx-admin add --name=laptop --exits=myexit,newexit
vtx-admin sync --brokers=broker2-host
```

**Option B**: If the client was already created with multi-exit (`--exits=exit1,exit2`), they have wildcard ACL (`vpn/+/...`) and can access any exit without changes.

### 5. Client Config — No Changes Needed

With DH key exchange, clients automatically negotiate encryption with any exit. No E2E keys in client config. Without `--exit` flag, the client will auto-select the best exit by discovery score.

---

## Connect a Client

Desktop VPN client for macOS or Linux. Requires `sudo` (creates TUN device and modifies system routes).

### Build

```bash
# macOS (Apple Silicon)
make build-client

# Linux AMD64
CGO_ENABLED=0 GOOS=linux GOARCH=amd64 go build -o vtx-client ./clients/cli/
```

### Run with Explicit Exit

```bash
sudo ./vtx-client \
  --brokers=mqtts://mqtt.example.com:8883,mqtts://mqtt2.example.com:8883,wss://mqtt.example.com:443,wss://mqtt2.example.com:443 \
  --name=laptop \
  --exit=myexit \
  --pass=CLIENT_PASSWORD
```

### Run with Auto-Select (Multiple Exits)

```bash
sudo ./vtx-client \
  --brokers=mqtts://mqtt.example.com:8883,mqtts://mqtt2.example.com:8883,wss://mqtt.example.com:443,wss://mqtt2.example.com:443 \
  --name=laptop \
  --pass=CLIENT_PASSWORD
```

The client subscribes to `discovery/exits/+`, scores each exit by `RTT * (1 + clients/capacity)`, and picks the best one. Re-evaluates every 5 minutes and switches if a better exit is 50%+ better.

### Run with YAML Config

```bash
sudo ./vtx-client --config=/etc/vertex.yaml
```

```yaml
brokers:
  - mqtts://mqtt.example.com:8883
  - mqtts://mqtt2.example.com:8883
  - wss://mqtt.example.com:443
  - wss://mqtt2.example.com:443
name: laptop
pass: "CLIENT_PASSWORD"
# exit: myexit          # uncomment to pin to specific exit
# No E2E keys needed — DH key exchange is automatic
```

### Verify Connection

```bash
# In another terminal:
curl ifconfig.me
# Should show the exit server's public IP, not your own

# With --verbose flag: packet log + stats every 5s
sudo ./vtx-client --config=/etc/vertex.yaml --verbose
```

---

## Connect a Gateway

Linux router (e.g. NanoPi R3S) that tunnels traffic for the entire LAN. Uses external `vtx-proxy.sh` for transparent proxy and split routing.

### Build

```bash
# Linux ARM64 (R3S, Raspberry Pi)
make build-gateway

# Linux AMD64
CGO_ENABLED=0 GOOS=linux GOARCH=amd64 go build -o vtx-gateway ./clients/gateway/
```

### Deploy to Gateway

```bash
scp dist/gateway/linux-arm64/vtx-gateway GATEWAY_HOST:/usr/local/bin/vtx-gateway
scp clients/gateway/r3s/vtx-proxy.sh GATEWAY_HOST:/usr/local/bin/vtx-proxy.sh
ssh GATEWAY_HOST "chmod +x /usr/local/bin/vtx-gateway /usr/local/bin/vtx-proxy.sh"
```

### Create YAML Config

```bash
# On the gateway
cat > /etc/vertex.yaml << 'EOF'
brokers:
  - mqtts://mqtt.example.com:8883
  - mqtts://mqtt2.example.com:8883
  - wss://mqtt.example.com:443
  - wss://mqtt2.example.com:443
name: r3s
exit: myexit
pass: "CLIENT_PASSWORD"
# No E2E keys needed — DH key exchange is automatic
EOF

chmod 600 /etc/vertex.yaml
```

### Create Environment File for vtx-proxy.sh

```bash
cat > /etc/vertex.env << 'EOF'
VTX_LAN_SUBNET=192.168.1.0/24         # your LAN subnet
VTX_WAN_IFACE=eth0                    # WAN interface
VTX_TUN_IFACE=vtx0                    # TUN interface name
EOF
```

Broker IPs (for iptables bypass) are resolved automatically by `vtx-proxy.sh`:
SRV `_mqtt._tcp.<domain>` → A records, with fallback to `/etc/vertex/discovery-cache.json`
(written by `vtx-gateway`) and finally `brokers:` list in `/etc/vertex.yaml`.

Requires `dig` (`apt install dnsutils`). The script hard-fails with a clear message
if `dig` is missing or every discovery source returns nothing.

Optional override: set `VTX_BROKER=IP,IP` in `/etc/vertex.env` to bypass DNS discovery
(useful when DNS is broken and you need to bring the gateway up manually).

### Create systemd Service

```bash
cat > /etc/systemd/system/vtx-gateway.service << 'EOF'
[Unit]
Description=Vertex Gateway
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
EnvironmentFile=/etc/vertex.env
ExecStartPre=/bin/sleep 3
ExecStart=/usr/local/bin/vtx-gateway --config=/etc/vertex.yaml
ExecStartPost=/usr/local/bin/vtx-proxy.sh setup
ExecStopPost=/usr/local/bin/vtx-proxy.sh cleanup
Restart=on-failure
RestartSec=5
LimitNOFILE=65536

[Install]
WantedBy=multi-user.target
EOF
```

### Start

```bash
systemctl daemon-reload
systemctl enable vtx-gateway
systemctl start vtx-gateway
journalctl -u vtx-gateway -f
```

### Verify

```bash
# TUN interface
ip addr show vtx0

# Split routing rules
iptables -t mangle -L VTX_FORWARD -n
ip rule list | grep "fwmark 1"
ip route show table 100

# Test from a LAN device (should show exit's IP)
curl ifconfig.me
```

---

## Credential Management

### Create Users

All user management happens on the **primary broker** server via `vtx-admin`.

```bash
# Create exit user
vtx-admin add-exit --id=EXITNAME

# Create client (single exit, tighter ACL)
vtx-admin add --name=USERNAME --exits=EXITNAME

# Create client (multi-exit, wildcard ACL)
vtx-admin add --name=USERNAME --exits=exit1,exit2

# Custom password (instead of auto-generated)
vtx-admin add --name=USERNAME --exits=exit1 --pass=CUSTOM_PASSWORD
```

### Sync After Changes

After any add/remove operation, sync to all brokers:

```bash
vtx-admin sync --brokers=broker2-host,broker3-host
```

### List and Inspect

```bash
# List all client users
vtx-admin list

# Show details for a specific user
vtx-admin show --name=USERNAME

# View exit stats (run on exit server or copy stats file)
vtx-admin stats
vtx-admin stats --stats-file=/path/to/vtx-stats.json
```

### Remove Users

```bash
vtx-admin remove --name=USERNAME
vtx-admin sync --brokers=broker2-host,broker3-host
```

### DH Key Rotation

1. Generate new keypair: `vtx-admin gen-dh-key`
2. Update exit config (`/etc/vertex.yaml`) with new `dh-private-key`
3. Restart exit: `systemctl restart vtx-exit`
4. Clients reconnect automatically (keepalive re-establishes cipher within 60s)
5. No client config changes needed

---

## Maintenance

### Update Binary

```bash
# Build on dev machine
make build-exit   # or build-gateway, build-admin

# Deploy (example for any exit)
scp dist/exit/linux-amd64/vtx-exit EXIT_HOST:/tmp/vtx-exit-new
ssh EXIT_HOST "systemctl stop vtx-exit && \
  cp /tmp/vtx-exit-new /usr/local/bin/vtx-exit && \
  chmod +x /usr/local/bin/vtx-exit && \
  systemctl start vtx-exit"
```

Or use Makefile targets:

```bash
make deploy-sto    # deploy to STO exit
make deploy-ams    # deploy to AMS exit
make deploy-mtl HOST=<alias>  # deploy to a restored MTL exit (see Backups)
make restore-mtl HOST=<alias> # restore the backed-up MTL exit on a fresh host
make deploy-r3s    # deploy gateway + vtx-proxy.sh to R3S
```

### Check Service Status

```bash
# Exit
ssh EXIT_HOST "systemctl status vtx-exit"
ssh EXIT_HOST "journalctl -u vtx-exit --since '10 min ago'"

# Gateway
ssh GATEWAY_HOST "systemctl status vtx-gateway"

# Mosquitto
ssh BROKER_HOST "systemctl status mosquitto"
ssh BROKER_HOST "journalctl -u mosquitto --since '10 min ago'"
```

### Mosquitto Reload (After ACL/Password Changes)

```bash
# Auto-reload happens via vtx-admin, but manual:
kill -HUP $(pidof mosquitto)
# or
systemctl reload mosquitto
```

### Firewall Rules

**Broker server**:
```bash
ufw allow 8883/tcp   # MQTTS
ufw allow 443/tcp    # WSS (DPI fallback)
ufw allow 22/tcp     # SSH
```

**Exit server**:
```bash
ufw allow 22/tcp     # SSH
# No inbound ports needed — exit connects outbound to broker
```

### Docker Integration Tests

Run the full test suite before deploying changes:

```bash
make test
# Runs test-docker.sh: starts 2 brokers + 2 exits + clients in Docker,
# verifies connectivity, failover, and E2E encryption.
```

---

## Quick Reference

### TUN Subnet Allocation

| Exit ID | TUN Subnet |
|---------|------------|
| First exit | 10.9.0.1/24 |
| Second exit | 10.9.1.1/24 |
| Third exit | 10.9.2.1/24 |
| ... | 10.9.N.1/24 |

### Port Map

| Service | Port | Protocol |
|---------|------|----------|
| Mosquitto (MQTTS) | 8883 | TCP |
| Mosquitto (WSS, DPI fallback) | 443 | TCP |
| Mosquitto (plain, Docker only) | 1883 | TCP |
| Mosquitto (WS, Docker only) | 9001 | TCP |

### File Paths (Broker)

| File | Path |
|------|------|
| Mosquitto config | `/etc/mosquitto/conf.d/vertex.conf` |
| Password file | `/etc/mosquitto/vtx_passwd` |
| ACL file | `/etc/mosquitto/vtx_acl` |
| vtx-admin | `/usr/local/bin/vtx-admin` |

### File Paths (Exit)

| File | Path |
|------|------|
| Binary | `/usr/local/bin/vtx-exit` |
| YAML config | `/etc/vertex.yaml` |
| Stats file | `/var/lib/vtx-stats.json` |
| systemd unit | `/etc/systemd/system/vtx-exit.service` |

### File Paths (Gateway)

| File | Path |
|------|------|
| Binary | `/usr/local/bin/vtx-gateway` |
| YAML config | `/etc/vertex.yaml` |
| Environment | `/etc/vertex.env` |
| vtx-proxy.sh | `/usr/local/bin/vtx-proxy.sh` |
| systemd unit | `/etc/systemd/system/vtx-gateway.service` |

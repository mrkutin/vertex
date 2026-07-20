# Vertex

**Brand:** Vertex — the point of convergence (graph vertex). Brokers are vertices, clients/exits are the edges meeting at them. See `BRAND.md` for the full naming line and design notes.

The project was historically named `broker-tunnel` / `bt-*`. The codebase, binaries, configs, brokers, exits, and clients are all on `vertex` / `vtx-*` now. The HKDF info string `"broker-tunnel-v1"` (wire-protocol) is the only remaining legacy identifier — see `MIGRATION.md`.

VPN tunnel over MQTT message broker. Clients and exit servers connect to Mosquitto and exchange IP packets as MQTT messages. Simpler, faster, and more reliable than WebRTC-based tunneling.

## Architecture

```
                    Broker-1 (RU)             Broker-2 (EU)
                    +-----------+             +-----------+
Client/Gateway ---->| Mosquitto |             | Mosquitto |
  (failover list)   | :8883/:443|             | :8883/:443|
                    +-----+-----+             +-----+-----+
                          |                         |
                    Exit-1 ------------------------+  (connected to ALL brokers)
                    Exit-2 ------------------------+
                          |
                       NAT/MASQUERADE -> Internet
```

- Each **exit** connects to **all** brokers simultaneously (fan-out topology)
- Each **client/gateway** connects to **one** broker with automatic failover
- Any exit is reachable from any broker
- Standalone Mosquitto instances (no bridges, no clustering)
- E2E encryption (X25519 DH + ChaCha20-Poly1305) -- broker is a zero-knowledge relay
- Device identity (X25519 TOFU) -- prevents credential sharing

## Features

### Security and Encryption

- **E2E Encryption** -- X25519 Diffie-Hellman key exchange during join handshake, ChaCha20-Poly1305 AEAD symmetric cipher. Broker sees only encrypted packets (zero-knowledge relay). 28 bytes overhead per packet (12B nonce + 16B auth tag).
- **Perfect Forward Secrecy** -- Ephemeral X25519 keypair generated per connection. New keys on every connect and exit switch (session-level PFS).
- **Key derivation** -- `HKDF-SHA256(ECDH_shared, salt=clientPub||exitPub, info="broker-tunnel-v1")` (the HKDF `info` string is wire-protocol; it is intentionally not renamed to keep wire compatibility with deployed clients).
- **Per-client session keys** -- Each client has a unique cipher on the exit. No shared secrets between clients.
- **Device Identity** -- WireGuard-style persistent X25519 keypair per device. TOFU (Trust On First Use) registration on exits. `HMAC-SHA256(ECDH(identity_priv, exit_pub), "vtx-identity-v1" + name)` proves key ownership. Stolen username+password is insufficient without the matching identity key.
- **require-identity mode** -- Exit rejects clients that do not present a registered identity key.
- **Zero key management** -- Clients need no E2E keys in config. DH exchange and identity key generation are fully automatic.

### Network and Topology

- **Multi-broker fan-out** -- Exit connects to N brokers simultaneously, subscribes to the same topic prefixes on each. Responses routed through the correct broker via per-client `brokerIdx`.
- **Broker failover** -- autopaho ordered URL list with sticky reconnect (last-known-good broker tried first). Failover time ~100ms.
- **WSS/443 DPI fallback** -- Mosquitto dual-listener: 8883 (MQTTS) + 443 (WSS). Port 443 traffic is indistinguishable from HTTPS for DPI. Custom WebSocket frame wrapper (`wsconn.go`) for paho.golang compatibility.
- **SRV-based discovery** -- DNS SRV records (`_mqtt._tcp.{domain}` for brokers, `_vtx-exit._tcp.{domain}` for exits) with priority/weight ordering. Domain is a config knob (no rebuild on infra changes). Native clients resolve via DoH (Cloudflare/Google).
- **Chain-of-trust fallback** -- `_vtx-backup._tcp.{domain}` SRV record points to a backup discovery domain. If primary domain SRV resolution fails, clients re-resolve through the backup before falling back to cached results.
- **Exit discovery** -- MQTT retained heartbeats every 30 seconds with broker RTT, client count, capacity, country, DH pubkey. LWT clears heartbeat on ungraceful disconnect.
- **Exit auto-select** -- Score formula: `broker_rtt * (1 + clients/capacity * load_factor)`. Lowest score wins.
- **Exit auto-switch** -- Runtime switching with atomic session swap, TUN IP reconfiguration, DH re-exchange. Anti-flapping: 1.5x tolerance, 5-minute rebalance interval.
- **Health check** -- 15-second tick detects offline exits via stale heartbeats (>90 seconds).

### Routing

- **macOS sub-range routes** -- 8 routes (1/8, 2/7, 4/6, 8/5, 16/4, 32/3, 64/2, 128/1), same approach as sing-box/Hiddify. Default route stays untouched so mDNSResponder keeps working.
- **Linux default via TUN** -- Standard `ip route` default through TUN device.
- **Broker bypass** -- All broker hosts get direct routes so broker traffic is never tunneled.
- **Split routing (gateway)** -- ipset with ~8500 RU subnets, iptables MARK + policy routing. RU traffic goes direct, everything else through the tunnel.
- **Transparent proxy (gateway)** -- iptables mangle PREROUTING marks packets from LAN devices for routing through the tunnel. No client configuration needed.
- **DNS cache flush** -- macOS: `dscacheutil -flushcache` + `killall -HUP mDNSResponder` on setup/cleanup.

### Reliability

- **Join retry** -- 2-second retry with configurable timeout (default 30 seconds).
- **Publish timeout** -- 5-second timeout prevents PINGREQ blocking on congested connections.
- **Periodic keepalive** -- Resends join message every 60s to update brokerIdx, refresh lastSeen, and re-establish cipher after exit restart. No DH key rotation (same cipher throughout session).
- **Graceful shutdown** -- Signal handler (SIGINT/SIGTERM) with iptables/route cleanup.
- **Idempotent NAT setup** -- `cleanup()` runs before `setup()` to avoid duplicate rules.
- **Client cleanup** -- Idle clients (>30 minutes) removed automatically, IP returned to pool.
- **ExecStartPre=sleep 3** -- Ensures broker has time to clean up the old session on zero-gap restarts.
- **TUN pipeline** -- Buffered channels (`pkg/pipeline`) decouple TUN read/write from MQTT publish/subscribe. Single writer goroutine per direction (no `tunMu`). Production throughput on macOS Ethernet ~89/92 Mbps after this optimization (vs 42/73 baseline).

### Native client UX (iOS / macOS / Android)

- **Shared Swift `VertexCore` package** -- iOS and macOS share a single Swift Package: CryptoKit (X25519, HKDF, ChaCha20-Poly1305), MQTT 5.0 over `NWConnection`, topics, IPC, identity. No code duplication between Apple platforms.
- **Native MQTT 5.0 stack** -- Built on `NWConnection` (Apple) / Kotlin port of the Swift codec (Android). Subset implemented: CONNECT/CONNACK/PUBLISH(QoS 0)/SUBSCRIBE/SUBACK/PINGREQ/PINGRESP/DISCONNECT + Message Expiry + retained flag.
- **PINGREQ/PINGRESP liveness** -- Application-level keepalive (15s ping, 5s timeout) is the sole link-dead detector on iOS — `NWPathMonitor` and per-connection viability proved unreliable in practice.
- **Wifi roam handling** -- `NEPacketTunnelProvider` restarts itself on cellular→wifi to re-scope to the better network; wifi→cellular relies on PINGRESP timeout for fast detection.
- **Identity in Keychain (Apple) / EncryptedFile (Android)** -- 32-byte X25519 private key stored in platform keystore with `kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly` (extension reads when screen locked).
- **Memory budget** -- iOS NEPacketTunnelProvider runs in a ~50 MB jetsam sandbox. Production Vertex iOS extension: 3.1 MB idle / 14 MB peak speedtest (3.5× headroom). Release-only extension build (Debug exceeds the limit).
- **MetricKit + signposts (Apple)** -- Host app subscribes to `MXMetricManager`, persists payloads to App Group container, exposes a Diagnostics view. `os_signpost` markers on keepalive-join (60 s), MQTT pingreq (15 s), stats-log (5 s), upload-batch — visible in Instruments Points of Interest.
- **DNS via tunnel (iOS)** -- All domains route through the tunnel (1.1.1.1 + 8.8.8.8). Split DNS via `matchDomains` was tried in v2.9.2 and broke any host resolved through the system resolver — iOS does not allow per-domain DNS servers.
- **VPN On-Demand (iOS)** -- `NEOnDemandRuleConnect` keeps the tunnel up across reboots and after self-`cancelTunnelWithError`-driven restarts.

### Platform Support

| Platform | Client | TUN | Routing |
|----------|--------|-----|---------|
| macOS (darwin/arm64) | vtx-client (CLI) + Vertex.app (SwiftUI + NEPacketTunnelProvider) | water (utun) | Sub-range routes, DNS flush |
| iOS 17+ | Vertex.app (SwiftUI + NEPacketTunnelProvider) | NEPacketTunnelProvider | NEIPv4Route includedRoutes/excludedRoutes |
| Android 8.0+ (API 26+) | Vertex.apk (Compose + VpnService) — **Phase 1-3 done**, validated on Sony Xperia 5 V (Android 13) | VpnService.Builder | full-tunnel + RU-exclude (cap 1500 routes due to Binder limit) |
| Linux amd64 | vtx-client, vtx-exit | Raw syscall + unix.Poll | ip route default via TUN |
| Linux arm64 (R3S) | vtx-gateway | Raw syscall + unix.Poll | vtx-proxy.sh (iptables/ipset) |
| Future: Windows | WinUI subprocess | -- | Stub |

## Components

| Component | Binary / Bundle | Description |
|-----------|-----------------|-------------|
| **Client (CLI)** | `vtx-client` | Universal Go VPN client (macOS/Linux desktop). Built-in routing, broker failover, exit auto-select/auto-switch, JSON events for native wrappers. |
| **Gateway** | `vtx-gateway` | Linux router mode (e.g. NanoPi R3S). No built-in routing — uses external `vtx-proxy.sh` for transparent proxy + split routing. |
| **iOS app** | `ru.vertices` (Vertex.app) | Native iPhone/iPad VPN app (SwiftUI + NEPacketTunnelProvider). Shared `VertexCore` Swift package. |
| **macOS app** | `ru.vertices` (Vertex.app) | Native Mac VPN app (SwiftUI + NEPacketTunnelProvider). Shares the same `VertexCore` Swift package as iOS. |
| **Android app** | `ru.vertices` (Vertex.apk) | Native Android VPN app (Jetpack Compose + VpnService). Pure Kotlin, byte-exact wire-protocol parity with the iOS/Go reference. **Phase 1-3 done** (full-tunnel, multi-broker, RU-exclude split, auto-exit RTT scoring, captive validation, Google Play login under always-on lockdown). See `clients/android/PLAN.md` for status & Phase 3.5 (userspace split) plan. |
| **Exit** | `vtx-exit` | Exit node. TUN + NAT + multi-client IP pool (/24, .2-.254). Connects to all brokers simultaneously. Discovery heartbeats. |
| **Admin** | `vtx-admin` | Mosquitto user/ACL management. Create users, generate DH keys, sync credentials across brokers, view stats. |

## Build

All binaries are built through the Makefile. Output goes to `dist/`.

```bash
# Build everything
make build-all

# Individual targets
make build-client     # macOS (darwin/arm64) -> dist/cli/darwin-arm64/vtx-client
make build-gateway    # Linux ARM64 (R3S)    -> dist/gateway/linux-arm64/vtx-gateway
make build-exit       # Linux AMD64          -> dist/exit/linux-amd64/vtx-exit
make build-admin      # Linux AMD64          -> dist/admin/linux-amd64/vtx-admin

# Server binaries only (gateway + exit + admin)
make build

# Deploy
make deploy-r3s       # SCP vtx-gateway + vtx-proxy.sh to R3S, restart service
make deploy-sto       # SCP vtx-exit to STO, restart service
make deploy-sto       # SCP vtx-exit to STO, restart service

# Native apps
make build-ios-release            # iOS Release .app  -> dist/ios/Vertex.app
make build-macos-release          # macOS Release .app -> dist/macos/Vertex.app
make build-android-release        # Android Release .apk -> dist/android/Vertex.apk (Phase 1-3 done)

# Docker integration tests
make test

# Clean
make clean
```

## Usage

### vtx-client

Desktop VPN client with built-in routing. Requires `sudo` (creates TUN device and modifies routes).

```bash
# Explicit exit
sudo ./vtx-client \
  --brokers=mqtts://mqtt.example.com:8883,mqtts://mqtt2.example.com:8883 \
  --name=mac \
  --exit=sto \
  --pass=secret

# Auto-select mode (picks best exit by discovery score)
sudo ./vtx-client \
  --brokers=mqtts://mqtt.example.com:8883,mqtts://mqtt2.example.com:8883 \
  --name=mac \
  --pass=secret

# With YAML config
sudo ./vtx-client --config=/etc/vertex.yaml
```

#### Flags

| Flag | Type | Default | Description |
|------|------|---------|-------------|
| `--config` | string | | YAML config file (replaces all flags below) |
| `--brokers` | string | | Comma-separated broker URLs (`mqtts://`, `wss://`) |
| `--broker` | string | | Single broker URL (backward compat) |
| `--name` | string | *required* | Client name (e.g. `mac`, `laptop`) |
| `--exit` | string | *auto* | Exit node ID; omit for auto-select |
| `--pass` | string | | MQTT password |
| `--no-routing` | bool | false | Skip route setup (for Docker or external routing) |
| `--json` | bool | false | JSON event output on stdout (for native wrappers) |
| `--verbose` | bool | false | Packet logging + stats every 5 seconds |

#### JSON Events

When running with `--json`, structured events are emitted on stdout for native app integration (Swift, Kotlin):

```
connecting, connected, exit_selected, ip_assigned, tun_created,
routes_configured, ready, exit_switching, exit_switched,
exit_switch_failed, error, disconnected
```

### vtx-gateway

Linux router mode for transparent proxying of LAN traffic. Designed for use with `vtx-proxy.sh` which handles iptables, ipset split routing, and policy routing. No built-in routing.

```bash
sudo vtx-gateway \
  --brokers=mqtts://mqtt.example.com:8883,mqtts://mqtt2.example.com:8883 \
  --name=r3s \
  --exit=sto \
  --pass=secret

# With YAML config
sudo vtx-gateway --config=/etc/vertex.yaml
```

#### Flags

| Flag | Type | Default | Description |
|------|------|---------|-------------|
| `--config` | string | | YAML config file |
| `--brokers` | string | | Comma-separated broker URLs |
| `--broker` | string | | Single broker URL (backward compat) |
| `--name` | string | *required* | Client name (e.g. `r3s`) |
| `--exit` | string | *auto* | Exit node ID; omit for auto-select |
| `--pass` | string | | MQTT password |

### vtx-exit

Exit node -- creates TUN, sets up NAT, assigns IPs to clients. Connects to all brokers in the list. Publishes discovery heartbeats.

```bash
sudo vtx-exit \
  --brokers=mqtts://mqtt.example.com:8883,mqtts://mqtt2.example.com:8883 \
  --id=mtl \
  --tun-ip=10.9.0.1/24 \
  --user=vtx-exit-mtl \
  --pass=secret \
  --dh-key=<hex-encoded-x25519-private-key> \
  --country=CA \
  --max-clients=50

# With YAML config
sudo vtx-exit --config=/etc/vertex.yaml
```

#### Flags

| Flag | Type | Default | Description |
|------|------|---------|-------------|
| `--config` | string | | YAML config file |
| `--brokers` | string | | Comma-separated broker URLs |
| `--broker` | string | | Single broker URL (backward compat) |
| `--id` | string | `mtl` | Exit node ID |
| `--tun-ip` | string | `10.9.0.1/24` | TUN interface IP/CIDR |
| `--user` | string | | MQTT username (typically `vtx-exit-{id}`) |
| `--pass` | string | | MQTT password |
| `--dh-key` | string | *auto* | Hex-encoded X25519 private key (generate with `vtx-admin gen-dh-key`) |
| `--stats-file` | string | `/var/lib/vtx-stats.json` | Path to stats JSON file |
| `--country` | string | | Country code for discovery (e.g. `CA`, `IS`) |
| `--max-clients` | int | 50 | Max clients for load balancing score |

#### Exit NAT Setup

The exit automatically configures:

- MASQUERADE for all RFC1918 ranges (10/8, 172.16/12, 192.168/16)
- ip_forward=1, rp_filter=0 on TUN
- Private subnet routes through TUN
- MSS clamping (`--clamp-mss-to-pmtu`)
- SSH safety rule (`-I INPUT 1 -p tcp --dport 22 -j ACCEPT`)

### vtx-admin

User and ACL management for Mosquitto. Run on the broker server where passwd/ACL files are stored.

```bash
# Create exit user + ACL
vtx-admin add-exit --id=mtl

# Generate X25519 keypair for exit DH config
vtx-admin gen-dh-key

# Create client (wildcard ACL -- access to all exits)
vtx-admin add --name=mac

# Create client (single exit, tighter ACL)
vtx-admin add --name=r3s --exits=sto

# Remove user
vtx-admin remove --name=laptop

# List / show users
vtx-admin list
vtx-admin show --name=r3s

# Sync credentials to all brokers (SCP + reload Mosquitto)
vtx-admin sync --brokers=yc,sber

# View per-client traffic stats (today/month/total)
vtx-admin stats
```

#### Username Format

- Clients: `vtx-client-{name}` (exit-independent, same credentials work across all exits)
- Exits: `vtx-exit-{id}`

## YAML Config

All components support `--config=path.yaml`. CLI flags override config file values.

### Client / Gateway

```yaml
domain: example.com                      # SRV-based discovery (recommended)
brokers:                                 # bootstrap fallback if SRV resolution fails
  - mqtts://mqtt-yc.example.com:8883     # MQTTS primary
  - mqtts://mqtt-sber.example.com:8883   # MQTTS secondary
  - wss://mqtt-yc.example.com:443        # WSS DPI fallback
  - wss://mqtt-sber.example.com:443      # WSS DPI fallback
name: mac
pass: "secret"
# exit: sto                              # omit for auto-select
# identity-key: ~/.config/vertex/identity.key  # default on macOS
# verbose: false
# json: false
```

### Exit

```yaml
brokers:
  - mqtts://mqtt-yc.example.com:8883
  - mqtts://mqtt-sber.example.com:8883
id: mtl
tun-ip: 10.9.0.1/24
user: vtx-exit-mtl
pass: "secret"
dh-private-key: "abcdef1234..."           # X25519 private key (hex, from vtx-admin gen-dh-key)
country: CA
max-clients: 50
require-identity: true                     # reject clients without device identity
# stats-file: /var/lib/vtx-stats.json     # default
```

### Gateway Environment (vtx-proxy.sh)

`vtx-proxy.sh` reads site-specific values from `/etc/vertex.env`:

```env
VTX_LAN_SUBNET=10.0.0.0/24               # LAN subnet (for MASQUERADE)
VTX_WAN_IFACE=eth0                         # WAN interface
VTX_TUN_IFACE=vtx0                         # TUN interface (default: tun0)
# VTX_BROKER=IP,IP                         # optional override; by default broker IPs are
                                           # resolved via SRV → cache → yaml `brokers:`
```

Requires `dig` (`apt install dnsutils`).

## MQTT Topics

```
vpn/{exit-id}/{client-name}/out       client -> exit (encrypted data)
vpn/{exit-id}/{client-name}/in        exit -> client (encrypted data)
vpn/{exit-id}/{client-name}/control   exit -> client (IP assignment, errors, alerts)
vpn/{exit-id}/control/join            client -> exit (join request + DH pubkey + identity proof)
discovery/exits/{exit-id}             exit -> all (retained heartbeat, LWT offline)
```

### MQTT Parameters

| Parameter | Value | Rationale |
|-----------|-------|-----------|
| QoS | 0 (fire-and-forget) | TCP retransmits; QoS 1 causes duplicates and blocking |
| Clean Start | true | Stale IP packets are useless |
| Session Expiry | 0 | No session storage needed |
| Retained | false (always) | Exception: `discovery/exits/{id}` heartbeats |
| Keepalive | 30s | 60s caused PINGRESP timeout through middleboxes |
| Message Expiry | 10s | MQTT 5.0 feature; drops stale packets |
| Max Packet Size | 1700 | 1500 MTU + 28B crypto + MQTT headers |
| Persistence | false | QoS 0 + no retained data = no disk I/O |

## E2E Encryption

X25519 DH key exchange during join handshake + ChaCha20-Poly1305 AEAD. Per-client session keys with Perfect Forward Secrecy.

**How it works:**

1. Client generates ephemeral X25519 keypair, sends pubkey in join request
2. Exit performs ECDH with its static private key and client's ephemeral pubkey
3. Both sides derive symmetric key via HKDF-SHA256
4. All data packets encrypted with ChaCha20-Poly1305 (12B nonce + payload + 16B tag)
5. New ephemeral key on every connect/exit switch (session-level PFS)

**Wire format:** `[12B random nonce][encrypted payload][16B auth tag]`

**Configuration:**

- Client/Gateway: no E2E configuration needed -- DH is fully automatic
- Exit: needs a persistent X25519 private key (`dh-private-key` in config, or auto-generated with warning)

```bash
# Generate DH keypair for exit
vtx-admin gen-dh-key
```

## Device Identity

WireGuard-style persistent X25519 keypair per device, TOFU registration.

**How it works:**

1. Client auto-generates a persistent identity key on first run
2. On join, client computes `HMAC-SHA256(ECDH(identity_priv, exit_pub), "vtx-identity-v1" + name)` as proof
3. Exit verifies the proof, then checks TOFU store
4. First connection: registered (Trust On First Use)
5. Subsequent connections: must match stored pubkey or rejected

**Key storage:**

| Component | Location |
|-----------|----------|
| Client (macOS) | `~/.config/vertex/identity.key` |
| Gateway (Linux) | `/etc/vertex/identity.key` |
| Exit key store | `/var/lib/vtx-devices.json` |

**Reset a device:** Delete the entry from `vtx-devices.json` on the exit. The next connection will TOFU-register again.

## Performance

| Scenario | Down | Up |
|----------|------|------|
| Gateway (R3S -> AWS-Canada, Ethernet, 2026-04 baseline) | 42 Mbps | 73 Mbps |
| macOS client (Ethernet) | 86 Mbps | 79 Mbps |
| Docker loopback | 1.17 Gbps | 1.17 Gbps |

| Operation | Latency |
|-----------|---------|
| DH key derivation | 28 us |
| ChaCha20 Seal (1500B) | 1.77 us |
| ChaCha20 Open (1500B) | 1.57 us |
| Broker failover | ~100 ms |
| Reconnect (broker restart) | <1 s |
| Reconnect (exit/client restart) | <10 s |

Benchmarks measured on Apple M3 Pro (crypto) and production deployment (throughput).

## Project Structure

Project is grouped by **role**, not language: everything that runs on a
broker/exit host is under `server/`, everything that connects out to a
broker is under `clients/`. Shared Go packages stay at root (`pkg/`)
because they're consumed by both server and Go clients.

```
server/
  cmd/
    exit/              Exit node (vtx-exit): TUN + NAT + multi-client + multi-broker + discovery
    admin/             vtx-admin: User/ACL management + credential sync + DH keygen + stats

clients/
  cli/                 Universal Go client (vtx-client), routing + auto-select + JSON events
  gateway/             Linux router (vtx-gateway, R3S) + r3s/vtx-proxy.sh transparent proxy
  ios/                 iPhone/iPad app (NEPacketTunnelProvider + SwiftUI)
  macos/               Mac app (NEPacketTunnelProvider + SwiftUI)
  android/             Native Kotlin app (Compose + VpnService), multi-module :app/:core/:vpn — Phase 1-3 done; PLAN.md
  shared/VertexCore/   Swift Package shared between iOS and macOS (CryptoKit + NWConnection MQTT)

pkg/
  transport/           Transport interface (Publish, Subscribe, Ready, Close)
    mqtt/              MQTT 5.0 (paho.golang autopaho), multi-URL failover, LWT
    mqtt/wsconn.go     WebSocket net.Conn wrapper (one frame per MQTT packet)
  config/              YAML config + broker list parsing + helpers
  discovery/           Exit discovery (heartbeat, scoring, auto-select, health check)
  crypto/              E2E encryption (X25519 DH + ChaCha20-Poly1305 + HKDF)
  identity/            Device identity (TOFU, HMAC proof, KeyStore)
  vpn/                 TUN device (Linux raw syscall / macOS water), checksum fix, MTU
  routing/             Platform routing: bypass brokers + default via TUN (macOS/Linux)
  events/              JSON event emitter for native wrappers (Swift, Kotlin)

test/
  docker/              Docker integration test configs (Mosquitto, ACL, passwd, identity key)
```

## Transport Interface

The MQTT transport is abstracted behind a `Transport` interface, making it possible to swap in NATS, gRPC, or other transports:

```go
type Transport interface {
    Publish(topic string, data []byte) error
    Subscribe(pattern string, handler func(topic string, data []byte)) error
    Ready() bool
    Close() error
}
```

## License

Private project.

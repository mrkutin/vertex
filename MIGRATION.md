# Migration history: broker-tunnel → vertex (DONE)

Historical record of the phased rename from `broker-tunnel` / `bt-*` to `vertex` / `vtx-*`. Migration is complete except for one wire-protocol identifier intentionally retained for compatibility (HKDF info string `"broker-tunnel-v1"`).

**Status:**
- Phase 0 (brand) — DONE 2026-04-25
- Phase 1 (code rename, Docker only) — DONE 2026-04-26
- Phase 2A (Go-side: module path, paths, SRV names, VTX_* envs, VTX_FORWARD chain, vtx-proxy.sh, Makefile, docker-compose.yml, docs) — DONE 2026-04-26
- Phase 2B (iOS/macOS folder rename, struct/file renames, Bundle IDs `ru.vertices(.tunnel)` on `vertices.ru` reverse-DNS, App Group, Logger subsystems, Keychain) — DONE 2026-04-26
- Phase 2C (production credential rollout) — DONE 2026-04-26: all clients (iphone/mac/r3s) and exits (aws/sto) running on `vtx-*`; legacy `bt-*` users removed from brokers in Phase 4
- Phase 3a (DNS migration `4few.ru` → `vertices.ru`, Let's Encrypt cert reissue) — DONE 2026-04-26
- Phase 3b (Gitea repo + local folder rename) — DONE 2026-04-26
- Phase 4 — DONE 2026-04-26: Swift package `VertexCore` (DONE 2026-04-25); legacy `bt-identity-v1` HMAC fallback removed from Go pkg/identity and Swift IdentityKey; `bt-client-` parser dropped from cmd/admin; legacy `bt-*` Mosquitto users + ACL blocks removed from brokers; SemVer infrastructure introduced (`-X main.Version` in Go, `MARKETING_VERSION` 2.0.0 in iOS, `make build/deploy` ldflags); shipped as **v2.0.0**

SPB broker (Timeweb) and RVK exit (1984.hosting) decommissioned 2026-04-26 — out of rotation.

AWS Canada exit renamed `aws` → `mtl` (Montreal — the actual ca-central-1 city, not Toronto as the old TXT mis-stated) and decommissioned 2026-05-22. Snapshot of yaml + identity DB + systemd unit lives at `~/Backups/vertex/mtl-exit/latest/` outside the repo; `vtx-exit-mtl` MQTT user retained on all 3 brokers; AWS instance terminated. Restore via `make restore-mtl HOST=<ssh-alias>` followed by SRV+TXT records for `mtl.exit.vertices.ru`. Backup pre-dates this rename (sealed at v2.4.0) and carries `id: mtl`, `user: vtx-exit-mtl`, original DH private key, and TUN subnet `10.9.0.0/24` for byte-identical restore.

Twb broker decommissioned 2026-05-22 after Timeweb-Moscow → AWS peering degraded (RTT 400-2400ms vs 25ms via yc/sber). Snapshot of mosquitto.conf + vertex.conf + Let's Encrypt cert (live+archive+renewal) + bt_passwd/bt_acl + systemd override at `~/Backups/vertex/twb-broker/latest/`; SRV records for twb removed; A record `mqtt-twb.vertices.ru` removed; Timeweb VM terminated. `vtx-exit-mtl` (and other) MQTT users were synced to twb but are also present on the live brokers — no broker-side state lost. Cert (CN=mqtt-twb.vertices.ru) expires 2026-08-16; if restored later, prefer Let's Encrypt re-issue (clean 90-day cycle) over reusing the backup cert. RESTORE.md inside the snapshot covers the full procedure.

vtx-exit (Go binary, v2.5.0 2026-05-22): SRV discovery wired into `server/cmd/exit/main.go`. With `domain:` in YAML, exits resolve `_mqtt._tcp.<domain>` at startup the same way clients do (yaml `brokers:` retained as bootstrap fallback only). This eliminates broker-list drift between exit YAMLs and DNS — adding/removing a broker is now a DNS edit + exit restart.

**Wire-protocol leftover (intentionally retained):** HKDF info string `"broker-tunnel-v1"` in `pkg/crypto` and Swift `SessionCrypto`. Changing it produces different session keys and would require a coordinated flag-day across all clients. Bump to `"vertex-v2"` is a separate decision when wire-protocol v2 is otherwise needed.

## Scope: what changes

### Code (cmd/* and pkg/*)

| Old | New |
|-----|-----|
| `cmd/client/` (binary `bt-client`) | `cmd/client/` (binary `vtx-client`) |
| `cmd/exit/` (binary `bt-exit`) | `cmd/exit/` (binary `vtx-exit`) |
| `cmd/gateway/` (binary `bt-gateway`) | `cmd/gateway/` (binary `vtx-gateway`) |
| `cmd/admin/` (binary `bt-admin`) | `cmd/admin/` (binary `vtx-admin`) |
| Default TUN name `bt0` | `vtx0` |
| MQTT clientID prefix `bt-client-` / `bt-exit-` | `vtx-client-` / `vtx-exit-` |
| MQTT username prefix `bt-client-{name}` | `vtx-client-{name}` |
| Identity HMAC label `"bt-identity-v1"` | `"vtx-identity-v1"` (BREAKS TOFU — needs key migration on exits) |

### Server-side

| Old | New |
|-----|-----|
| `/etc/broker-tunnel.yaml` | `/etc/vertex.yaml` |
| `/etc/broker-tunnel.env` | `/etc/vertex.env` |
| `/etc/broker-tunnel/identity.key` | `/etc/vertex/identity.key` |
| `/var/lib/bt-devices.json` | `/var/lib/vtx-devices.json` |
| `/usr/local/bin/bt-{client,exit,gateway,admin}` | `/usr/local/bin/vtx-*` |
| systemd `bt-gateway.service` | `vtx-gateway.service` |
| systemd `bt-exit.service` | `vtx-exit.service` |
| `bt-proxy.sh` script + `BT_*` env vars | `vtx-proxy.sh` + `VTX_*` env vars |

### Mosquitto

| Old | New |
|-----|-----|
| User `bt-client-{name}` | `vtx-client-{name}` |
| User `bt-exit-{id}` | `vtx-exit-{id}` |
| Topic prefix `bt/{exit}/...` | `vtx/{exit}/...` (if used; verify in ACL) |
| ACL file `aclfile`: bt-* patterns | vtx-* patterns |

### iOS / macOS

The brand domain is `vertices.ru`. Bundle IDs follow Apple's reverse-DNS convention rooted at that domain.

| Old | New |
|-----|-----|
| Display name "Broker Tunnel" | "Vertex" (DONE 2026-04-25) |
| `PRODUCT_NAME` in project.yml | "Vertex" (DONE 2026-04-25) |
| Bundle ID `com.4few.brokertunnel` | `ru.vertices` (DONE 2026-04-26) |
| Bundle ID `com.4few.brokertunnel.tunnel` | `ru.vertices.tunnel` (DONE 2026-04-26) |
| Logger subsystem `com.4few.brokertunnel` | `ru.vertices` (DONE 2026-04-26) |
| App Group `group.com.4few.brokertunnel` | `group.ru.vertices` (DONE 2026-04-26) |
| Keychain service / access group | `ru.vertices` / `group.ru.vertices` (DONE 2026-04-26 in `VertexCore.KeychainStore`) |
| Swift package `BrokerTunnelCore` | `VertexCore` (DONE 2026-04-25, Phase 4 complete) |
| `BrokerTunnelApp` struct + filename | `VertexApp` (DONE 2026-04-26, Phase 2B) |
| Folders `clients/{ios,macos}/BrokerTunnel/` | `clients/{ios,macos}/Vertex/` (DONE 2026-04-26) |

**Bundle ID change consequence:** the iOS/macOS app installed under the old `com.4few.brokertunnel` ID is treated by iOS/macOS as a *different* app from the new `ru.vertices`. After deploying the new build, users must:
- Delete the old "Vertex" app icon from the device (it'll still be there with the old Bundle ID under the hood)
- Install the new build (iOS asks "Allow VPN configuration" again — fresh permission grant)
- Re-enter the Mosquitto password (Keychain doesn't transfer across Bundle IDs)
- Identity key auto-regenerates fresh — exit's TOFU rejects the new key with `unregistered`. Run `vtx-admin reset-device <name>` on each exit, then reconnect

**Apple Developer registration:** new App IDs need to be registered in `developer.apple.com` before signed builds work:
- `ru.vertices` (App ID, Network Extensions capability)
- `ru.vertices.tunnel` (App ID, Network Extensions + System Extension if macOS)
- `group.ru.vertices` (App Group)

### Repo / folder

| Old | New |
|-----|-----|
| `~/Projects/broker-tunnel/` | `~/Projects/vertex/` |
| Claude Code memory dir `-Users-mrkutin-Projects-broker-tunnel/` | `-Users-mrkutin-Projects-vertex/` (rename in lockstep) |
| Git remote `broker-tunnel` repo | `vertex` repo (Gitea rename + update remote URL) |

## Phases

### Phase 0 (DONE 2026-04-25)

- [x] BRAND.md saved
- [x] iOS/macOS app display names → "Vertex"
- [x] iOS/macOS user-visible UI strings updated
- [x] Project CLAUDE.md and README.md updated with brand notes
- [x] Memory entry added: `project_vertex_brand.md`

### Phase 1 — Code rename (Docker-test only, no production) — DONE 2026-04-26

1. ~~Rename Go binaries in Makefile: `bt-*` → `vtx-*`~~ DONE
2. ~~Update `MQTTClientIDPrefix`, default TUN name, etc. in Go source~~ DONE
3. ~~Add `vtx-identity-v1` HMAC label alongside `bt-identity-v1` (compat mode for TOFU rotation)~~ DONE
4. ~~Run full Docker integration test (`./test-docker.sh`)~~ DONE — all 44/44 scenarios pass
5. ~~Verify all 6 Docker scenarios pass: explicit-aws, explicit-ams, auto-select, multi-broker failover, identity-required, broker-restart~~ DONE
6. ~~Commit but **do not deploy**~~ DONE

### Phase 2A — Go-side full rename (Docker-test only) — DONE 2026-04-26

1. Module path `github.com/mrkutin/broker-tunnel` → `github.com/mrkutin/vertex` (go.mod + every Go import)
2. Server file path defaults: `/etc/vertex.{yaml,env}`, `/etc/vertex/identity.key`, `/var/lib/vtx-{stats,devices}.json`, `~/.config/vertex/identity.key`
3. DNS SRV record names: `_vtx-exit._tcp` / `_vtx-backup._tcp` (in `pkg/discovery`, `cmd/admin` invite output, tests)
4. Admin tool env vars: `VTX_PASSWD_FILE`, `VTX_ACL_FILE`, `VTX_STATS_FILE`. Default file paths now `vtx_passwd`, `vtx_acl`, `vtx-stats.json`
5. iptables chain `BT_FORWARD` → `VTX_FORWARD` (gateway Go source + r3s shell script)
6. Renamed `configs/r3s/bt-proxy.sh` → `configs/r3s/vtx-proxy.sh` (env vars `VTX_*`, paths `/etc/vertex.env`, `/var/lib/vtx-ru-nets.zone`)
7. Makefile deploy targets use `/usr/local/bin/vtx-*` and `vtx-{exit,gateway}.service`
8. `docker-compose.yml` mounts `/root/.config/vertex/identity.key`
9. Docs (CLAUDE.md, README.md, DEPLOY.md, BRAND.md, MIGRATION.md) updated to new paths/binaries; legacy `bt-*` mentions reduced to historical-context one-liners
10. test-docker.sh expects only `vtx-*` (the legacy `(bt|vtx)-` regexes removed since the Docker config is fully migrated)
11. Backward-compat preserved: `cmd/admin` still parses both `bt-client-`/`vtx-client-` prefixes; `pkg/identity.VerifyIdentityProof` still accepts the legacy `bt-identity-v1` label. These are the migration safety net — kept intentionally
12. HKDF info string `"broker-tunnel-v1"` (in `pkg/crypto`) is on the wire and is intentionally NOT renamed — would break decryption with deployed iOS/macOS clients

### Phase 2B — iOS/macOS bundle rename (DONE 2026-04-26)

- [x] Folders `clients/{ios,macos}/BrokerTunnel/` → `clients/{ios,macos}/Vertex/`
- [x] `BrokerTunnelApp.swift` → `VertexApp.swift`; `BrokerTunnel.entitlements` → `Vertex.entitlements`
- [x] Xcode project regenerated as `Vertex.xcodeproj` (`name: Vertex` in `project.yml`, scheme/target/tunnel-target renamed)
- [x] Bundle IDs `com.4few.brokertunnel(.tunnel)` → `ru.vertices(.tunnel)` (reverse-DNS rooted at the new brand domain `vertices.ru`)
- [x] App Group `group.com.4few.brokertunnel` → `group.ru.vertices` (in App + Tunnel entitlements, both platforms)
- [x] Logger subsystems `com.4few.brokertunnel*` → `ru.vertices*`
- [x] Keychain service / accessGroup in `VertexCore.KeychainStore` → `ru.vertices` / `group.ru.vertices` (load-bearing — must match entitlements)
- [x] Wire-protocol Swift mirrors Go side: PacketTunnelProvider emits `vtx-client-{name}` MQTT username; SRVDiscovery resolves `_vtx-exit._tcp` / `_vtx-backup._tcp`
- [x] iOS Simulator + macOS Debug builds verified
- [x] iPhone Release build with `-allowProvisioningUpdates` auto-creates the new App IDs in Apple Developer (requires Xcode logged into Apple ID associated with team `J698685529`)
- [x] iPhone install + launch verified under new Bundle ID `ru.vertices` (treated as a fresh app — old `com.4few.brokertunnel` Vertex stays as separate icon until manually deleted)

### Phase 2C — Production credential rollout — DONE 2026-04-26

- [x] `vtx-client-iphone` added to all brokers alongside legacy `bt-client-iphone`, same password hash, dedicated ACL block. Mosquitto reload via `kill -HUP $(pidof mosquitto)`
- [x] TOFU `iphone` entry removed from `/var/lib/bt-devices.json` on AWS, STO; `bt-exit` restarted. New iPhone Vertex auto-registers fresh identity on first connect
- [x] iPhone (new Bundle ID `ru.vertices`) verified working end-to-end
- [x] macOS Vertex rebuilt under new Bundle ID `ru.vertices`; `vtx-client-mac` added; TOFU `mac` reset; verified
- [x] **Exits (AWS / STO)** — `vtx-exit` deployed, `/etc/vertex.yaml` + `/var/lib/vtx-devices.json` populated from legacy files, `vtx-exit.service` enabled, `bt-exit.service` disabled; `vtx-exit-{aws,sto}` users added to brokers; identity store reused so existing TOFU pubkeys recognised
- [x] **R3S gateway** — `vtx-gateway` deployed, `vtx-proxy.sh` installed, `/etc/vertex.{yaml,env}` with `VTX_*` envs, identity key copied to `/etc/vertex/identity.key` (same key bytes — TOFU preserved), TUN renamed `bt0` → `vtx0`, iptables chain switched `BT_FORWARD` → `VTX_FORWARD`. `vtx-client-r3s` user added to brokers. End-to-end verified: R3S → broker (yc) → STO exit, DH PFS, IP 10.9.2.2, split routing 8585 RU subnets

**Estimated downtime per device: ~30 sec** during binary swap + reconnect (rolling, both legacy and new users coexisted on brokers throughout).

### Phase 3 — DNS migration + repo/folder rename — DONE 2026-04-26

#### 3a. DNS migration from 4few.ru to vertices.ru — DONE 2026-04-26

The discovery domain is a config knob (`domain:` field in `/etc/vertex.yaml`, `--domain` CLI flag, `domain=` invite URL parameter, "Discovery domain" field in iOS/macOS Settings). The migration was a DNS records + config swap with no code change.

**Final state — `vertices.ru` zone (Timeweb NS):**

```
mqtt-yc.vertices.ru.           A    51.250.12.145
mqtt-sber.vertices.ru.         A    37.230.192.188

_mqtt._tcp.vertices.ru.        SRV  10  0  8883  mqtt-yc.vertices.ru.    ; primary mqtts
_mqtt._tcp.vertices.ru.        SRV  20  0  8883  mqtt-sber.vertices.ru.  ; backup mqtts
_mqtt._tcp.vertices.ru.        SRV  30  0  443   mqtt-yc.vertices.ru.    ; primary wss
_mqtt._tcp.vertices.ru.        SRV  40  0  443   mqtt-sber.vertices.ru.  ; backup wss

_vtx-exit._tcp.vertices.ru.    SRV  10  0  1     aws.exit.vertices.ru.   ; handle, no A
_vtx-exit._tcp.vertices.ru.    SRV  10  0  1     sto.exit.vertices.ru.   ; handle, no A

_vtx-backup._tcp.vertices.ru.  SRV  10  0  1     4few.ru.                ; chain-of-trust
```

`*.exit.vertices.ru` targets have no A records — `pkg/discovery.extractExitID` only takes the first label as exit ID, no TCP connection ever happens to them.

**4few.ru zone** is left as-is (no `_vtx-*` records) — legacy clients still on `bt-*` Bundle IDs / hostnames have already been migrated. SPB and RVK A-records on 4few.ru deleted by user.

**Cert reissue (Let's Encrypt, certbot --standalone):**
- yc broker: new cert for `mqtt-yc.vertices.ru`; old `mqtt-yc.4few.ru` cert deleted
- sber broker: new cert for `mqtt-sber.vertices.ru`; old `mqtt-msk.4few.ru` and `mqtt.4few.ru` certs deleted
- Mosquitto deploy hooks updated to chown/chmod the new cert paths and `systemctl restart mosquitto` on renewal

**Chain-of-trust fallback** (`pkg/discovery.ResolveWithFallback`) — if `vertices.ru` SRV resolution fails, clients fall back to whatever `_vtx-backup._tcp.vertices.ru` advertises (currently `4few.ru`).

#### 3b. Gitea repo + folder rename — DONE 2026-04-26

- [x] Gitea repo `mrkutin/broker-tunnel` → `mrkutin/vertex` (web UI Settings → Rename)
- [x] Local clone: `mv ~/Projects/broker-tunnel ~/Projects/vertex`
- [x] Git remote: `git remote set-url origin ssh://git@git.it-laboratory.com:2222/mrkutin/vertex.git`
- [x] Claude Code memory dir: `mv ~/.claude/projects/-Users-mrkutin-Projects-broker-tunnel ~/.claude/projects/-Users-mrkutin-Projects-vertex` (between sessions)
- [x] Go module path `github.com/mrkutin/vertex` already renamed in Phase 2A — `go get` from network resolves correctly after Gitea rename

### Phase 4 — Cleanup of backward-compat — DONE 2026-04-26 (code + brokers)

- [x] Removed `identityLabelLegacy = "bt-identity-v1"` fallback from `pkg/identity/identity.go` and the corresponding `TestVerifyProof_LegacyLabel` test. Swift `IdentityKey.proof()` updated to emit `vtx-identity-v1` label.
- [x] Removed `bt-client-` prefix branch from `cmd/admin/main.go` user parsing
- [x] Removed legacy `bt-client-*` and `bt-exit-*` Mosquitto users + ACL blocks on sber and yc brokers (backups at `bt_passwd.pre-bt-cleanup` / `bt_acl.pre-bt-cleanup`). `vtx-client-mac` re-added on both brokers (had been missing).
- [x] User cleaned `_bt-exit._tcp` SRV records from `4few.ru` zone (out of scope: `mqtt-spb.4few.ru` A-record and SPB/RVK servers themselves also decommissioned)
- [x] Docker integration test (`./test-docker.sh`) — 44/44 passed against new code

**Production deploy (DONE 2026-04-26 — v2.0.0):** R3S gateway, AWS exit, STO exit redeployed with new `vtx-*` binaries; iPhone Vertex installed via `xcrun devicectl` from Release archive; STO logs confirm joins for r3s + iphone with DH PFS, no rejects. macOS Vertex left on v1.0.0 by user choice — its `bt-identity-v1` proofs are now rejected by new exits, will reconnect once user manually rebuilds. SemVer infrastructure: `-X main.Version` ldflags via Makefile `VERSION ?= 2.0.0`, `--version` flag in all four `cmd/` binaries; iOS `MARKETING_VERSION=2.0.0`, `CURRENT_PROJECT_VERSION=2`. Git tag `v2.0.0` pushed.

**Deferred:**
- [ ] macOS Vertex rebuild + reinstall (when user is ready) — requires manual Xcode session
- [ ] Optionally: wire-protocol bump and HKDF info string `"broker-tunnel-v1"` → `"vertex-v2"` (requires flag-day; HKDF string change cannot be backward-compat'd)
- [ ] Cleanup `bt_passwd.pre-*` / `bt_acl.pre-*` backup files on sber, yc (rollback safety net, can be removed any time)

### Phase 4 — Internal Swift package rename (DONE 2026-04-25)

- [x] `BrokerTunnelCore` → `VertexCore`
- [x] All `import BrokerTunnelCore` → `import VertexCore` across iOS + macOS Swift sources (14 files)
- [x] `clients/shared/BrokerTunnelCore/` → `clients/shared/VertexCore/` (incl. `Sources/VertexCore/` and `Tests/VertexCoreTests/`)
- [x] `Package.swift` updated (name, library, target, testTarget)
- [x] Both `project.yml` updated (`packages:` block + dependency references)
- [x] iOS `README.md` reference paths updated
- [x] Project `CLAUDE.md` and memory entries updated
- [x] xcodeproj regenerated on macOS + iOS (xcodegen)

## Docker test checklist (gate before any production deploy)

- `make test` — runs `./test-docker.sh`, full 44-scenario integration test (covers explicit-aws/ams, auto-select, multi-broker failover, identity-required reject, broker restart resilience, WSS fallback, throughput)
- `go test ./...` — unit tests (`pkg/config`, `pkg/crypto`, `pkg/discovery`, `pkg/identity`)
- iOS: `make archive-ios` (Release config) + install on physical iPhone via `xcrun devicectl device install app`
- macOS: `make build-macos -allowProvisioningUpdates` + manual install/connect cycle

## What is NOT migrating (intentional)

- **HKDF info string** `"broker-tunnel-v1"` in `pkg/crypto/crypto.go` and `VertexCore.SessionCrypto` — used to derive ChaCha20-Poly1305 session keys via HKDF-SHA256. Changing it produces different session keys with every peer. Retained for wire-protocol v1; can only be retired in a coordinated v2 flag-day.

## Future client implementations

The Android client (`clients/android/`, native Kotlin, in development) uses the same wire-protocol identifiers as the iOS Swift / Go references: HKDF info string `"broker-tunnel-v1"`, identity HMAC label `"vtx-identity-v1"`, encrypted packet format `[12B nonce][ciphertext][16B tag]`. It is a from-scratch Kotlin port of `clients/shared/VertexCore/` — no gomobile, no JNI, no Kotlin Multiplatform. Cross-language wire-format regression tests (planned in `test/fixtures/wire-format/`) will share JSON fixtures across Go, Swift, and Kotlin to keep the three implementations byte-exact.

## References

- `BRAND.md` — public brand identity, iconography, naming line (V₀/V₁/V₂, E₀/E₁/E₂)
- `feedback_docker_before_deploy.md` (memory) — Docker test mandatory before deploy
- `feedback_deploy_explicit.md` (memory) — never deploy without explicit user instruction
- `feedback_check_all_components.md` (memory) — verify ALL components during migration (binary, config, scripts)

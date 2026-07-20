# Vertex Android — implementation plan

Native Kotlin Android client. **No gomobile, no JNI to Go, no Kotlin Multiplatform.** Wire-protocol (MQTT 5.0 + X25519/HKDF/ChaCha20-Poly1305 + identity TOFU) is rewritten in Kotlin to be byte-exact compatible with the iOS Swift reference (`clients/shared/VertexCore/`) and the Go reference (`pkg/`). HKDF info string `"broker-tunnel-v1"` and identity HMAC label `"vtx-identity-v1"` are preserved.

iOS is the production-tested reference (memory `iOS = reference for macOS`); Android targets the same wire protocol and the same UX where Android best-practice allows.

## Decisions (locked)

- **Application ID:** `ru.vertices` (parity with iOS host app; Android has no separate extension package)
- **Distribution:** Gitea release + sideload APK only. **No Play Store, no F-Droid**
- **Localization:** English only (parity with iOS)
- **Android-extras enabled:** Quick Settings Tile (Phase 4). **Disabled:** Per-app VPN, Connect on boot, Material You / dynamic color
- **VPN routing model:** full-tunnel in Phase 1; RU-exclude split in Phase 3 (one global setting, not per-app)

## Tech stack

| | Choice | Rationale |
|---|---|---|
| minSdk / targetSdk / compileSdk | 26 / 35 / 35 | API 26 covers ~98% of active devices; VpnService API stable since 14; `Conscrypt`/BC fills X25519 on older APIs so no pressure for higher minSdk |
| Language | Kotlin 2.0+ | Coroutines + Flow (StateFlow/SharedFlow); kotlinx.serialization |
| UI | Jetpack Compose + Material 3 | No Material You — fixed brand palette |
| DI | Hilt (KSP, not KAPT) | `@AndroidEntryPoint` integrates cleanly with VpnService; KSP is 2-3× faster than KAPT |
| Network (WSS) | OkHttp 4.x WebSocket | Battle-tested binary frame; one MQTT packet per WS message |
| Network (MQTTS:8883) | `javax.net.ssl.SSLSocket` direct | Single long-lived socket — parity with Swift NWConnection approach |
| MQTT 5.0 | **Self-implemented** (port of Swift VertexCore) | HiveMQ pulls Netty + abstracts state machine; Paho is v3.1.1 stable only (v5 incubator). Our subset (CONNECT/CONNACK/PUBLISH(QoS 0)/SUBSCRIBE/SUBACK/PINGREQ/PINGRESP/DISCONNECT + Message Expiry property + retained flag) is ~600 LOC |
| Crypto | BouncyCastle (`bcprov-jdk15to18` 1.78+) for X25519/HKDF; `javax.crypto.Cipher` ChaCha20-Poly1305 on API 28+, BC fallback on API 26-27 | Tink rejected: abstracts salt/info — risk of byte-incompatibility with iOS/Go reference |
| HMAC-SHA256 | `javax.crypto.Mac` (platform) | All API levels |
| Identity priv-key storage | `EncryptedFile` (`androidx.security:security-crypto`) + master-key in Android Keystore | Same strategy as WireGuard Android; works on all API levels |
| MQTT password storage | `EncryptedSharedPreferences` | Single string is simpler than wrapping in DataStore |
| User prefs | DataStore Preferences | Modern replacement for SharedPreferences |
| RU CIDR list | File in `getFilesDir()`, bundled fallback in `assets/ru-aggregated.zone` | ~150KB APK size cost is acceptable |
| Logging | Timber + rolling file logger in `filesDir/logs/` (7×1MB) | MetricKit has no direct Android equivalent |
| DoH (SRV discovery) | OkHttp HTTPS GET to Cloudflare/Google + kotlinx.serialization | Parity with Swift `SRVDiscovery.swift` |
| Build | Gradle KTS + libs.versions.toml, AGP 8.7+, JDK 17 | — |

## Multi-module Gradle layout

```
clients/android/Vertex/
├── build.gradle.kts
├── settings.gradle.kts
├── gradle/libs.versions.toml
├── gradle.properties
├── app/      — UI (Compose), ViewModels, navigation, Application class
├── core/     — Pure Kotlin: MQTT codec/transport, crypto, wire-protocol data classes
│              with @SerialName parity to iOS/Go, DoH+SRV, RU CIDR parser, IPC types
└── vpn/      — VpnService, packet pipeline, foreground notification, Quick Settings Tile,
               IPC server, identity keystore-backed store
```

Dependencies: `:app` → `:core`, `:vpn`; `:vpn` → `:core`. `:core` is pure Kotlin where possible (no Android SDK imports).

## Package structure

Application ID: `ru.vertices`. Package root: `ru.vertices.android.{...}`.

`:app`:
```
ru.vertices.android.app                     (Application, MainActivity)
ru.vertices.android.ui.connect              (ConnectScreen, BigConnectButton, VertexHero, StatusPill, SpeedPill, StatRow)
ru.vertices.android.ui.settings             (SettingsScreen, IdentityKeyView, AboutView, DiagnosticsView)
ru.vertices.android.ui.pickers              (BrokerListView, ExitListView)
ru.vertices.android.ui.permissions          (PermissionScreen)
ru.vertices.android.ui.theme                (Theme, Colors, Typography, Shapes, Tokens)
ru.vertices.android.ui.navigation           (NavGraph)
ru.vertices.android.viewmodel
ru.vertices.android.repository              (TunnelRepository, IdentityRepository, DiscoveryRepository, RoutingRepository, ConfigRepository)
ru.vertices.android.di
```

`:core`:
```
ru.vertices.android.core.crypto             (SessionCrypto, IdentityKey, Hkdf, X25519, ChaChaPoly1305)
ru.vertices.android.core.mqtt               (MqttPacket [sealed], MqttPacketCodec, MqttConnection, MqttTransport, MqttSocket [interface], TlsMqttSocket, WssMqttSocket)
ru.vertices.android.core.protocol           (JoinMessage, AssignMessage, DiscoveryHeartbeat, Topics)
ru.vertices.android.core.config             (BrokerUrl, TunnelConfig)
ru.vertices.android.core.discovery          (DohClient, SrvRecord, SrvDiscovery, DiscoveryCache)
ru.vertices.android.core.routing            (CidrParser, RuNetsLoader, RouteSet)
ru.vertices.android.core.ipc                (IpcMessage, IpcEvent, ConnectionStatus, TunnelStats, TunnelErrorReport)
ru.vertices.android.core.identity           (IdentityKeyStore [interface])
ru.vertices.android.core.util               (HexCodec, BigEndian)
```

`:vpn`:
```
ru.vertices.android.vpn                     (VertexVpnService, TunnelEngine, PacketPipeline, KeepaliveScheduler, LinkLivenessProbe)
ru.vertices.android.vpn.ipc                 (IpcServer)
ru.vertices.android.vpn.notify              (TunnelNotification, NotificationChannelSetup)
ru.vertices.android.vpn.routing             (VpnRouteBuilder, RuExclusionRoutes)
ru.vertices.android.vpn.identity            (KeystoreIdentityKeyStore)
ru.vertices.android.vpn.diag                (FileLogger, MemoryProbe)
ru.vertices.android.vpn.qs                  (VertexQuickSettingsTile — Phase 4)
```

## Wire-format byte-exact invariants

These MUST match the iOS/Go reference exactly:

| Item | Value |
|---|---|
| HKDF info | `"broker-tunnel-v1"` (legacy, retained for wire compat — see MIGRATION.md) |
| HKDF salt | `clientPub \|\| exitPub` (concatenation, 64 bytes) |
| HKDF output | 32 bytes (ChaCha20-Poly1305 key) |
| Identity HMAC label | `"vtx-identity-v1"` |
| Identity proof | `HMAC-SHA256(ECDH(identity_priv, exit_pub), "vtx-identity-v1" + name)` |
| Encrypted packet wire format | `[12B random nonce][ciphertext][16B Poly1305 tag]` |
| Crypto overhead | 28 bytes per packet (12 + 16) |
| MQTT keepAlive | 20 (parity with iOS Swift; Go uses 30 — Android matches iOS reference) |
| MQTT Clean Start | true |
| MQTT Session Expiry | 0 |
| MQTT Message Expiry property | 10 seconds |
| MQTT Retained | false (except discovery heartbeats) |
| MQTT QoS | 0 |
| MTU | 1500 |
| Max MQTT packet | 1700 (1500 MTU + 28 crypto + MQTT headers) |

JSON wire-protocol structures (use kotlinx.serialization with explicit `@SerialName`):

```kotlin
@Serializable
data class JoinMessage(
    @SerialName("name") val name: String,
    @SerialName("dh") val dh: String,           // base64 client ephemeral pubkey
    @SerialName("id") val id: String? = null,   // base64 client identity pubkey
    @SerialName("id_sig") val idSig: String? = null
)

@Serializable
data class AssignMessage(
    @SerialName("ip") val ip: String,
    @SerialName("gw") val gw: String,
    @SerialName("dh") val dh: String            // base64 exit static DH pubkey
)

@Serializable
data class DiscoveryHeartbeat(
    @SerialName("id") val id: String,
    @SerialName("country") val country: String,
    @SerialName("clients") val clients: Int,
    @SerialName("max_clients") val maxClients: Int,
    @SerialName("broker_rtt_ms") val brokerRttMs: Map<String, Int>? = null,
    @SerialName("uptime") val uptime: Long,
    @SerialName("ts") val ts: Long,
    @SerialName("dh_pubkey") val dhPubkey: String
)
```

MQTT topics (parity with `pkg/transport` and Swift `Topics.swift`):

```
vpn/{exit-id}/{client-name}/out      client → exit (encrypted)
vpn/{exit-id}/{client-name}/in       exit → client (encrypted)
vpn/{exit-id}/{client-name}/control  exit → client (assignments, errors)
vpn/{exit-id}/control/join           client → exit (handshake)
discovery/exits/+                    subscription pattern
discovery/exits/{exit-id}            specific exit's heartbeat (retained)
```

## VpnService model (Android specifics)

### Builder configuration (Phase 1, full-tunnel)

```
VpnService.Builder()
    .setSession("Vertex")
    .setMtu(1500)
    .addAddress(assignedIp, /24)         // from AssignMessage
    .addRoute("0.0.0.0", 0)              // full tunnel
    .addDnsServer("1.1.1.1")
    .addDnsServer("8.8.8.8")
    .setBlocking(false)
    .establish()                         // → ParcelFileDescriptor
```

Broker bypass on Android: call `protect(socket)` on the MQTT socket. Linux kernel's routing trick keeps protected sockets out of the VPN — broker traffic never recurses through the tunnel. **This is different from iOS, which uses `excludedRoutes` in `NEPacketTunnelNetworkSettings`.**

IPv6: not enabled in Phase 1 (parity with iOS). On read, inspect first nibble (IP version field) and skip IPv6 packets.

### Foreground service

Mandatory — without it API 26+ kills the VPN service after ~5 minutes background:

- NotificationChannel `vpn_channel`, IMPORTANCE_LOW, no sound
- `startForeground(NOTIF_ID, notification, FOREGROUND_SERVICE_TYPE_SYSTEM_EXEMPTED)` on API 34+
- Text: `"Vertex VPN — Connected via {exit} ({broker})"`, action: Disconnect
- Permission `POST_NOTIFICATIONS` (API 33+) requested at first run

### Packet pipeline

`establish()` returns `ParcelFileDescriptor`. Wrap with `FileInputStream` (read) and `FileOutputStream` (write).

- Read loop on dedicated thread `vtx-tun-up`: `read(buf)` → `sessionCrypto.seal(packet)` → `mqttTransport.publish(uploadTopic, sealed)`. Buffer size = MTU + 4 (Linux raw IP header).
- Write loop on dedicated thread `vtx-tun-down`: MQTT subscribe handler → `sessionCrypto.open(payload)` → `tunOut.write(decrypted)`. Single-writer keeps FD race-free.
- Coroutines NOT used on TUN IO (FileInputStream blocks; coroutines waste threads). MQTT callbacks stay in coroutine scope (StateFlow + suspend).

### Liveness and reconnect

Strict parity with iOS PacketTunnelProvider:

- **Keepalive:** re-publish join message every 60s (`kotlinx.coroutines.delay(60_000)` in a `launch`)
- **MQTT PINGREQ:** every 15s (keepAlive=20, ping=20−5)
- **PINGRESP timeout:** 5s → linkDead → escalate
- **Network change handling:** `ConnectivityManager.NetworkCallback` on default network. `onLost` marks stale; `onAvailable` triggers `MqttTransport.checkLiveness()` → ping → `forceReconnect` on timeout.
- **Reconnect strategy:** Android does NOT need `cancelTunnelWithError` ceremony (unlike iOS extension). The kernel tunnel persists across MQTT reconnect — we only re-establish the MQTT socket. After 3 consecutive connect failures, escalate to `stopSelf()`.

### Process separation

- **Phase 1:** same-process VpnService for fast MVP. IPC = Application-scope StateFlow singleton.
- **Phase 2:** `android:process=":vpn"` for memory isolation (mirrors iOS extension's 50MB sandbox). IPC moves to **Messenger** (cross-process Bundle messaging — simpler than full AIDL).

## Split routing (Phase 3)

Android `VpnService.Builder` does **not** support `excludedRoutes` until API 33. Two-tier strategy:

| API level | Approach |
|---|---|
| 33+ | `Builder.excludeRoute(IpPrefix(...))` — native excludedRoutes. Pass ~8500 RU CIDR from ipdeny.com |
| 26-32 | Complement-set algorithm (sing-box / Hiddify approach): invert RU CIDR → complementary set covering non-RU IPv4 → `addRoute()` for each non-RU CIDR |

Per-app VPN was rejected — single global RU-exclude toggle.

### RUNets loader

- Bundled fallback: `assets/ru-aggregated.zone` (~150KB)
- Bootstrap on first run: copy asset → `filesDir/ru-aggregated.zone`
- Refresh: OkHttp GET `https://www.ipdeny.com/ipblocks/data/aggregated/ru-aggregated.zone` → atomic replace
- Stats UI: mtime + line count

## UI / theme tokens

### Color mapping (iOS → Compose)

`ui.theme.VertexColors` data class — exact dup of iOS `Color+Vertex.swift`. Only dark mode (parity with iOS `preferredColorScheme(.dark)`).

| iOS Asset | Compose token | Use |
|---|---|---|
| `bgCanvas` | `Tokens.bgCanvas` | Root background |
| `bgCanvasTop` / `bgCanvasBottom` | `Tokens.bgCanvasTop/Bottom` | Gradient endpoints |
| `bgSurface` | `Tokens.bgSurface` | Card surface |
| `bgSurfaceElev` | `Tokens.bgSurfaceElev` | Raised surface |
| `bgSurfaceMuted` | `Tokens.bgSurfaceMuted` | Placeholder ghost |
| `borderSubtle/Strong` | `Tokens.borderSubtle/Strong` | Borders |
| `accentPrimary` / `Hover` / `Muted` | `Tokens.accentPrimary*` | CTA, brand |
| `glowPrimary/CoreHot/Warm` | `Tokens.glow*` | Halo effects |
| `text*` / `glyph*` | `Tokens.text*` / `Tokens.glyph*` | Text and glyphs |
| `state*` (Connected/Transitioning/Dormant/Error) | `Tokens.state*` | Status indicators |

Material 3 colorScheme bridge: primary=accentPrimary, surface=bgSurface, background=bgCanvas, error=stateError. Non-Material tokens (bgSurfaceElev, glow*, state*) live in extended `VertexColors` accessed via `MaterialTheme.colorScheme` extension.

**Dynamic color: explicitly disabled.** No `dynamicLightColorScheme()` / `dynamicDarkColorScheme()`. Brand consistency.

### Typography

| iOS | Android |
|---|---|
| SF Pro Rounded (heroStatus, brandWordmark) | Bundled **Google Sans Rounded** (downloadable) or **Geist Mono** for wordmark |
| SF Pro Text/Display (body, headline) | Material 3 default **Roboto** (system) |
| SF Mono (statValue, identityHex) | Bundled **JetBrains Mono** (~140KB ttf for regular+medium+semibold) |

### Icons

- Material Symbols via `androidx.compose.material.icons.extended`
- Custom V-asterisk glyphs (from iOS `Glyphs/`): convert SVG → Compose `ImageVector` via Vector Asset Studio

### Screens (parity with iOS)

| iOS view | Android equivalent |
|---|---|
| `RootView` + NavigationStack | `MainActivity` + Compose Navigation graph |
| `ConnectScreen` | `ConnectScreen` (hero + button + stats + error banner) |
| `SettingsScreen` | `SettingsScreen` (Identity / Discovery / Routing / Active Configuration) |
| `BrokerListView` | `BrokerListScreen` |
| `ExitListView` | `ExitListScreen` |
| `IdentityKeyView` | `IdentityKeyScreen` (16-hex fingerprint, 4 groups; long-press copy; reset confirmation) |
| `AboutView` | `AboutScreen` |
| `DiagnosticsView` (MetricKit) | `DiagnosticsScreen` (memory probe / battery / file log tail / export) |
| `PermissionDeniedView` | `PermissionScreen` (VpnService.prepare result + POST_NOTIFICATIONS prompt) |

### VertexHero animation

iOS uses ~2700 LOC Compose `Canvas` + `TimelineView(.animation)` for V-asterisk geometry with breath/pulse/reassert/edge-shimmer. Strategy:

- **Phase 1:** static V-asterisk SVG (no animation). Just identity-recognizable shape.
- **Phase 3:** animated Compose Canvas — `withFrameNanos` for time-based animation. Simplified breath/pulse without full port. State-keyed (dormant/connecting/connected/error). Honor `Settings.Global.TRANSITION_ANIMATION_SCALE == 0` (reduceMotion).

## App ↔ Service IPC

**Phase 1 (MVP):** Same-process. Application-scope `@Singleton` Hilt object exposing `StateFlow<ConnectionStatus>` and `StateFlow<TunnelStats>`. UI subscribes via ViewModel. Service writes via flow updates.

**Phase 2:** Move VpnService to `android:process=":vpn"`. Cross-process boundary requires Bundle marshaling — use **Messenger** (lighter than full AIDL):

| Direction | Message | Payload |
|---|---|---|
| App → Service | `RequestStatus` | — |
| App → Service | `RequestStats` | — |
| App → Service | `NotifyNetworkChanged` | hint that wifi appeared |
| Service → App | `Status` | `ConnectionStatus` JSON |
| Service → App | `Stats` | `TunnelStats` JSON |
| Service → App | `Error` | `TunnelErrorReport` JSON |

`TunnelManagerClient` in `:app` wraps bind/unbind, exposes `StateFlow` over the Messenger boundary.

## Identity / TOFU

- First run: generate X25519 keypair, store private key in `EncryptedFile("vtx-identity.bin", masterKey)` where `masterKey` lives in Android Keystore.
- `IdentityKeyView`: display first 16 hex chars of pubkey as `XXXX XXXX XXXX XXXX`; full 64 hex chars in expandable mono section. Long-press copies for admin reset.
- Reset flow: warning dialog → delete EncryptedFile → next connect TOFU-registers fresh key on exit (admin must `vtx-admin reset-device <name>` on each exit).

## SRV discovery (DoH)

Port of Swift `SRVDiscovery.swift`:

- `https://cloudflare-dns.com/dns-query?name=_mqtt._tcp.{domain}&type=SRV` with `Accept: application/dns-json`
- Fallback chain: Cloudflare → Google DoH → cached results → backup domain (`_vtx-backup._tcp`)
- Cache in DataStore Preferences (key `srv_discovery_cache_json`)
- JSON parsing via kotlinx.serialization

## Build & distribution

### Gradle structure

`libs.versions.toml` (target versions):

```
kotlin                = 2.0.21
agp                   = 8.7.x
compose-bom           = 2024.10.x
hilt                  = 2.52
okhttp                = 4.12.0
kotlinx-coroutines    = 1.9.x
kotlinx-serialization = 1.7.x
bouncycastle          = 1.78
androidx-security     = 1.1.0-alpha06
datastore             = 1.1.x
timber                = 5.0.1
```

### Signing

- Debug: auto debug keystore (`~/.android/debug.keystore`)
- Release: `~/.android/vertex.keystore` + env vars (see CREDENTIALS.md):
  - `VERTEX_ANDROID_KEYSTORE_PATH`
  - `VERTEX_ANDROID_KEYSTORE_PASSWORD`
  - `VERTEX_ANDROID_KEY_ALIAS`
  - `VERTEX_ANDROID_KEY_PASSWORD`

### Distribution

Gitea release only. APK uploaded as `Vertex-android-vX.Y.Z.apk` via `scripts/gitea-release.sh android-vX.Y.Z`. Users install via `adb install` or sideload.

### Versioning

- `versionName="X.Y.Z"`
- `versionCode = major*10000 + minor*100 + patch`
- Bump before every `make build-android-release` / `make release-android` (memory `semver_every_build`)
- Git tag: `android-vX.Y.Z` (parallel to `ios-vX.Y.Z`, `macos-vX.Y.Z`, `go-vX.Y.Z`)

## AndroidManifest permissions

```xml
<uses-permission android:name="android.permission.INTERNET"/>
<uses-permission android:name="android.permission.ACCESS_NETWORK_STATE"/>
<uses-permission android:name="android.permission.FOREGROUND_SERVICE"/>
<uses-permission android:name="android.permission.FOREGROUND_SERVICE_SYSTEM_EXEMPTED"/>  <!-- API 34+ -->
<uses-permission android:name="android.permission.POST_NOTIFICATIONS"/>                    <!-- API 33+ -->

<service
    android:name=".vpn.VertexVpnService"
    android:permission="android.permission.BIND_VPN_SERVICE"
    android:foregroundServiceType="systemExempted"
    android:exported="false">
    <intent-filter>
        <action android:name="android.net.VpnService"/>
    </intent-filter>
</service>

<!-- Phase 4 -->
<service
    android:name=".vpn.qs.VertexQuickSettingsTile"
    android:label="@string/quick_tile_label"
    android:icon="@drawable/ic_vertex_tile"
    android:permission="android.permission.BIND_QUICK_SETTINGS_TILE"
    android:exported="true">
    <intent-filter>
        <action android:name="android.service.quicksettings.action.QS_TILE"/>
    </intent-filter>
</service>
```

NOT requested (decisions): `RECEIVE_BOOT_COMPLETED` (no boot start), `QUERY_ALL_PACKAGES` (no per-app VPN), `REQUEST_IGNORE_BATTERY_OPTIMIZATIONS` (deferred — may add as opt-in onboarding step in Phase 4).

## Testing

### Unit tests (`:core/test/`, JVM, fast)

- **CryptoVectorsTest** — fixed test vectors (privKey, peer pubKey, expected shared, expected derived key, plaintext, nonce, expected ciphertext). Single JSON fixture in `test/fixtures/wire-format/` shared with Go and Swift tests.
- **MqttCodecTest** — encode/decode round-trip for every packet type
- **WireFormatTest** — JSON marshalling for JoinMessage / AssignMessage / DiscoveryHeartbeat with explicit field-name pinning
- **CidrParserTest** — same fixtures as Swift `CidrParser` tests
- **TopicMatchTest** — MQTT wildcards (`+`, `#`)

### Integration tests

- Robolectric for `TunnelEngineTest` with mock MQTT socket (full handshake simulation)
- Espresso instrumentation on emulator for `VpnPermissionFlowTest`

### Docker integration

- Bring up `test/docker/` stack
- Android emulator with `adb reverse tcp:8883 tcp:8883`
- Run TunnelEngine against `mqtts://localhost:8883`
- Verify connecting → connected → handshake → seal/open round-trip → disconnect

### Wire-format regressions

Single JSON fixture shared across Go / Swift / Kotlin tests. Add to `test/fixtures/wire-format/`:
- `join-v1.json`
- `assign-v1.json`
- `discovery-heartbeat-v1.json`
- `crypto-vectors-v1.json` (privKey, peerPub, expectedShared, expectedDerivedKey, plaintext, nonce, expectedCiphertext)

All three languages read these fixtures and verify byte-exact match.

## Roadmap

### Phase 1 — `android-v1.0.0` (MVP) — ✅ DONE 2026-04-29

- ✅ Multi-module skeleton (Gradle KTS, Hilt, Compose)
- ✅ `:core`: crypto (X25519/HKDF/ChaCha20-Poly1305), MQTT codec/transport, wire-protocol models, unit tests w/ shared fixtures
- ✅ `:vpn`: VertexVpnService with full-tunnel routing, foreground notification, packet pipeline, keepalive, liveness
- ✅ Identity TOFU: generate + store (Android Keystore + EncryptedFile) + proof
- ✅ IPC: same-process StateFlow singleton (`TunnelStateBus`)
- ✅ UI: ConnectScreen + BigConnectButton + VertexHero (animated). SettingsScreen with About / IdentityKey
- ✅ Permission flow: `VpnService.prepare` + POST_NOTIFICATIONS, rotation-stable `vpnPermissionDenied`
- ✅ Build: Debug APK via `make build-android`

### Phase 2 — `android-v1.1.0` — ✅ DONE 2026-04-30

- ✅ SRV discovery via DoH (OkHttp → Cloudflare → Google fallback) + 6 h cache TTL + force refresh
- ✅ Multi-broker failover with sticky reconnect, unique per-session `clientId` (epoch36 suffix to avoid mosquitto ghost-session conflicts on rapid restarts)
- ✅ Multi-exit + DiscoveryHeartbeat subscription + auto-select scoring (3 s window, broker-RTT primary, load tiebreak)
- ✅ BrokerListScreen / ExitListScreen UI
- ✅ ConnectivityManager.NetworkCallback for path-monitor (force-reconnect MQTT on Wi-Fi↔cell handoff)
- ⏸ Move VpnService to `:vpn` process + Messenger — deferred (single-process StateFlow is sufficient for now)

### Phase 3 — `android-v1.2.0` — ✅ DONE 2026-04-30

- ✅ Split routing RU exclude via `Builder.excludeRoute(IpPrefix)` on API 33+
- ✅ RUNets loader + bundled `assets/ru-aggregated.zone`
- ✅ VertexHero animated Compose Canvas (breath/pulse, state-keyed)
- ✅ StatusPill state-keyed transitions
- ✅ Tunnel MTU **1300** (vs 1500 on iOS/macOS) — Android Linux netstack doesn't auto-MSS-clamp like XNU; PMTU black-holes under RU ISP DPI without this
- ✅ `Builder.allowBypass()` + `addRoute("::", 0)` + `setMetered(false)` — required for Wi-Fi to validate, see `feedback_android_vpn_builder_essentials`
- ⚠️ **Trade-off — RU exclude routes capped at 1500** (down from full 8585): each `IpPrefix` marshals to ~140 B inside `LinkProperties`, so the full set blows past Android's Binder transaction limit (~1 MB AOSP / lower on Sony Xperia), `INetworkMonitor.notifyNetworkConnected` throws `TransactionTooLargeException`, the VPN never gets `VALIDATED` and the user sees the no-internet cross. Sorted by prefix length ascending so the largest aggregated blocks survive — coverage loss is <1 % of RU IP space. Removing this cap is the explicit goal of Phase 3.5.
- ✅ ipdeny.com refresh — `RuNetsRepository` downloads `https://www.ipdeny.com/ipblocks/data/aggregated/ru-aggregated.zone`, atomically swaps `filesDir/ru-aggregated.zone` via POSIX `Os.rename`, refuses bodies < 50 KB or < 1000 valid CIDR lines. `RUNetsLoader.load()` prefers the refreshed copy over the bundled asset. UI: Settings → Routing exposes "X CIDRs · bundled with app | updated 5m ago" + "Refresh from ipdeny.com" tap row.

### Phase 3.5 — userspace split tunnel (deferred 2026-04-30)

Move the RU bypass out of the kernel (`Builder.excludeRoute` capped at 1500) into a userspace TCP/UDP terminator, sing-box / Hiddify style.

Realistic implementation (the only one that works on stock Android — `IPPROTO_RAW` requires `CAP_NET_RAW` which apps don't have): bundle gvisor's netstack as an AAR via `gomobile bind`, terminate RU-bound TCP/UDP in-process, dial out via `service.protect()`-ed Sockets, synthesize response packets back into the TUN. xjasonlyu/tun2socks is the closest off-the-shelf reusable code.

**Trigger to ship this:** (a) user reports of specific RU sites going through the VPN, (b) need to add a second large geo (CN ~13k CIDRs, SA ~5k), or (c) Sony's tighter Binder cap blocking other vendors. Until any of those hits, the current 1500-route cap loses <1% of RU IP space and is the right trade-off — see `project_android_phase35_userspace_bypass` in memory for the full roadmap.

### Phase 4 — `android-v1.3.0`

- ✅ DiagnosticsScreen with memory probe / battery / file log tail / export zip — `FileLogger` Timber tree (planted in every build, not just DEBUG) writes to `filesDir/logs/vtx-{current,1..6}.log`, 1 MiB per file × 7 = ~7 MiB cap, single-thread bounded executor (`ArrayBlockingQueue(4096)` + `DiscardOldestPolicy`) so Timber on the packet hot path can't OOM the queue. `MemoryProbe` and `BatteryProbe` in `:vpn/diag/`. `DiagnosticsRepository` zips logs + a sanitized `summary.txt` (versionName, device codename only — no PRODUCT carrier-region tag, identity pubkey, settings sans password) into `cacheDir/diagnostics/` and surfaces via FileProvider (`${applicationId}.fileprovider`). UI: live Memory / Battery / Recent log sections refreshed every 3 s while screen visible, "Export diagnostics zip" button → ACTION_SEND chooser; `ActivityNotFoundException` paths surface a Failed banner instead of swallowing.
- ✅ Quick Settings Tile — `VertexQuickSettingsTile` (`@AndroidEntryPoint TileService`) mirrors `TunnelStateBus.status` into `STATE_INACTIVE`/`STATE_ACTIVE` with a "Off"/"Connecting…"/"Connected" subtitle. Click toggles connect/disconnect; missing VPN permission, missing MQTT password, or empty broker list all punt to MainActivity (the system VPN dialog needs an Activity context, and a doomed connect from the shade is worse than asking the user to open the app once). Connect dispatch runs in an application-scoped CoroutineScope (Hilt `@ApplicationScope`) so the shade collapsing on tap doesn't cancel the service-start intent. Per-click `clickInFlight: Job?` latch debounces rapid double-taps. `unlockAndRun` is used for the lock-screen fallback so the system VPN dialog actually appears instead of a silent no-op.
- ⏸ Crash reporting — explicitly skipped (Sentry deferred; Firebase rejected for Google Services dependency)
- ✅ Release APK signing pipeline → Gitea release — `~/.android/vertex.keystore` (PKCS12, RSA 4096, alias `vertex`, DName `CN=Vertex, O=Vertex, C=RU`). Passwords and env-var setup live in CREDENTIALS.md. ProGuard rules silence transitive Tink/errorprone warnings (added 2026-04-30 when first release surfaced them). `make release-android VERSION=X.Y.Z` runs the signed assemble + uploads the APK as `Vertex-android-vX.Y.Z.apk` via `scripts/gitea-release.sh`.
- Polish UI (additional glow effects, motion tokens)

(Phase 5 — Play Store / F-Droid: explicitly skipped per user decision)

## Validated platforms / devices

Reference test device: **Sony Xperia 5 V** (XQ-BE72, Android 13). Every fix in this app's history was validated against this exact hardware — Sony Xperia introduced two non-AOSP behaviours which the Phase 3 work specifically had to navigate:

1. `com.sonymobile.smartnetworkengine` crashes `system_server` with `DeadSystemException` if the VPN ever calls `service.setUnderlyingNetworks(null)` — cascade-kills `com.android.settings` and the launcher. **Don't call `setUnderlyingNetworks`.** See `feedback_android_no_setUnderlyingNetworks`.
2. Sony's `INetworkMonitor` proxy enforces a stricter Binder transaction cap than AOSP's nominal 1 MB. The 1500-route cap above is empirically tuned for this device — Pixel-class hardware likely tolerates more, but the conservative cap costs <1 % coverage and avoids per-vendor surprises.

For test-driving on USB: `adb` over USB-C is the stable channel — the Wi-Fi-debug ADB port hops every time the tunnel cycles up/down because the listener gets re-created on the new interface.

## Critical reference files

Without these the `:core` port cannot start:

```
clients/shared/VertexCore/Sources/VertexCore/Crypto/SessionCrypto.swift
clients/shared/VertexCore/Sources/VertexCore/Crypto/IdentityKey.swift
clients/shared/VertexCore/Sources/VertexCore/Transport/MQTTPacketCodec.swift
clients/shared/VertexCore/Sources/VertexCore/Transport/MQTTConnection.swift
clients/shared/VertexCore/Sources/VertexCore/Transport/MQTTTransport.swift
clients/shared/VertexCore/Sources/VertexCore/Protocol/JoinMessage.swift
clients/shared/VertexCore/Sources/VertexCore/Protocol/AssignMessage.swift
clients/shared/VertexCore/Sources/VertexCore/Protocol/DiscoveryHeartbeat.swift
clients/ios/Vertex/Tunnel/PacketTunnelProvider.swift
clients/ios/Vertex/App/Services/SRVDiscovery.swift
clients/ios/Vertex/App/Services/TunnelManager.swift
clients/ios/Vertex/App/Theme/Color+Vertex.swift
clients/ios/Vertex/App/Theme/Font+Vertex.swift
clients/ios/Vertex/App/Views/Components/VertexHero.swift
pkg/crypto/                          # Go reference for cross-language test vectors
pkg/transport/mqtt/                  # Go MQTT 5.0 reference
pkg/identity/                        # Go identity reference
```

## Verification checklist

### Phase 1

- [x] `./gradlew :core:test` — all unit tests pass; crypto vectors match Go/Swift fixtures
- [x] `./gradlew :app:assembleDebug` — builds clean
- [x] APK installs on physical Android ≥ 8.0 (Sony Xperia 5 V, Android 13)
- [x] Permission flow: VpnService.prepare → user accept → POST_NOTIFICATIONS → connect button enabled
- [x] Connect to production broker → STO exit → assigned IP `10.9.2.5` → big-page download (Google home 80 KB) at ~100 KB/s
- [x] Disconnect cleans up — foreground notification dismissed
- [x] Identity TOFU: first connect registers key on exit
- [ ] Reconnect after wifi↔cellular swap (airplane mode toggle) — covered by `ConnectivityManager.NetworkCallback` registration; not formally tested
- [ ] Memory baseline measurements
- [ ] Docker integration test against stack-up + `adb reverse`

### Phase 2-4

- [x] SRV discovery against `vertices.ru` finds brokers/exits via DoH
- [x] Broker failover: sticky reconnect with unique per-session clientId — no ghost-session churn
- [x] Exit auto-select: 3 s window, broker-RTT scoring, load tiebreak
- [x] Phase 3: split routing — non-RU traffic via STO exit (`ifconfig.me` returns `13.51.6.23`), RU CIDRs excluded via `Builder.excludeRoute` (cap 1500)
- [x] Phase 3: Wi-Fi gets `IS_VALIDATED` capability within ~25 s of connect — no permanent no-internet cross
- [x] Phase 3: Google Play login final step works under always-on lockdown (`Block connections without VPN` enabled in system VPN settings)
- [x] Phase 3: captive HTTP/HTTPS probes return `204` through tunnel
- [x] Phase 3: `mtalk.google.com:5228` (Google FCM) TLS handshake completes through tunnel
- [ ] Phase 4: Quick Settings Tile visible in shade, click toggles VPN
- [ ] Phase 4: Sentry self-hosted crash report ingestion

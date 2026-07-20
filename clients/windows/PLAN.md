# Vertex Windows Client — План имплементации

## Контекст

В проекте Vertex VPN есть три нативных клиента (iOS/macOS на Swift, Android на Kotlin), которые делят wire-protocol с Go server/exit. Каждый клиент — нативная имплементация одного и того же протокола (MQTT 5.0 + X25519 ECDH + ChaCha20-Poly1305 + HKDF + identity TOFU). Windows-клиента нет — десктоп-пользователи Windows не могут подключиться без Go CLI.

**Цель**: четвёртый нативный клиент с полным паритетом фич iOS/macOS/Android и UI 1-в-1 с macOS (тот же layout, цвета, иконки, анимации). Long-term consistency: тот же подход что у Android (порт VertexCore на родной язык платформы), Windows-специфичные системные API в Service.

**Что прерывалось**: Phase 5 (multi-broker, multi-exit, SRV discovery, identity TOFU) уже отгружен в production на iOS/macOS/Android — Windows должен вступить в строй на этом уровне функциональности.

---

## Архитектурные решения

### Технологический стек

| Компонент | Выбор | Обоснование |
|-----------|-------|-------------|
| **Язык** | C# / .NET 8 (LTS) | Лучшая интеграция с Windows API, AOT support, x64+ARM64 |
| **UI Framework** | WinUI 3 + Windows App SDK 1.6+ | Современный Microsoft stack, нативный Win11 look (Mica/Acrylic), лучший канвас |
| **Min Windows** | Windows 10 1903 (build 18362) | `System.Security.Cryptography.ChaCha20Poly1305` требует 1903+ (на 1809 throws PNSE) |
| **VPN driver** | WinTUN 0.14.1 (WireGuard) | User-mode DLL, MIT, Microsoft-attested kernel driver внутри, не требует EV signing |
| **Service** | Windows Service (LocalSystem) | Аналог NEPacketTunnelProvider, владеет TUN handle (process-local) |
| **IPC** | Named Pipe (`\\.\pipe\vertex-vpn`) | JSON line-delimited; SDDL ограничивает доступ Users + LocalSystem |
| **Crypto X25519** | NSec.Cryptography (libsodium) | .NET 8 не имеет X25519 ECDH встроенного |
| **Crypto AEAD/HKDF** | .NET 8 built-in | `ChaCha20Poly1305`, `HKDF.DeriveKey`, `HMACSHA256` |
| **MQTT 5.0** | **Своя реализация** (порт Swift `MQTTPacketCodec`/`MQTTConnection`/`MQTTTransport`) | MQTTnet несовместим с WSS subprotocol "mqtt" framing; шарим test fixtures с iOS/macOS/Android |
| **Sockets** | `SslStream` over `Socket` (mqtts:8883) + `ClientWebSocket` (wss:443 c subprotocol "mqtt") | Прямой паритет с NWConnection / SSLSocket+OkHttp |
| **DNS DoH** | `HttpClient` → Cloudflare/Google `application/dns-json` | Тот же подход что у iOS/Android |
| **Routing API** | `CreateIpForwardEntry2`/`DeleteIpForwardEntry2` (iphlpapi) через CsWin32 source-gen | Не shell out в `route.exe`/`netsh.exe` (медленно, 1500 RU CIDRs) |
| **Canvas (VertexHero)** | Win2D (`Microsoft.Graphics.Win2D`) + `CanvasAnimatedControl` | Immediate-mode 2D + 60 FPS + sin/log на каждый кадр — как Swift Canvas+TimelineView |
| **Secrets storage** | DPAPI LocalMachine + ACL'd files в `%ProgramData%\Vertex\` | Service-owned secrets; UI пишет через named pipe |
| **Installer** | WiX 4 MSI | Service registration + bundled wintun.dll + ACLs через `<PermissionEx>` |
| **Code signing** | Authenticode EV cert | SmartScreen reputation с первого запуска |

### Что отвергнуто и почему

- **Avalonia / WPF**: WPF не в LTS-плане, Avalonia требует custom Mica/Acrylic для нативного вида.
- **MAUI**: WinUI 3 под капотом всё равно, плюс мобильный surface.
- **MSIX-only**: Service-инсталляция в MSIX ограничена, и проект не идёт в Store (паритет с Android sideload-only).
- **Go vtx-client как backend (subprocess)**: ломает паттерн "shared protocol, native UI" других платформ; subprocess crash через JSON event hard to debug; identity DPAPI bridging усложняется. Принято: native C# порт.
- **MQTTnet**: тот же trap что обошли с HiveMQ на Android — собственная state machine не совпадает с `keepAlive=20` semantics и WSS framing.
- **OpenVPN tap-windows6**: kernel driver signing на нас, Layer-2, неправильный fit.
- **wireguard-go TUN**: тащит Go runtime.

### Системные ограничения Windows, которые надо адресовать с Phase 1

1. **DNS leak via Smart Multi-Homed Resolution** — Windows запрашивает DNS на ВСЕХ интерфейсах параллельно. Мер: NRPT (`HKLM\SYSTEM\CurrentControlSet\Services\Dnscache\Parameters\DnsPolicyConfig`) + `SetInterfaceDnsSettings` на TUN + disable LLMNR/mDNS на время сессии. Reference: WireGuard-Windows `tunnel/dnsLeakBlocker.go`.
2. **Broker bypass без `protect()`** — на Windows нет per-socket bypass. Решение: /32 host routes для broker IPs через physical interface (как macOS sub-range trick из `pkg/routing`).
3. **WinTUN handle process-local** — нельзя пробросить через pipe. Service владеет, UI получает только события.
4. **WinTUN read loop требует dedicated Thread** (не Task) — `WintunReceivePacket` блокирует indefinitely, ThreadPool starvation реальна. Та же лекция что Android dedicated threads.
5. **Sleep/resume через `RegisterPowerSettingNotification`** + `NotifyIpInterfaceChange` для смены интерфейса (Ethernet↔Wi-Fi).
6. **IPv6 на TUN** — Windows биндит link-local автоматически, отключить (`Set-NetAdapterBinding ms_tcpip6`) для паритета с iOS/macOS.

---

## Структура проекта

```
clients/windows/
├── Vertex.sln
├── Directory.Build.props                 # nullable=enable, nowarn, version, signing
├── src/
│   ├── Vertex.Core/                      # Library: MQTT/crypto/discovery (.NET 8)
│   │   ├── Crypto/
│   │   │   ├── SessionCrypto.cs          # ECDH + HKDF("broker-tunnel-v1") + ChaChaPoly
│   │   │   └── IdentityKey.cs            # X25519 keypair + HMAC proof "vtx-identity-v1"
│   │   ├── Transport/
│   │   │   ├── MqttPacketCodec.cs        # Packet encode/decode (~250 LOC)
│   │   │   ├── MqttConnection.cs         # SslStream/WebSocket + receive loop + ping/pong (~400 LOC)
│   │   │   └── MqttTransport.cs          # Failover + sticky reconnect + state stream (~250 LOC)
│   │   ├── Discovery/
│   │   │   ├── DiscoveryTracker.cs       # Heartbeat ingest + scoring (RTT × loadFactor)
│   │   │   ├── BrokerProbe.cs            # TCP RTT measurement + reorderByRTT
│   │   │   └── SrvResolver.cs            # DoH (Cloudflare→Google→backup) + cache
│   │   ├── Protocol/
│   │   │   ├── JoinMessage.cs            # name, dh, id, idSig
│   │   │   ├── AssignMessage.cs          # ip, mask, gw, dh
│   │   │   ├── DiscoveryHeartbeat.cs     # id, country, clients, brokerRTTms, dhPubkey
│   │   │   └── Topics.cs                 # vpn/{exit}/{name}/in|out|control + discovery/exits/+
│   │   ├── Routing/
│   │   │   ├── CidrParser.cs
│   │   │   └── RuNetsLoader.cs           # bundled + ipdeny refresh + cap top-1500
│   │   ├── Config/
│   │   │   ├── BrokerUrl.cs              # mqtt|mqtts|ws|wss schemes, port defaults
│   │   │   └── TunnelConfig.cs
│   │   └── Util/NodeLabels.cs            # V₀ · YC, E₀ · AWS labels
│   │
│   ├── Vertex.Core.Tests/                # xUnit
│   │   ├── CryptoVectorsTests.cs         # против shared test/fixtures/wire-format/*.json
│   │   ├── MqttCodecTests.cs             # encode/decode roundtrip
│   │   └── DiscoveryScoringTests.cs      # ports of Swift Tracker tests
│   │
│   ├── Vertex.Service/                   # Windows Service (LocalSystem)
│   │   ├── Program.cs                    # Worker host bootstrap + EventLog
│   │   ├── TunnelEngine.cs               # Orchestrator — аналог PacketTunnelProvider
│   │   ├── Tun/
│   │   │   ├── WintunInterop.cs          # CsWin32 P/Invoke
│   │   │   ├── WintunSession.cs          # IDisposable wrapper
│   │   │   ├── PacketPipeline.cs         # 2 dedicated threads: TUN→MQTT, MQTT→TUN
│   │   │   ├── RouteManager.cs           # CreateIpForwardEntry2 batched
│   │   │   ├── DnsLeakGuard.cs           # NRPT + SetInterfaceDnsSettings + LLMNR off
│   │   │   └── SplitRouter.cs            # Top-1500 RU CIDRs via physical iface
│   │   ├── Power/
│   │   │   ├── PowerNotifier.cs          # RegisterPowerSettingNotification
│   │   │   └── NetworkChangeNotifier.cs  # NotifyIpInterfaceChange + NotifyRouteChange2
│   │   ├── Storage/
│   │   │   ├── IdentityStore.cs          # DPAPI LocalMachine → identity.bin
│   │   │   ├── PasswordStore.cs          # DPAPI LocalMachine → password.bin
│   │   │   └── StateStore.cs             # JSON: lastGoodExit, settings, srvCache
│   │   ├── Ipc/
│   │   │   ├── PipeServer.cs             # Named pipe accept loop, async
│   │   │   └── IpcHandler.cs             # Routing AppMessage → engine
│   │   └── Logging/
│   │       └── FileLogger.cs             # Rolling 7×1MiB → %ProgramData%\Vertex\logs
│   │
│   ├── Vertex.App/                       # WinUI 3 desktop UI (packaged MSIX inside MSI)
│   │   ├── App.xaml(.cs)
│   │   ├── MainWindow.xaml(.cs)          # Frame + NavigationView
│   │   ├── Theme/
│   │   │   ├── Colors.xaml               # bgCanvas #0F0F26, accentPrimary #7DB3FF, etc.
│   │   │   ├── Typography.xaml           # heroStatus 28pt rounded semibold, ...
│   │   │   ├── VxSpace.cs                # 4pt grid: s0..s12
│   │   │   ├── VxRadius.cs               # sm 8, md 12, lg 16, xl 22
│   │   │   └── VxMotion.cs               # heroBreathPeriod 2.4s, heroPulsePeriod 0.9s, ...
│   │   ├── Views/
│   │   │   ├── ConnectScreen.xaml(.cs)   # Главный: wordmark + Hero + ServerCard + Connect + Stats + Error
│   │   │   ├── SettingsPage.xaml(.cs)    # Pivot: Identity / Discovery / Routing / About
│   │   │   ├── BrokerListPage.xaml(.cs)
│   │   │   ├── ExitListPage.xaml(.cs)
│   │   │   ├── StatsDialog.xaml(.cs)     # Modal: full stats
│   │   │   └── DiagnosticsPage.xaml(.cs) # Memory probe + log tail + Export ZIP
│   │   ├── Controls/
│   │   │   ├── VertexHero.cs             # Win2D CanvasAnimatedControl, port VertexHero.swift
│   │   │   ├── StatusPill.xaml(.cs)
│   │   │   ├── SpeedPill.xaml(.cs)
│   │   │   ├── ServerCard.xaml(.cs)
│   │   │   ├── BigConnectButton.xaml(.cs)
│   │   │   ├── StatRow.xaml(.cs)
│   │   │   ├── VxSection.xaml(.cs)
│   │   │   └── Glyphs/
│   │   │       ├── VxAsteriskGlyph.cs    # Path geometry — V-shape с 3 точками
│   │   │       ├── VxEdgeGlyph.cs        # Ascending line + dot
│   │   │       └── VxSelectionGlyph.cs   # Checkmark
│   │   ├── ViewModels/
│   │   │   └── TunnelViewModel.cs        # ObservableObject, IPC events → properties
│   │   ├── Services/
│   │   │   ├── IpcClient.cs              # Pipe client + reconnect
│   │   │   ├── Notifications.cs          # ToastNotification (status changes)
│   │   │   └── Haptics.cs                # System sound (no haptics on desktop)
│   │   └── Resources/
│   │       ├── ru-aggregated.zone        # Bundled fallback CIDRs
│   │       └── Icons/                    # SVG glyphs
│   │
│   └── Vertex.Shared/                    # Shared between Service & App
│       ├── Ipc/
│       │   ├── AppMessage.cs             # requestStatus, requestStats, connect, disconnect, setPassword, ...
│       │   └── ExtensionResponse.cs      # status, stats, error
│       ├── ConnectionStatus.cs           # state, assignedIP, currentBroker, currentExit, connectedSince
│       ├── TunnelStats.cs                # bytesUp/Down, packetsUp/Down
│       └── TunnelErrorReport.cs          # kind (auth, identityRejected, configuration, ...)
│
├── packaging/
│   ├── Vertex.Setup.wixproj              # WiX 4
│   ├── Product.wxs                       # Service install + ACLs + ARP entry
│   ├── Files/
│   │   ├── wintun-amd64.dll              # 0.14.1, MIT
│   │   └── wintun-arm64.dll
│   └── localization/ru-RU.wxl
│
├── tools/
│   └── make.ps1                          # build/test/sign/publish helpers
│
└── README.md
```

---

## Фазы реализации

### Phase 0 — Scaffold (1 неделя)

- Solution layout + CI на `windows-latest`
- WiX 4 MSI скелет — устанавливает empty Service + ARP запись + uninstaller
- CsWin32 source-generator для IpHelper / SCM / WinTUN P/Invoke
- IPC контракт (JSON line-delimited): event names паритет с `pkg/events` (`connecting`, `connected`, `ip_assigned`, `tun_created`, `routes_configured`, `ready`, `error`, `disconnected`)
- Authenticode signing pipeline (test cert ok пока, EV в Phase 5)

### Phase 1 — MVP full tunnel (3-4 недели)

**Vertex.Core port (приоритет — паритет с эталоном):**
- Crypto: NSec X25519 + .NET ChaCha20-Poly1305 + `HKDF.DeriveKey(SHA256, sharedSecret, info: "broker-tunnel-v1", salt: clientPub||exitPub)`. Обязательный regression test против shared fixtures (test vectors производят те же ciphertexts что Swift и Kotlin — это ловит drift который Docker не ловит).
- IdentityKey: persistent X25519 keypair, HMAC-SHA256(ECDH, "vtx-identity-v1"+name).
- MqttPacketCodec: CONNECT (cleanStart=true, sessionExpiry=0), PUBLISH (QoS 0, retain=false, messageExpiry=10), SUBSCRIBE, PINGREQ/PINGRESP, DISCONNECT. Variable-length integers, MQTT 5.0 properties.
- MqttConnection: `SslStream` для mqtts (с manual `RemoteCertificateValidationCallback`, SNI explicit), `ClientWebSocket` с `AddSubProtocol("mqtt")` для wss. Keepalive 20s (`max(keepAlive-5, 5)` ping interval, 5s pingResponseTimeout, link-dead detection).
- MqttTransport: ordered failover, sticky reconnect (winner на index 0), exp backoff `[0, 0.5, 1, 2, 5, 5, ...]`, `consecutiveConnectFailures>=3 → onFatalError`, auth failure (`CONNACK reason=0x86/0x87 → onAuthFailure`, не retry).
- Topics + JoinMessage + AssignMessage + DiscoveryHeartbeat (System.Text.Json с `[JsonPropertyName]` обязательно — для wire-format паритета).
- DiscoveryTracker: `score = brokerRTT * (1 + clients/capacity * 2.0)`, staleAge 90s, `shouldSwitch` flap-guard 1.5×.

**Vertex.Service:**
- WintunInterop через CsWin32: `WintunCreateAdapter`, `WintunStartSession`, `WintunReceivePacket`, `WintunSendPacket`.
- TunnelEngine lifecycle: load identity → ephemeral DH → MQTT connect → discovery subscribe → join handshake (с identity proof) → assign → SessionCrypto.fromDH → TUN open → routes → packet pipeline.
- PacketPipeline: 2 dedicated `Thread` (IsBackground=true): TUN→encrypt→MQTT publish, MQTT subscribe→decrypt→TUN write.
- RouteManager: full-tunnel routes (`0.0.0.0/0` + /32 broker bypass через physical iface).
- DnsLeakGuard: NRPT `.` → TUN DNS, disable LLMNR/mDNS, restore on cleanup. **Реализовать с Phase 1, не откладывать** — DNS leak это privacy bug.
- IdentityStore + PasswordStore (DPAPI LocalMachine) + StateStore (JSON).
- IpcServer: async accept loop, JSON line protocol.

**Vertex.App (минимум):**
- WinUI 3 shell, ConnectScreen с static V-asterisk SVG (без анимации), `BigConnectButton`, базовая `StatusPill`.
- IpcClient + reconnect.
- TunnelViewModel — ObservableProperty биндинги к XAML.

### Phase 2 — SRV + multi-broker (2 недели)

- SrvResolver: DoH (Cloudflare → Google → cached → backup domain). 6h TTL cache в `%ProgramData%\Vertex\srv-cache.json`.
- Multi-broker ordered list (priority+weight sorted) + sticky reconnect (паритет с iOS/macOS).
- Auto-select exit через DiscoveryTracker.bestExit() при `selectedExit == "auto"`.
- Broker probe RTT при `selectedBroker == "auto"`.
- BrokerListPage + ExitListPage — auto + manual выборы.

### Phase 3 — Split routing + animated VertexHero (3 недели)

- RuNetsLoader: bundled `ru-aggregated.zone` + ipdeny refresh + atomic replace.
- SplitRouter: top-1500 prefix-length-ascending CIDRs as more-specific routes via physical iface (паритет с Android Phase 3). Default 0/0 через TUN.
- DnsLeakGuard работает корректно при split tunnel.
- **Win2D VertexHero** — direct port `VertexHero.swift`:
  - 220×220 contentSize + 24 haloPadding
  - 30 FPS `CanvasAnimatedControl`
  - Geometry: endpointA/B/V, lineA/B start/end (в px при scale=1)
  - States: `.connected` (breath halo + rate-driven endpoint glows + shimmer), `.connecting` (3-node A→B→V sequence + sweep), `.reasserting`, `.disconnecting`, `.disconnected`, `.invalid` (error shake)
  - RateMap: floor 50K, ceil 50M, exponent 0.85 (continuous) + 3-step ladder (Reduce Motion via `UISettings.AnimationsEnabled`)
- SettingsPage с 4 разделами (Identity / Discovery / Routing / About) — реальное содержимое
- ServerCard, SpeedPill, StatRow, StatsDialog
- Cleanup полная типографики и spacings

### Phase 4 — Diagnostics + UX polish (2 недели)

- DiagnosticsPage: WorkingSet64 + Process CPU + log tail (last 200 lines) + Export ZIP
- AboutPage с version + build
- Identity reset (delete identity.bin → next connect re-TOFU)
- Network change detection: `NotifyIpInterfaceChange` + `NotifyRouteChange2` для exhibit
- Power events: `RegisterPowerSettingNotification` для sleep/resume
- Toast notifications (статус подключения)
- Single-instance UI через `AppInstance.RedirectActivationToAsync`

### Phase 5 — Installer + signing + release (1-2 недели)

- WiX 4 MSI: Service registration с `<ServiceInstall>` + `<ServiceConfig>`, ACL через `<PermissionEx>` (LocalSystem + Admins full, Users read-only на pipe), bundled `wintun-{amd64,arm64}.dll`
- Authenticode signing (signtool + EV cert) для Service.exe, App.exe, MSI
- Раздельный MSI на amd64 / arm64
- Auto-update через Velopack (MIT, простой) или WinSparkle
- Версия в `Directory.Build.props` (SemVer на каждом билде — bump перед каждым `make build-windows-release`)
- Release upload в Gitea

---

## Точные значения для UI (паритет macOS)

### Палитра (тёмная только, как macOS)

| Token | Hex |
|-------|-----|
| `bgCanvas` | `#0F0F26` |
| `bgSurface` | `#141C38` |
| `bgSurfaceElev` | `#1A1F40` |
| `bgSurfaceMuted` | `#0D1226` |
| `borderSubtle` | `#26263F` |
| `accentPrimary` | `#7DB3FF` |
| `accentPrimaryMuted` | `#334080` |
| `glowPrimary` | `#7DB3FF` |
| `textPrimary` | `#FFFFFF` |
| `textSecondary` | `#B3B3BF` |
| `textTertiary` | `#808088` |
| `stateConnected` | `#7DB3FF` |
| `stateTransitioning` | `#FAD27A` |
| `stateError` | `#FF6E78` |
| `stateDormant` | `#666666` |

(Точные значения — взять из `/Users/mrkutin/Projects/vertex/clients/macos/Vertex/App/Assets.xcassets/*.colorset/Contents.json` при имплементации.)

### Spacing (4pt grid → DIP в WinUI)

s0=0, s1=4, s2=8, s3=12, s4=16, s5=20, s6=24, s7=28, s8=32, s10=40, s12=48

### Radius

sm=8, md=12, lg=16, xl=22, capsule=999, heroOuter=96

### Motion

heroReplace=0.28, heroBreathPeriod=2.4, heroPulsePeriod=0.9, heroReassertPeriod=1.4, heroErrorShake=0.36, heroDisconnectFade=0.6, buttonPress=0.12, buttonGlowPeriod=1.8, sheetPresent=0.36, navPush=0.32, statsAppear=0.28, pillTextChange=0.18, edgeFlow=5.0

### Окно

minWidth: 520 DIP, content maxWidth: 480 DIP, не resizable по высоте (как macOS `.windowResizability(.contentSize)`).

### ConnectScreen layout

VStack(spacing=s7=28): wordmark → hero (220×220+24 padding) → serverCard → BigConnectButton(56pt height) → statsCard (опционально) → errorBanner (опционально). Padding: H s5=20, top s2=8, bottom s8=32.

---

## Критичные файлы для имплементации

**Эталоны (Read для портирования):**
- `/Users/mrkutin/Projects/vertex/clients/shared/VertexCore/Sources/VertexCore/Transport/MQTTPacketCodec.swift`
- `/Users/mrkutin/Projects/vertex/clients/shared/VertexCore/Sources/VertexCore/Transport/MQTTConnection.swift`
- `/Users/mrkutin/Projects/vertex/clients/shared/VertexCore/Sources/VertexCore/Transport/MQTTTransport.swift`
- `/Users/mrkutin/Projects/vertex/clients/shared/VertexCore/Sources/VertexCore/Crypto/SessionCrypto.swift`
- `/Users/mrkutin/Projects/vertex/clients/shared/VertexCore/Sources/VertexCore/Crypto/IdentityKey.swift`
- `/Users/mrkutin/Projects/vertex/clients/shared/VertexCore/Sources/VertexCore/Discovery/Tracker.swift`
- `/Users/mrkutin/Projects/vertex/clients/shared/VertexCore/Sources/VertexCore/Discovery/BrokerProbe.swift`
- `/Users/mrkutin/Projects/vertex/clients/shared/VertexCore/Sources/VertexCore/Protocol/{JoinMessage,AssignMessage,DiscoveryHeartbeat,Topics}.swift`
- `/Users/mrkutin/Projects/vertex/clients/macos/Vertex/Tunnel/PacketTunnelProvider.swift` — образец lifecycle для TunnelEngine
- `/Users/mrkutin/Projects/vertex/clients/macos/Vertex/App/Views/ConnectScreen.swift` — XAML reference layout
- `/Users/mrkutin/Projects/vertex/clients/macos/Vertex/App/Views/Components/VertexHero.swift` — Win2D port (770 LOC)
- `/Users/mrkutin/Projects/vertex/clients/macos/Vertex/App/Theme/{Color+Vertex,Font+Vertex,Motion,Radius,Spacing}.swift` — точные значения
- `/Users/mrkutin/Projects/vertex/clients/android/Vertex/core/src/main/kotlin/ru/vertices/android/core/` — Kotlin port как референс адаптации к "не-Swift" языку
- `/Users/mrkutin/Projects/vertex/clients/android/PLAN.md` — структура планирования (этот план её mirror'ит)

**Внешние референсы:**
- WireGuard-Windows `tunnel/dnsLeakBlocker.go` (DNS leak)
- WireGuard-Windows `tunnel/winipcfg/` (IpHelper P/Invoke)
- WinTUN 0.14.1 SDK + headers

---

## Риски и митигации

| Риск | Вероятность | Удар | Митигация |
|------|-------------|------|-----------|
| Wire-format drift Swift↔C# | Средняя | Несовместимость с production exit | Shared `test/fixtures/wire-format/*.json`, regression test с Phase 1 |
| DNS leak под split-tunnel | Высокая | Privacy bug | NRPT + multi-homed mitigation **в Phase 1**, не откладывать |
| WinTUN handle leak при reconnect | Средняя | Stale adapter после crash | `IDisposable` симметрично, idempotent `RouteManager.Cleanup()`, sentry-style ServiceController.Restart on bad state |
| WinUI 3 quirks на 1809 | Высокая | UI regressions | Bumped min до 1903 (build 18362), align с ChaCha20Poly1305 native availability |
| SmartScreen reputation cold-start | Высокая | Юзер боится открывать MSI | EV cert с Phase 5 (или принять ~100 install learning curve) |
| Sleep/resume reconnect loops → battery drain | Средняя | UX | `RegisterPowerSettingNotification` + 3-strike escalation как Android |
| LocalSystem service → AV false-positives | Низкая | Defender quarantine | `SERVICE_SID_TYPE_RESTRICTED` + `AdjustTokenPrivileges` дроп лишних privileges |

---

## Verification

### Unit + Integration

- `Vertex.Core.Tests` через `dotnet test`:
  - Crypto round-trip против shared `test/fixtures/wire-format/crypto-vectors-v1.json`
  - MqttCodec encode/decode roundtrip + сравнение байт-в-байт с reference fixtures
  - DiscoveryTracker scoring (порт Swift тестов из `Tests/VertexCoreTests/`)
  - SrvResolver DoH parsing
- `Vertex.Service.Tests` для RouteManager, DnsLeakGuard (mocked iphlpapi).

### Docker integration test

- Поднять Mosquitto (тот же `test/docker/`) на хосте, запустить Service+UI на Win VM, выполнить speedtest.net через TUN, замерить throughput, проверить отсутствие DNS leak (`dnsleaktest.com`).
- Тестировать переключение брокеров (kill один Mosquitto → sticky reconnect должен подняться на втором за <1s).
- Тестировать exit auto-switch (пометить exit как stale в DiscoveryTracker).

### Production smoke

- Build → MSI signed → install на чистой Win10 1903 / Win11 ARM64 VM
- Connect к production AWS/STO через YC/Sber брокеры
- Verify: assigned IP внутренний AWS/STO, ping <100ms, DNS через 1.1.1.1 (через TUN), speedtest >50 Mbps, RU traffic в bypass (ipinfo.io показывает RU IP), VK/Yandex direct
- Identity TOFU: первое подключение регистрирует ключ на exit (`/var/lib/vtx-devices.json`), второе подтверждает same key, replace identity.bin → reject (require-identity=true)
- Sleep/resume: laptop closed 30s, открыть → reconnect <10s

### Acceptance criteria для production-ready

1. Все 6 главных экранов соответствуют macOS layout (verify visually screenshot-by-screenshot)
2. VertexHero на 30 FPS все 6 состояний работают (connected breath, connecting 3-node sweep, reasserting, disconnecting fade, disconnected dim, invalid shake)
3. Throughput Ethernet ≥ 80 Mbps (паритет с macOS 89/92)
4. DNS leak test чистый (только TUN DNS отвечает)
5. RU split tunnel: yandex.ru и vk.com ходят через physical iface, остальное через TUN
6. Auto exit selection работает (отключить best exit → переключение на next за < 5s)
7. Multi-broker failover (kill YC → sticky reconnect на Sber за < 1s)
8. Identity TOFU enforce'd (повторное подключение с тем же ключом OK, после reset — re-TOFU)
9. Sleep/resume стабильно (10 циклов без deadlock)
10. MSI install/uninstall чисто (Service удаляется, routes восстанавливаются, NRPT cleared)

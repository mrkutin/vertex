# Vertex iOS

Native iOS клиент Vertex. SwiftUI + NEPacketTunnelProvider.

Использует общий Swift Package `clients/shared/VertexCore` (X25519 DH + ChaCha20-Poly1305 + MQTT 5.0 over NWConnection + Topics + IPC), тот же что у macOS-клиента.

## Требования

- macOS с Xcode 16+
- iPhone (физическое устройство, iOS 17.0+) — **Simulator не запускает NEPacketTunnelProvider**
- Apple Developer account (для signing) — Team ID `J698685529` уже прописан в `project.yml`
- `xcodegen` (`brew install xcodegen`)

## Сборка

```bash
make gen-ios          # сгенерировать Vertex.xcodeproj
make build-ios-sim    # compile-only через iOS Simulator (validate Swift 6 concurrency)
make build-ios        # сборка под физическое устройство (Debug)
make archive-ios      # Release archive
```

**Важно:** для production-использования всегда деплоить **Release-сборку**. Debug-конфигурация превышает iOS jetsam memory limit при пиковой нагрузке (speedtest), и iOS убивает extension в середине теста.

## Установка на iPhone

```bash
# 1. Узнать UUID устройства (для devicectl) и USB-id (для xcodebuild)
xcrun devicectl list devices                # → ваш девайс
xcodebuild -showdestinations -project Vertex.xcodeproj -scheme Vertex

# 2. Сборка Release под этот id
xcodebuild -project Vertex.xcodeproj -scheme Vertex \
  -configuration Release \
  -destination 'id=<XCODEBUILD_DEVICE_ID>' \
  -derivedDataPath build/DerivedData-device-release \
  -allowProvisioningUpdates build

# 3. Установить и запустить
xcrun devicectl device install app --device <DEVICECTL_UUID> \
  build/DerivedData-device-release/Build/Products/Release-iphoneos/Vertex.app
xcrun devicectl device process launch --device <DEVICECTL_UUID> ru.vertices
```

После первой установки на iPhone: Settings → General → VPN & Device Management → разрешить developer profile.

## Первый запуск

1. Открой Vertex на iPhone.
2. Шестерёнка → Settings:
   - **Client name** — `iphone` (или твоё имя; должно совпадать с `vtx-client-{name}` на брокерах)
   - **Password** — из CREDENTIALS.md
   - **Discovery domain** — `vertices.ru` (default)
   - Нажми Refresh
3. Вернись назад → тапни Server card → выбери broker и exit.
4. Нажми **Connect** — iOS попросит «Allow VPN Configurations», разреши.
5. После Connected проверь: открой Safari, `https://api.ipify.org` — должен показать IP exit'а.

## Diagnostics

Extension логирует memory + traffic stats каждые 5 секунд через os_log в subsystem `ru.vertices.tunnel`. Чтобы их увидеть:

**Через Console.app:**
1. `Console.app` (`/Applications/Utilities/Console.app`)
2. Devices → выбери iPhone
3. Action → Include Info Messages (Cmd+Option+I)
4. В строке поиска: `process:VertexTunnel`
5. Start streaming

**Через Xcode:**
- Window → Devices and Simulators (Cmd+Shift+2) → iPhone → Open Console.

Формат stats-строки:
```
stats: mem=14.2MB up=12345678 down=98765432 pkt up=1234 down=5678 maxBatch up=128 decErr=0 mqttReady=true
```

- `mem` — phys_footprint extension'а в МБ. iOS jetsam-лимит ~50MB. **Baseline 2026-04-25 (iPhone 15 Pro Max, Release):** idle 3.1MB / peak speedtest 14MB / возврат 3.3MB.
- `decErr` — ошибки расшифровки. Должно быть 0; рост означает рассинхронизацию ключей.
- `mqttReady` — состояние MQTT соединения; false означает reconnect.

## Energy / battery testing (v2.3.0+)

VPN extension работает фоном неделями. Нужно убедиться, что: (1) при засыпании iPhone и сворачивании приложения коннект не рвётся; (2) батарейка не садится без причины.

### Что встроено в приложение

- **MetricKit subscriber** (`App/Services/MetricsCollector.swift`) — host-app подписан на `MXMetricManager`. iOS доставляет `MXMetricPayload` примерно раз в 24 часа (когда устройство на зарядке/Wi-Fi и НЕ подключено к Xcode debugger). Payload'ы сохраняются как JSON в App Group container `group.ru.vertices/MetricKit/`, держим последние 30.
- **Diagnostics view** (Settings → Diagnostics) — список сохранённых payload'ов, детальный pretty-printed JSON, ShareLink для экспорта.
- **os_signpost маркеры** (`subsystem: ru.vertices.tunnel`, `category: pointsOfInterest`) — события wake-up'ов, видны в Instruments → Points of Interest:
  - `keepalive-join` — раз в 60s, control plane re-publish join
  - `mqtt-pingreq` — раз в 25s (keepalive 30 - 5)
  - `stats-log` — раз в 5s, чтение memory footprint
  - `upload-batch` (с n=count) — каждое чтение пакетов из TUN

### Метрики, которые смотрим

В Diagnostics → payload JSON ключевые поля:

- `cpuMetrics.cumulativeCPUTime` — суммарное CPU-время за период
- `networkTransferMetrics.cumulativeCellularUpload/Download` vs `cumulativeWifiUpload/Download` — сколько пошло через cellular vs WiFi
- `cellularConditionMetrics.cellularConditionTime` — гистограмма времени проведённого на разных уровнях сигнала (1-4 bars). На 1 bar radio в high-power режиме чаще = больше расход
- `applicationLaunchMetrics`, `applicationResponsivenessMetrics` — UI hangs (нас не должно беспокоить — extension не имеет UI, host-app минимален)
- `displayMetrics.averagePixelLuminance` — нерелевантно
- В диагностических payload'ах: `MXCPUExceptionDiagnostic` (CPU-bound кусок > N% за M минут), `MXHangDiagnostic`, `MXCrashDiagnostic`

### Пользовательская проверка

- **Settings → Battery → Last 10 Days** на iPhone: `Vertex` (host-app) и `ru.vertices.tunnel` (extension) показаны отдельными строками с процентом расхода. Norm: <2%/день при типичной нагрузке.

### Instruments timeline (при подключённом кабеле)

```
Xcode → Open Developer Tool → Instruments
→ Blank template
→ + → Points of Interest (subsystem: ru.vertices.tunnel)
→ Choose target: VertexTunnel process (нужно сначала запустить тоннель)
→ Record
```

Ищем: группируются ли события на таймлайне или разбросаны. Сгруппированные wake-up'ы (один радио-всплеск, потом долгий сон) сильно дешевле для cellular-радио, чем равномерно размазанные.

### Сценарии для тестирования

1. **Idle cellular 30 min, экран заблокирован** — подключить тоннель, заблокировать iPhone на 30 минут на cellular (без WiFi). Settings → Battery должно показать <1%/час для extension.
2. **Sleep/wake cycle** — 100× lock/unlock с подключённым тоннелем. В Console.app искать reconnect события (`MQTT connected`, `Tunnel ready`) — их быть НЕ должно.
3. **Background time** — свернуть приложение, через 30 минут открыть. Тоннель должен оставаться подключённым (`vpnStatus = connected`), без promptа «Allow VPN».
4. **Low Power Mode** — Settings → Battery → Low Power Mode → ON. Запустить speedtest — трафик должен идти, extension не перезапускается. iOS в LPM может дросселить background-таймеры — наблюдаем.
5. **Network Link Conditioner** — Settings → Developer → Network Link Conditioner → 3G/Edge/Lossy. Проверка устойчивости MQTT keepalive в плохих условиях.

### Собираем данные

- **Локально**: Diagnostics view → нажать payload → ShareLink → отправить себе на Mac.
- **TestFlight / App Store**: Xcode → Window → Organizer → Metrics tab — реальные данные с устройств тестеров (Battery Usage, Hangs, Disk Writes). Доступно после первого Release-build с beta-тестерами.

### Что точно НЕ делать

- НЕ профилировать Energy Log в Instruments на iPhone 15 — он устарел/частично сломан.
- НЕ запускать Diagnostics-тесты под Xcode debugger — MetricKit не доставляет payload'ы при attached debugger.
- НЕ менять MQTT keepalive с 30s без замеренных данных — мы уже видели PINGRESP timeout на cellular middleboxes при 60s.

## Известные ограничения

- **Simulator**: NEPacketTunnelProvider не запускается, тоннель не поднимается. Симулятор используется только для валидации компиляции.
- **Memory limit**: iOS даёт NE Packet Tunnel extension ~50MB. На скоростях >100 Mbps без Release-сборки extension убивается jetsam'ом.
- **Identity ключ device-specific**: каждое устройство (macOS, iOS, R3S, …) генерирует свой identity ключ при первом запуске и регистрируется на exit'е через TOFU. iPhone и MacBook одного человека имеют разные identity, это by design.

## Архитектура

См. `/CLAUDE.md` в корне репозитория. Структура iOS-проекта зеркалирует macOS:

```
clients/ios/Vertex/
├── App/                     # SwiftUI host app
│   ├── VertexApp.swift
│   ├── ViewModels/TunnelViewModel.swift
│   ├── Services/{TunnelManager, SRVDiscovery, Haptics}.swift
│   └── Views/...            # ConnectScreen, BrokerListView, ExitListView, SettingsScreen, …
└── Tunnel/
    └── PacketTunnelProvider.swift   # NEPacketTunnelProvider extension
```

Общий Swift Package: `clients/shared/VertexCore/`.

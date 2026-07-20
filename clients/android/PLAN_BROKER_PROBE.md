# Android: Broker Probe + Auto UX + Sticky pingMs

Порт фич, выполненных на iOS (commits `d6e0a30`, `d3a3fcb`, `7fbba83`,
`85cd8c0`, `c1c21ab`) и macOS (`faed236`), на Android-клиент. Цель —
паритет с iOS:

1. Перед connect-ом измерять TCP-RTT до каждого brokera параллельно и
   переупорядочивать список по возрастанию RTT — failed probes уезжают
   в хвост, чтобы остаться запасным вариантом.
2. Probe запускается ТОЛЬКО когда `selectedBroker == "auto"`. Явный
   выбор пользователя уважается без переупорядочивания (только лог).
3. Synthetic "Auto" pseudo-row в picker'е brokers (как в exit-picker'е),
   `selectedBroker = "auto"` по умолчанию для свежих установок.
4. ServerCard показывает `Auto · YC` (resolved host overlay), как в
   exit-картe `Auto · STO`.
5. `pingMs` — sticky: остаётся виден до следующего успешного измерения
   или фактического disconnect. Не сбрасывается на `stopPolling()`,
   transient timeouts.

## Что уже есть

- ✅ `selectedExit = "auto"` UX + auto-resolve через `DiscoveryTracker`.
- ✅ Sticky reconnect, broker failover в `MqttTransport`.
- ✅ `ConnectionStatus.currentBroker` отражает живой broker.
- ✅ `pingMs` measure через TCP connect к `1.1.1.1:443` в
  `ConnectViewModel.measurePing`.
- ✅ Broker picker `BrokerListScreen`, ServerCard.
- ✅ `lastGoodExit` персистится через `SettingsRepository`.

## Чего не хватает

- ❌ Probe TCP-RTT brokers + reorder — обязательный блок перед стартом
  `MqttTransport`.
- ❌ `selectedBroker = "auto"` как UX-понятие (сейчас всегда конкретный
  URL, см. `SettingsRepository.selectedBroker = ""`).
- ❌ Synthetic "auto" row в `BrokerListScreen`, `presentedBrokers`.
- ❌ "Auto · YC" overlay в `ServerCard` для broker-row.
- ❌ Sticky `pingMs` (сейчас `stopPolling()` сбрасывает в `null`).
- ❌ Передача `selectedBroker` через Intent в `VertexVpnService` →
  `TunnelConfig` → `TunnelEngine`.
- ❌ `pkg/probe`-эквивалент в `:core` модуле (Kotlin port).

## Архитектура

Источник правды = iOS. На каждом этапе сверяться с iOS-кодом и
комментариями (особенно про race-window-cancel в `TCPRTT.swift`,
`SingleShotGate`, и stable sort в `BrokerProbe.swift`).

### Где живёт probe

`:core` модуль (Kotlin pure JVM-friendly), без Android API:

- `core/src/main/kotlin/.../core/probe/TcpRtt.kt` — измеряет TCP RTT
  одного host:port через `Socket.connect(SocketAddress, timeoutMs)` в
  Dispatchers.IO; возвращает `Result<Int>` с elapsed ms.
- `core/src/main/kotlin/.../core/probe/BrokerProbe.kt` —
  `reorderByRtt(brokers, timeoutMs): List<BrokerUrl>` и
  `reorderWithRtts(...) -> Pair<List<BrokerUrl>, Map<String, Int>>` +
  `formatOrder(...)` для лога.

Gotcha: `Socket()` без protect() пройдёт через TUN, если TUN уже
поднят. На Android probe запускается ДО `establish()`, так что TUN ещё
нет — пакеты пойдут через underlying network. Но на всякий случай
надо передать `socketProtector` как опциональный параметр (для случая
если когда-нибудь probe будет вызываться после establish()).

В iOS `TCPRTT` использует `NWConnection` который умеет требовать
конкретный interface; на Android аналог — `Network.bindSocket(Socket)`
после `cm.requestNetwork(...)`. Поскольку probe вызывается ДО `start()`
TunnelEngine'а (=> до establish() и до registerNetworkCallback()),
эту привязку пропустим — обычный `Socket()` в Dispatchers.IO в порядке.

### Где живёт измерение pingMs

ViewModel side: уже работает. Нужно только:
1. Не сбрасывать `_pingMs` в `stopPolling()` — только в
   `ConnectionState.DISCONNECTED` коллекторе и при явном `disconnect()`.
2. Failure → `break` (не сбрасывать), как iOS.

### Где запускается probe

В `TunnelEngine.runHandshakeAndDataPlane()` ПЕРЕД
`transport.start()`. Сейчас `MqttTransport(initialBrokers = ...)`
получает сырой список; нужно разбить на два пути:

- `selectedBroker == "auto"`: probe + reorder, передать
  reordered-список в MqttTransport.
- иначе: `TunnelController` уже двигает selectedBroker в начало; probe
  не запускаем, лог "broker pinned".

`selectedBroker` нужно протащить через Intent → `TunnelConfig` →
`TunnelEngine`.

## Фазы

### Phase 1 — `:core` probe (TcpRtt + BrokerProbe + tests)

Файлы:
- `core/src/main/kotlin/ru/vertices/android/core/probe/TcpRtt.kt`
- `core/src/main/kotlin/ru/vertices/android/core/probe/BrokerProbe.kt`
- `core/src/test/kotlin/ru/vertices/android/core/probe/BrokerProbeTest.kt`
  (≥ 6 тестов: empty, single, partition successful/failed, stable
  tiebreak by index, formatOrder, per-host map dedup)

Поведение должно совпадать с iOS `BrokerProbe` 1:1 (см.
`clients/shared/VertexCore/Sources/VertexCore/Util/BrokerProbe.swift`):

- Stable sort: ascending by RTT, ties → original index.
- Failed → tail в original order.
- Per-host map: lower RTT wins per host (race-зависимый при exact tie,
  diagnostic-only).
- Gotcha race-window: на Android socket.connect() — синхронный с
  встроенным timeout, гонка как в `NWConnection` отсутствует. Просто
  убедиться, что socket закрывается в `finally`.

Параллелизм: `coroutineScope { brokers.map { async { TcpRtt.measure(...) } }.awaitAll() }`.

После: запустить `./gradlew :core:test --tests "*BrokerProbeTest"`.

**Review (Phase 1)**: reviewer agent проверяет:
- Корректность partition / stable sort.
- Нет socket leak (try/finally).
- Парсинг порта default scheme.
- Совпадение публичного API с iOS (имена методов, сигнатуры).

### Phase 2 — `selectedBroker` через config

Файлы:
- `core/src/main/kotlin/ru/vertices/android/core/config/TunnelConfig.kt`
  → добавить `selectedBroker: String = "auto"`.
- `app/.../repository/SettingsRepository.kt` → `KEY_SELECTED_BROKER`
  default value сейчас `""`; добавить `DEFAULT_BROKER = "auto"`,
  Snapshot.selectedBroker, в map fallback на `"auto"` если пусто.
  ВАЖНО: существующие пользователи с saved `selectedBroker` (URL)
  должны сохранить свой выбор (как в iOS).
- `app/.../repository/TunnelController.kt`:
  - Если `snap.selectedBroker == "auto"` → не двигаем ничего, передаём
    список как есть; добавляем extra `EXTRA_SELECTED_BROKER = "auto"`.
  - Если URL — двигаем в начало (как сейчас), extra = URL.
- `vpn/.../VertexVpnService.kt`:
  - `EXTRA_SELECTED_BROKER` → парсится в `parseConfig` → `TunnelConfig.selectedBroker`.
  - Default fallback `"auto"`.
- `app/.../viewmodel/ConnectViewModel.kt`:
  - `presentedBrokers = listOf("auto") + availableBrokers` в state.
  - `ensureValidSelections`: "auto" всегда валиден; не-"auto"
    overwrite только если SRV-список непустой и значения нет в нём
    (как iOS). На пустом списке (cold start) сохраняется saved value.
- `app/.../viewmodel/ConnectViewModel.kt`: `ConnectUiState` → новое
  поле `presentedBrokers: List<String>`.

Не трогаем UI (это Phase 4) — пока всё работает на старом BrokerListScreen
(если saved broker = "auto", покажет первый из availableBrokers как
"selected" — это бажный момент, исправим в Phase 4).

**Review (Phase 2)**: reviewer проверяет:
- TunnelConfig backward-compat (пустой Intent extra не падает).
- DataStore migration: без миграции, ключ просто остаётся пустым у
  старых пользователей, но они зашли — значит у них уже сохранён URL.
- Snapshot/parseConfig симметричны.

### Phase 3 — TunnelEngine probe + reorder

Файлы:
- `vpn/.../TunnelEngine.kt`:
  - Перед `transport.start()` (либо до создания `MqttTransport`):
    if `config.selectedBroker == "auto"` → probe + reorder; иначе
    оставить как есть.
  - Probe timeout = 1500 ms (`BROKER_PROBE_TIMEOUT_MS`).
  - Лог "broker probe (auto, N): host1=Xms host2=fail" / "broker
    pinned: <url>".
  - **Важно**: socket protect не делаем (TUN ещё не поднят).
  - Если probe вернул пустой список (fallback от полностью failed) —
    оставить original config.brokerUrls.

**Review (Phase 3)**: reviewer проверяет:
- Probe не запускается на explicit pick.
- Probe запускается до transport.start() (а не параллельно).
- Никакой socket leak / coroutine leak.
- Логи match iOS формат.

### Phase 4 — Sticky pingMs

Файл: `app/.../viewmodel/ConnectViewModel.kt`:
- `stopPolling()` НЕ сбрасывает `_pingMs.value = null`. Только при
  `ConnectionState.DISCONNECTED` в коллекторе и в `onDisconnectClicked()`.
- `measurePing()` failure → не сбрасывать (сейчас уже корректно —
  `if (rtt != null) _pingMs.value = rtt`, без else). Удалить старый
  комментарий "stopPolling сбрасывает".

Один маленький патч — буквально 5 строк.

**Review (Phase 4)**: reviewer проверяет:
- Sticky behaviour: pingMs остаётся при transient timeouts.
- Сбрасывается при actual disconnect (в одном месте — коллекторе
  status.collect → DISCONNECTED).

### Phase 5 — UI: synthetic Auto row + ServerCard overlay

Файлы:
- `app/.../ui/pickers/BrokerListScreen.kt`:
  - Использовать `ui.presentedBrokers` (включает `"auto"` head).
  - Для `item == "auto"` рендерить специальную `AutoBrokerRow` с
    `VxAsteriskGlyph`, заголовком "Auto", subtitle "Lowest TCP RTT" /
    "Now: <YC>" (из `ui.status.currentBroker`).
  - Tap → `selectBroker("auto")` + back.
- `app/.../ui/connect/ServerCard.kt`:
  - Добавить параметр `resolvedBrokerHost: String? = null`.
  - Если `selectedBroker == "auto"`:
    - Top row title = `Auto` или `Auto · ${resolvedHost.uppercase()}`
      (короткое имя из `NodeLabels.vertexLabel(...).shortName`).
    - Скрыть `vertexCode`.
- `app/.../ui/connect/ConnectScreen.kt`:
  - Передать `resolvedBrokerHost = ui.status.currentBroker` в
    ServerCard.

**Review (Phase 5)**: reviewer проверяет:
- A11y: contentDescription для AutoRow.
- Default selected = "auto" → BrokerListScreen показывает Auto row
  выделенной.
- ServerCard: на explicit pick `vertexCode` виден; на auto скрыт.
- Аналог iOS BrokerListView ровно.

### Phase 6 — Финальная проверка + bump версии

- Сборка: `make build-android` (debug, smoke).
- Sanity: запустить unit-тесты `./gradlew :core:test`.
- Bump версии (PATCH или MINOR — это feature parity, MINOR):
  - `clients/android/Vertex/app/build.gradle.kts` → versionName /
    versionCode (Makefile-driven; см. `verName/verCode`). Bump через
    Makefile/CLI -P, или через дефолт в build.gradle.kts если нет
    свежего тэга. Проверить `git tag` для актуальной версии.
- Не деплоить на устройство (юзер сам тестит сборку, см.
  `feedback_deploy_explicit`).

**Review (Phase 6)**: reviewer agent делает финальный pass по всем
изменениям; security agent проверяет, что probe не открывает
плоскость уязвимости (DoS на TCP connect множественный — нет, мы сами
инициатор; информационный leak — DNS lookup до broker'а уже происходит,
ничего нового не добавилось).

## Acceptance criteria

- ☐ `:core:test` проходит (включая новые BrokerProbeTest).
- ☐ `make build-android` проходит без warnings.
- ☐ Свежий install: `selectedBroker = "auto"`, BrokerListScreen
  показывает Auto row выделенной.
- ☐ Существующий пользователь с saved URL (mqtts://...) — selected
  остаётся URL, probe не запускается, как в iOS.
- ☐ В Auto-режиме лог содержит `broker probe (auto, N): ...`.
- ☐ ServerCard на Auto показывает "Auto · <host.uppercase>" после
  CONNECTED.
- ☐ pingMs остаётся виден после background→foreground цикла (только
  тестит юзер).
- ☐ Visual паритет с iOS BrokerListView/ServerCard (юзер посмотрит).

## Не делаем

- Probe внутри `MqttTransport` (он живёт в :core, а probe на Android
  через стандартный Socket — это тоже OK для :core; решено: probe
  запускается из `TunnelEngine`, до старта transport, как в iOS).
- Socket protection (TUN ещё не поднят, не нужен).
- Отдельный `Network.bindSocket` на конкретный underlying network —
  оверкилл для probe длительностью 1.5s, defaults справятся.
- Persistence `lastGoodBroker` — iOS этого не делает (не путать с
  `lastGoodExit` который уже есть).
- WSS/HTTP probe — мы измеряем чисто TCP, как iOS (исключая TLS
  handshake), достаточно для медианной разницы между brokerами.

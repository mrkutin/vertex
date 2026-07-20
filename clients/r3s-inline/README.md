# Vertex R3S-inline

Второй, **независимый** вариант R3S-клиента для Vertex VPN.

В отличие от существующего `clients/gateway/` (который работает в паре с Mikrotik — Микротик policy-routing'ом гонит трафик на R3S), этот вариант ставится **в разрыв** между ISP и юзерским простым роутером (TP-Link, Keenetic, Asus). R3S сам выступает шлюзом, юзерский роутер за ним делает свой NAT.

```
┌────────┐    ┌──────────────────────┐    ┌──────────────┐    ┌──────┐
│  ISP   │────│   R3S-inline         │────│  Юзерский    │────│ LAN  │
│ PPPoE/ │WAN │  eth0 → pppoe0/eth0  │LAN │   роутер     │    │ устр.│
│ Ether  │    │  eth1 → 192.168.42.1 │    │ (DHCP, NAT)  │    │      │
└────────┘    │  + dnsmasq + TUN     │    └──────────────┘    └──────┘
              └──────────────────────┘
                       │
                       │ MQTT (broker) + TUN (exit)
                       ▼
                Vertex broker × 3, exit × 3
```

## Кому это для

Пользователю с простым роутером (без гибкого policy routing) и желанием «воткнул железку — VPN заработал». Юзеру не нужно ничего настраивать на своём роутере — только подключить кабели и пройти bootstrap web-UI.

## Отличия от существующего R3S (`clients/gateway/`)

| | `clients/gateway/` (Mikrotik-mode) | `clients/r3s-inline/` (inline-mode) |
|---|---|---|
| Топология | R3S = device в LAN Микротика, Микротик гонит трафик через R3S | R3S = шлюз в разрыв ISP↔роутер |
| Routing на upstream | Микротик policy-routing | R3S сам форвардит eth1↔WAN/TUN |
| WAN на R3S | eth0 от Микротика (LAN-side) | eth0 PPPoE/Ethernet+DHCP от ISP |
| LAN на R3S | — (Микротик — LAN) | eth1 = 192.168.42.1/24 + dnsmasq DHCP |
| Setup | вручную SSH + правка `/etc/vertex.yaml` | Web-UI bootstrap на 192.168.42.1 |
| Конфиги | `/etc/vertex.yaml`, `/etc/vertex.env` | `/etc/vertex-inline/router.yaml`, `/etc/vertex-inline/inline.env` |
| systemd units | `vtx-gateway.service` | `vtx-inline-{lan,setup,wan,firewall,router}.service` |
| Бинари | `vtx-gateway` | **тот же `vtx-gateway`** + новый `vtx-inline-setup` |
| Identity TOFU | `/etc/vertex/identity.key`, одна запись на AWS/STO/AMS | `/etc/vertex-inline/identity.key`, генерится при first boot, **отдельная** запись на каждый R3S |

## Жёсткое правило (внутреннее)

**ZERO** правок в `clients/gateway/**`, `DEPLOY.md` (секции про gateway/R3S), существующих Makefile-targets `build-gateway`/`deploy-r3s`, форматах `/etc/vertex.yaml` и `/etc/vertex.env`. Production-инсталляция Mikrotik-R3S работает в домашней сети и **не трогается**.

Все нужные файлы — **копия** в `clients/r3s-inline/`, независимая поддержка. После того как inline заработает в production — отдельной задачей провести анализ что вынести в shared.

## Структура

```
clients/r3s-inline/
  cmd/setup/             # vtx-inline-setup: bootstrap web-UI бинарь (~200 LOC)
  scripts/               # vtx-inline-proxy.sh (копия) + WAN/firewall/install скрипты
  systemd/               # 5 unit-ов с префиксом vtx-inline-*
  configs/               # dnsmasq, networkd, PPPoE peer template
```

## Архитектурные решения (фиксированы)

- **Топология**: router-mode + double NAT.
- **Порты**: eth0 = WAN, eth1 = LAN (фиксированно, документировано в инструкции по подключению).
- **WAN типы**: PPPoE (через `pppd` + `rp-pppoe`) ИЛИ Ethernet+DHCP (через `systemd-networkd`). Выбор юзером в web-UI bootstrap.
- **LAN subnet**: `192.168.42.0/24` (R3S = `.1`).
- **DNS**: dnsmasq только DHCP (`port=0`), отдаёт downstream роутеру `1.1.1.1`. R3S сам не резолвит — нейтрален.
- **RU split**: `ipset ru-nets` (как в Mikrotik-варианте).
- **Identity key**: генерируется при first boot, каждый R3S = новая TOFU-запись на STO/AMS.
- **Web-UI**: **персистентный** (всегда доступен с eth1), без auth, доступ только с eth1 (INPUT firewall: DROP 80/443/22 с WAN). Sentinel `/etc/vertex-inline/.setup-done` — монотонный флаг "когда-либо успешный bootstrap", для UI переключает форму в режим **Reconfigure** (предзаполняет non-secret поля; пустые пароли → сохраняются текущие из disk).
- **Брокер/Exit selector**: SRV-based, как у iOS/macOS/Android (`_mqtt._tcp.vertices.ru` + `_vtx-exit._tcp.vertices.ru`). Cached 60s + background refresh. Per-(broker, scheme): `yc-mqtts`, `yc-wss`, `sber-mqtts`, `sber-wss` — позволяет форсировать WSS:443 для DPI-блокированных сетей. Auto = все URLs (default).
- **Reconfigure atomic**: на submit snapshot текущих конфигов в `.bak` → stop services → write new → start services. При любой ошибке rollback восстанавливает `.bak` и поднимает старый stack — VPN не падает при опечатке.
- **Save without applying**: чекбокс "Сохранить без применения" в форме — пишет конфиги + enable units, не стартует. Текущий VPN работает в памяти до выключения. На новом месте после power-cycle systemd сам поднимает units с новой конфигурацией. Use case: pre-configure PPPoE creds для целевого ISP, не находясь там физически.
- **Updates**: ручной `scp` + `systemctl restart` (как с `clients/gateway/`).
- **OS**: Ubuntu 24.04 ARM64.

## Установка / эксплуатация

См. `DEPLOY-inline.md` (TBD по мере прохождения phase-ов).

## Текущий статус

Код готов, ждёт первого live R3S для приёмки сценариев. План в `~/.claude/plans/humble-painting-pearl.md`.

- [x] Phase 0 — скелет директории + копия `vtx-proxy.sh`
- [x] Phase 1 — LAN baseline (`networkd-lan.network` + `dnsmasq.inline.conf` + `vtx-inline-lan.service`)
- [x] Phase 2 — WAN bring-up (PPPoE через pppd + Ethernet+DHCP через networkd, ip-up.d hooks)
- [x] Phase 3 — bootstrap-бинарь `vtx-inline-setup` (web-UI на 192.168.42.1:80, CSRF, embed templates)
- [x] Phase 4 — `vtx-inline-router.service` (вызывает существующий vtx-gateway) + 3 правки в `vtx-inline-proxy.sh` (defensive RPS, MSS clamp, FORWARD ACCEPT)
- [x] Phase 5 — `vtx-inline-firewall.service` + `vtx-inline-firewall.sh` (INPUT chain, IPv6 disable, ICMP rate-limit)
- [x] Phase 6 — `install.sh` + Makefile targets (`build-inline-setup`, `build-inline`, `deploy-inline-r3s`) + cloud-init flow
- [~] Phase 7 — live reliability scenarios (частично, см. `DEPLOY-inline.md`)
  - [x] First boot full path (DHCP WAN, manual deploy)
  - [x] Split routing (RU direct / non-RU через TUN) — `traceroute` подтверждает
  - [x] Identity TOFU registration на AMS exit
  - [x] **Reconfigure flow** — submit с теми же creds → атомарно stop/write/start, `.bak` cleanup, ~3с downtime
  - [x] **WSS pin** — выбор `sber-wss` → router.yaml содержит **только** `wss://mqtt-sber.vertices.ru:443`, vtx-gateway соединяется через :443
  - [x] **Exit pin** — выбор `sto` → vtx-gateway joins STO с правильным subnet `10.9.2.0/24` (10.9.2.6/24 на vtx0)
  - [ ] Reboot после setup, PPPoE WAN, throughput, nmap извне, CGNAT

### Post-MVP fixes из live теста

- ✅ **vtx-admin sync path bug** — было: sync копирует source-path → same dest-path → при VTX_PASSWD_FILE=/tmp/bt_passwd файлы попадали на брокер в /tmp/, не в /etc/mosquitto/. Фикс: VTX_REMOTE_PASSWD_FILE + `sudo install` через stage в /tmp на стороне брокера (commit `8a94b75`).
- ✅ **DHCP ClientIdentifier=mac** — netplan→networkd миграция меняла client-id на DUID, static lease по MAC не матчился. Добавлен `[DHCP] ClientIdentifier=mac` (commit `535907a`).
- ✅ **vtx-gateway exit-switch race** — pre-existing баг в `clients/gateway/main.go` + `clients/cli/main.go`: после rebalance keepalive старого exit'а race-выигрывал в joinResp канале → TUN оставался со старым IP. Фикс: strict target-only filter (`pkg/protocol.ShouldAcceptControl`), regression test в pkg/protocol + Docker E2E test (commit `2b0fde5`).
- ✅ **Persistent web-UI + atomic reconfigure** — для смены сетей/ISP без SSH. Snapshot/restore защищает работающий VPN при опечатке (commit `e36648a`).
- ✅ **SRV-based broker/exit picker** — динамически из DNS, как iOS/macOS (commit `d1b3415`).
- ✅ **Per-(broker, scheme) selector + keep-current-password** — выбор `sber-wss` для DPI-bypass, пустой пароль на reconfigure сохраняет текущий (commit `b1ebbf3`).

# Vertex R3S-inline — deploy / эксплуатация

## Обзор

R3S включается **в разрыв** между ISP (PPPoE или Ethernet/DHCP) и юзерским простым роутером. R3S сам выступает шлюзом: WAN-сторона `eth0` смотрит к провайдеру, LAN-сторона `eth1` раздаёт `192.168.42.0/24` по DHCP юзерскому роутеру. Юзерский роутер делает свой NAT внутрь домашней сети — double-NAT, но это нормально для VPN-сценария.

```
[ISP]→eth0  R3S  eth1→[Юзерский роутер WAN]→[Домашняя LAN]
              │
              ├─ vtx0 (TUN) → VPN-туннель к AWS/STO/AMS exit
              └─ split routing: RU IP → direct, остальное → tunnel
```

Бинарь VPN-клиента — существующий **`vtx-gateway`** (без правок исходников). Конфиги — отдельная папка `/etc/vertex-inline/` чтобы не конфликтовать с production-R3S (Mikrotik-mode).

## Подготовка SD-карты (для админа)

### Метод A — ручная установка по SSH

Применимо когда у вас есть физический доступ к R3S или SSH-доступ к нему через временный канал.

1. На R3S — чистая Ubuntu 24.04 ARM64 (например, образ NanoPi с FriendlyArm или официальный Ubuntu Server arm64 + правильный device-tree).
2. С dev-машины:
   ```bash
   make build-inline               # собирает dist/r3s-inline/payload/
   INLINE_HOST=<ssh-alias> make deploy-inline-r3s
   ```
   `INLINE_HOST` должен иметь passwordless `sudo` (см. preflight в Makefile).
3. R3S настроен. `poweroff`, передать юзеру.

### Метод B — cloud-init на SD-карте

Для массового рулона — R3S настраивается автоматически на первом boot.

1. Flash стандартного Ubuntu 24.04 ARM64 server образа на microSD (например, через `dd` или Balena Etcher).
2. Собрать payload:
   ```bash
   make build-inline
   tar -czf vtx-inline-payload.tar.gz -C dist/r3s-inline/payload .
   ```
3. На FAT-разделе SD (`/boot/firmware/`) положить:
   - `vtx-inline-payload.tar.gz` (только что собран)
   - `user-data`:
     ```yaml
     #cloud-config
     hostname: vtx-inline
     users:
       - name: admin
         ssh_authorized_keys:
           - ssh-ed25519 AAAA... (ваш ключ)
         sudo: ALL=(ALL) NOPASSWD:ALL
         groups: [sudo]
         shell: /bin/bash
     ssh_pwauth: false
     package_update: false
     # Всё в одной команде с &&: если install.sh падает — poweroff НЕ
     # вызывается, оператор видит "boot повис" и понимает что setup failed.
     # `sync` перед poweroff гарантирует что /var/log/vtx-inline/install.log
     # запишется на диск.
     runcmd:
       - [bash, -ec, "mkdir -p /tmp/vtx-inline && tar xf /boot/firmware/vtx-inline-payload.tar.gz -C /tmp/vtx-inline && cd /tmp/vtx-inline && bash scripts/install.sh && sync && poweroff"]
     ```
   - `meta-data` (NoCloud datasource требует минимум instance-id, иначе
     cloud-init считает каждый boot новой инстансой и re-runs `runcmd`):
     ```yaml
     instance-id: vtx-inline-prebuild
     local-hostname: vtx-inline
     ```
     На самой выдаваемой юзеру SD `instance-id` можно поменять на
     уникальный (например, `vtx-inline-$(uuidgen)`), но и фиксированное
     значение работает — cloud-init после первого успешного boot создаёт
     `/var/lib/cloud/instance/sem/` и больше не перевыполняет `runcmd`.
4. Insert SD в R3S, boot, cloud-init отрабатывает install.sh, R3S выключается.
5. SD готова к отправке юзеру.

Лог установки: `/var/log/vtx-inline/install.log`.

## First-boot flow для пользователя

1. Юзер подключает кабели: **ISP → eth0** (порт ближе к USB или к питанию — указать в инструкции для конкретной модели R3S), **WAN-порт его роутера → eth1**.
2. Подаёт питание. Через ~20-30s юзерский роутер получает DHCP-lease `192.168.42.x` (R3S — `192.168.42.1`).
3. С устройства за юзерским роутером (или подключённого напрямую к eth1) открывает `http://192.168.42.1/`.
4. Форма:
   - **Тип WAN**: PPPoE / Ethernet+DHCP.
   - **PPPoE creds** (если PPPoE): логин + пароль провайдера.
   - **Vertex creds**: `client_name` + MQTT-пароль (выдаются админом отдельно — например в письме или по SMS, **уникальные на каждое устройство**).
   - **Брокер** (опц.): `auto` (default — выбирается по RTT, MQTTS + WSS fallback) или конкретный `{id}-mqtts` / `{id}-wss`. Pin `*-wss` форсит WSS:443 — для сетей с DPI-блоком 8883.
   - **Exit-нода** (опц.): `auto` (по RTT и нагрузке) или конкретный (`sto`/`ams`/…).
5. Submit → bootstrap-бинарь автоматически делает в нужном порядке:
   `systemctl enable+start vtx-inline-wan → vtx-inline-firewall → vtx-inline-router`.
   Юзеру ничего не надо вводить в SSH. Через ~60s VPN-туннель up.
6. **Web-UI остаётся доступен** на `http://192.168.42.1/` — для смены настроек при переезде в другую сеть (новые PPPoE-creds, exit pin и т.п.). В режиме Reconfigure форма предзаполняет non-secret поля; **пустые пароли = сохраняются текущие** (hint в форме).

### Списки брокеров и exit-нод

Динамически из DNS-SRV (`_mqtt._tcp.vertices.ru` + `_vtx-exit._tcp.vertices.ru`), как iOS/macOS/Android. Cache 60s + background refresh. Если DNS недоступен (cold first-boot до WAN) — fallback на hardcoded короткий список + только `auto` для exit'ов.

### Атомарный reconfigure

На каждый submit (когда sentinel уже существует) копируются `.bak` файлы конфигов ДО изменений. При failure (например, неверный PPPoE-пароль → wan-up.sh падает) rollback восстанавливает `.bak` и поднимает stack на СТАРОЙ конфигурации — **работающий VPN не падает**. Юзер заполняет форму заново.

### Отложенное применение (Save without applying)

Чекбокс в форме "Сохранить без применения — для переноса R3S в другую сеть". Use case: ввести PPPoE creds под целевого провайдера, не находясь физически в этой сети.

- Submit с галочкой → applyConfig пишет router.yaml + inline.env + PPPoE peer + chap-secrets + sentinel, делает `daemon-reload + enable`, но **НЕ стартует** wan/firewall/router
- Текущий VPN (если работает) продолжает работать **на in-memory конфигурации**, не перезагружая сервисы
- Юзер выключает R3S → перевозит → подключает в целевой сети → включает
- systemd на boot запускает все units с новой конфигурацией → wan-up.sh дозванивается до реального ISP → VPN up
- Если новая конфигурация неисправна — web-UI на `http://192.168.42.1/` доступен сразу после загрузки (lan.service не зависит от WAN), юзер открывает форму и исправляет

**Trap**: если работающий сервис рестартанёт по любой причине ДО power-cycle (OOM, manual systemctl restart, crash + on-failure-restart) — он подхватит **новую** конфигурацию с диска и попробует подключиться к новому ISP из текущей сети → failure. В journalctl будет строка `SaveOnly: configs written but services NOT restarted; running services may pick up new config on unexpected restart`.

### Юзер видит ошибку «Не удалось поднять WAN»

→ Неверные PPPoE-логин/пароль или ISP-кабель не подключён. Форма re-renders с заполненными нечувствительными полями. Юзер вводит пароли заново и нажимает Сохранить.

### Юзер видит ошибку «WAN работает, но VPN не поднялся»

→ Неверный MQTT-пароль / `client_name` (или конфликт TOFU). Админ проверяет:
```bash
ssh <inline-host>
sudo journalctl -u vtx-inline-router -e
# поиск "identity mismatch" или "MQTT auth failed"
```

## Повторная настройка (юзер сменил PPPoE-creds, MQTT-пароль, exit, и т.п.)

Web-UI **всегда доступен** на `http://192.168.42.1/` с eth1-стороны. Юзер открывает форму — non-secret поля предзаполнены (WAN type, имя клиента, PPPoE-логин, выбранный broker/exit). Заполняет только то что меняется, **пустые пароли = сохраняются текущие**. Submit → атомарный reconfigure (см. выше).

Никакого SSH или физической кнопки.

**"Factory reset"** (полный сброс — заново с нуля): по-прежнему через SSH (требует доступа админа):

```bash
ssh <inline-host>
sudo rm /etc/vertex-inline/.setup-done /etc/vertex-inline/router.yaml /etc/vertex-inline/inline.env
sudo systemctl stop vtx-inline-router vtx-inline-firewall vtx-inline-wan
sudo systemctl restart vtx-inline-setup
# Web-UI снова в режиме "Первоначальная настройка"
```

Web-кнопка factory-reset в форме — future enhancement (требует доп. UX для подтверждения).

## Диагностика

С R3S (по SSH):

```bash
# Общий обзор
systemctl status vtx-inline-*           # все 5 unit-ов
cat /var/log/vtx-inline/install.log     # лог установки

# WAN bring-up (#1 ожидаемый класс багов на первой live-инсталляции)
networkctl status eth0 eth1             # link state + конфиг от networkd
pgrep -af pppd                          # PPPoE: есть ли процесс
ip a show pppoe0 2>/dev/null            # PPPoE: есть ли интерфейс
ip -4 route show default                # есть ли default route на WAN
journalctl -u vtx-inline-wan -e         # WAN-сервис логи
cat /etc/vertex-inline/inline.env       # текущая конфигурация
cat /etc/ppp/peers/vtx-isp 2>/dev/null  # PPPoE peer (только PPPoE mode)

# VPN-туннель
journalctl -u vtx-inline-router -f      # live логи vtx-gateway
ip a show vtx0                          # TUN intf + IP

# Routing / firewall
ip a                                    # все интерфейсы
ip route show table 100                 # split-routing таблица
ipset list ru-nets | head               # RU подсети (должно быть ~8500)
iptables -t mangle -L VTX_FORWARD -n -v # MARK rules
iptables -L VTX_INPUT -n -v             # firewall INPUT
iptables -t nat -L POSTROUTING -n -v    # MASQUERADE
iptables -L FORWARD -n -v               # inline FORWARD ACCEPT правила
```

С устройства за юзерским роутером:

```bash
curl https://api.ipify.org              # должен показать exit IP (не ISP)
curl https://yandex.ru/ping             # RU — должен идти напрямую через ISP
traceroute 8.8.8.8                      # должен пройти через 192.168.42.1 → 10.9.x.1 (TUN gw на exit)
```

## Сценарии надёжности (для приёмки)

Прогон первой live-инсталляции на тестовом R3S (10.0.0.17, MAC F2:E9:C7:F4:E0:29) — **2026-05-19**:

- [x] **First boot full path (DHCP WAN)**: SD-flash + ручной деплой через `INLINE_HOST=inline-test make deploy-inline-r3s` → systemctl start lan+setup → ноут на eth1 получил DHCP `192.168.42.50` + DNS `1.1.1.1` → форма на `http://192.168.42.1/` отдалась → submit с client_name + MQTT pass → VPN up за <60s. Файл `/var/log/vtx-inline/install.log` присутствует.
- [x] **Split routing (RU direct / non-RU через TUN)**: `traceroute -n yandex.ru` (5.255.255.77) → hop1=`192.168.42.1`, hop2=`10.0.0.1` (Mikrotik = ISP), hop3+ → ISP. `traceroute -n 8.8.8.8` → hop1=`192.168.42.1`, hop2=`10.9.3.1` (AMS TUN gw), hop3=`95.140.146.1` (Timeweb AMS). Подтверждено: mangle `match-set ru-nets dst -j RETURN` работает, остальное идёт через `fwmark 1 → table 100 → vtx0`.
- [x] **Identity TOFU registration**: первый join к AMS exit прошёл без identity mismatch error → запись добавлена в `/var/lib/vtx-devices.json` на AMS.
- [x] **Reconfigure через web-UI** (2026-05-19): submit с теми же creds → атомарно stop → snapshot → write → start. VPN восстановился за ~3с, `.bak` файлы прибраны. Все 5 unit-ов active.
- [x] **Broker pin (WSS)** (2026-05-20): submit `broker=sber-wss` → router.yaml содержит **только** `wss://mqtt-sber.vertices.ru:443`. vtx-gateway соединяется через :443, обходит DPI-блок 8883.
- [x] **Exit pin** (2026-05-20): submit `exit=sto` → router.yaml `exit: sto`, vtx-gateway joins STO, vtx0=`10.9.2.6/24` (правильный STO subnet `10.9.2.0/24`). Подтверждает что фикс exit-switch race работает для initial join тоже.
- [x] **Keep-current-password на reconfigure**: пустой `mqtt_password` → загружается из `cfg.Pass` в router.yaml. Пустой `pppoe_password` → парсится из `/etc/ppp/chap-secrets`. HTML `required minlength` снимается на reconfigure.
- [ ] **Reboot после setup**: TBD на следующем live-цикле.
- [ ] **PPPoE WAN**: TBD — этот тест проводился в DHCP-mode (R3S за Mikrotik как "ISP router"). PPPoE-режим требует реального PPPoE-провайдера.
- [ ] **Exit failover**: **БАГ обнаружен в vtx-gateway** (про-existing, не inline-специфичный) — после auto-rebalance `ams → sto` лог пишет `[switch] done (IP 10.9.3.3)`, но `10.9.3.0/24` это AMS subnet; STO должен быть на `10.9.2.0/24` (см. `project_sto_exit.md` memory). TUN IP не обновляется при смене exit'а → пакеты к STO не возвращаются. Workaround: pin `exit: ams` в `router.yaml`. Тест отложен до фикса в `clients/gateway/`.
- [ ] **Повторная настройка PPPoE**: TBD.
- [ ] **Throughput**: TBD — speedtest CLI с ноута за R3S, цель ≥80 Mbps.
- [ ] **nmap извне**: TBD — `nmap -p 22,80,443 <WAN-IP>` с 4G или внешнего хоста.
- [ ] **CGNAT WAN**: TBD.

### Известные проблемы, обнаруженные на первом прогоне

1. **`vtx-admin sync` копирует source-path → same dest-path.** При локальном запуске с `VTX_PASSWD_FILE=/tmp/bt_passwd` файлы попадают на брокер в `/tmp/bt_passwd`, а не в `/etc/mosquitto/bt_passwd`. Результат: новый пользователь "synced" но реально отсутствует в активном файле, MQTT auth reason 135. **Workaround**: после `vtx-admin add` запускать `scp /tmp/bt_passwd broker:/etc/mosquitto/bt_passwd` + `ssh broker 'kill -HUP $(pidof mosquitto)'` вручную. Либо запускать vtx-admin прямо на брокере (требует установки бинаря туда). Документировать в README `server/cmd/admin/`.
2. **`StartLimitIntervalSec=` в `[Service]` секции `vtx-inline-router.service`**. Этот ключ должен быть в `[Unit]`, systemd выкидывает warning в журнал на каждом старте. Не блокер. Фикс в следующем коммите.
3. **DHCP ClientIdentifier change after netplan → networkd**. install.sh переводит сетевой стек с netplan на networkd. Networkd по умолчанию использует другой `ClientIdentifier` чем netplan → DHCP-сервер на роутере провайдера может выдать **новый IP** (не подтвержденный по MAC static lease, если был выпущен под netplan). На нашем тесте это не критично (Mikrotik выдал `.17` вместо настроенного `.13`). Для production-сценария с PPPoE проблема не возникает (PPP не использует DHCP).
4. **vtx-gateway exit-switch не обновляет TUN IP/subnet** (про-existing, не inline). См. выше. Issue trackable.
- [ ] **nmap извне**: с 4G hotspot или внешнего хоста `nmap -p 22,80,443 <WAN-IP>` — все closed/filtered.
- [ ] **CGNAT WAN** (для региональных RU ISP): если WAN IP получается из 100.64.0.0/10 или RFC1918, проверить что VPN всё равно поднимается. vtx-gateway делает только outbound MQTT (8883/443), CGNAT не должен мешать — но проверить эмпирически.

## Открытые вопросы / future work

- **HTTPS для bootstrap web-UI**: MVP без TLS (self-signed бессмысленно для closed LAN, активен один раз). Future: автогенерировать self-signed cert + помочь юзеру bypassить browser warning.
- **Auto-update**: ручной `make deploy-inline-r3s` для каждого юзера. Future: подпись + auto-pull из `vertices.ru/downloads/r3s-inline/`.
- **Reset-button**: физическая кнопка GPIO на R3S → сброс `.setup-done`. Future.
- **Status-dashboard**: добавить страницу `/status` в `vtx-inline-setup` (current exit, ping, throughput). Future.
- **shared-files analysis**: триггер — **после 2-х production inline установок ИЛИ первого момента, когда фикс в `vtx-proxy.sh` нужно cherry-pick'ать в `vtx-inline-proxy.sh`** (либо наоборот). Тогда отдельной задачей определить какие файлы можно вынести в `clients/shared/` и подключать симлинком/Go-пакетом, чтобы избежать долгосрочного дрейфа двух копий.

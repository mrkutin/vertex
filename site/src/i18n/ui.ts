// Translation dictionary for shell chrome (header / footer / common CTAs).
// Page content lives in dedicated files per locale.

export const languages = {
  en: 'English',
  ru: 'Русский',
} as const;

export const defaultLang = 'en' as const;
export type Lang = keyof typeof languages;

export const ui = {
  en: {
    'nav.features': 'Features',
    'nav.download': 'Download',
    'nav.support': 'Support',

    'cta.download': 'Download',
    'cta.learnMore': 'Learn more',

    'footer.tagline': 'Where paths meet.',
    'footer.product': 'Product',
    'footer.legal': 'Legal & Support',
    'footer.privacy': 'Privacy Policy',
    'footer.terms': 'Terms',
    'footer.support': 'Support',
    'footer.copyright': '© 2026–present Vertex',

    'theme.toggle': 'Toggle theme',
    'theme.dark': 'Dark',
    'theme.light': 'Light',

    'lang.toggle': 'Switch language',

    'hero.heading': 'Vertex',
    'hero.tagline': 'Where paths meet.',
    'hero.subtag':
      'A privacy-focused VPN built around a relay-vertex topology. End-to-end encrypted and transport-resilient.',
    'hero.graph.title': 'Vertex network graph',
    'hero.graph.desc':
      'A central node radiating connections to satellite nodes; small packets travel along the edges.',
    'hero.graph.caption': 'client → vertex → exit',

    'a11y.skipToContent': 'Skip to content',
    'a11y.openMenu': 'Open menu',
    'a11y.closeMenu': 'Close menu',

    // Numbers strip
    'numbers.throughput.value': '100+ / 100+',
    'numbers.throughput.unit': 'Mbps',
    'numbers.throughput.label': 'measured Ethernet throughput',
    'numbers.failover.value': '~100',
    'numbers.failover.unit': 'ms',
    'numbers.failover.label': 'vertex failover',
    'numbers.split.value': '8585',
    'numbers.split.unit': 'subnets',
    'numbers.split.label': 'split-routed direct',

    // Home features grid
    'home.features.heading': 'Designed around indirection',
    'home.features.lede':
      'Six guarantees that follow naturally from the relay-vertex topology — not features bolted on after the fact.',
    'home.features.e2e.title': 'End-to-end encrypted',
    'home.features.e2e.desc':
      'X25519 Diffie-Hellman handshake derives a session key per device. Packets are sealed with ChaCha20-Poly1305 before they ever leave your device — the relay vertex only ever sees ciphertext.',
    'home.features.vertex.title': 'Multi-vertex fan-out',
    'home.features.vertex.desc':
      'Several independent relay vertices, exit servers connected to all of them, automatic client failover in roughly 100 ms. A single vertex outage is invisible.',
    'home.features.exit.title': 'Auto exit-select',
    'home.features.exit.desc':
      'Clients probe each relay vertex and read exit load on every connect, then pick the shortest path. Manual override is one tap away.',
    'home.features.identity.title': 'Device identity (TOFU)',
    'home.features.identity.desc':
      'A leaked password isn\'t enough. The exit pins your device on first use; an attacker also needs the identity key file from your machine.',
    'home.features.split.title': 'Split routing',
    'home.features.split.desc':
      'On-device CIDR table sends Russian destinations direct, the rest through the tunnel. 8 585 subnets, updated with each release.',
    'home.features.dpi.title': 'Censorship-resistant transport',
    'home.features.dpi.desc':
      'On the wire Vertex looks like ordinary encrypted web traffic — keeps working where conventional VPN protocols are filtered.',

    // Architecture
    'home.arch.heading': 'How the topology fits together',
    'home.arch.lede':
      'A vertex is the meeting point. Your client and the exit talk through it; the vertex itself only relays opaque ciphertext.',

    // How it works
    'home.how.heading': 'Three steps',
    'home.how.step1.title': 'Install',
    'home.how.step1.desc': 'Native apps for iOS, macOS, Android and Windows. Linux desktop and gateway builds available on request.',
    'home.how.step2.title': 'Authenticate',
    'home.how.step2.desc': 'First launch generates an X25519 identity key locally. The exit registers it on first use.',
    'home.how.step3.title': 'Connect',
    'home.how.step3.desc': 'Toggle the switch. The client picks the best vertex and exit, hands off seamlessly when the network changes.',

    // Download row
    'home.download.heading': 'Get it for your platform',
    'home.download.lede': 'Native, minimal, no telemetry.',
    'home.download.cta': 'See all downloads',

    // CTA
    'home.cta.heading': 'No telemetry. No analytics.',
    'home.cta.lede':
      'Vertex is built around a single design principle: the network can\'t see your traffic, and we don\'t want to either.',
    'home.cta.primary': 'Get Vertex',
    'home.cta.secondary': 'Read the privacy policy',

    // Features page
    'features.title': 'Features — Vertex',
    'features.heading': 'What\'s under the hood',
    'features.lede':
      'Vertex is a small set of design choices applied consistently. This page goes deeper than the home page — same six pillars, with the cryptography and the configuration spelled out.',
    'features.e2e.heading': 'End-to-end encryption',
    'features.e2e.body':
      'Every packet between your device and the exit server is sealed with ChaCha20-Poly1305 using a session key derived from an X25519 Diffie-Hellman exchange done at connect time. The relay vertex has no key material; even if it logged every byte it relayed, it would have only ciphertext. The session key is per-device-per-connect, giving you Perfect Forward Secrecy at the session level.',
    'features.e2e.detail1': 'X25519 ephemeral keypair on every connect',
    'features.e2e.detail2': 'HKDF-SHA256 derives 256-bit session key',
    'features.e2e.detail3': 'ChaCha20-Poly1305 AEAD on every packet',
    'features.e2e.detail4': '12-byte random nonce + 16-byte auth tag (28 B overhead)',
    'features.e2e.detail5': 'No persistent shared secrets between client and exit',
    'features.vertex.heading': 'Multi-vertex fan-out',
    'features.vertex.body':
      'Vertex runs several independent relay vertices, hosted with different infrastructure providers in different regions, with no replication or clustering between them. Exit servers connect to all vertices; clients keep an ordered list and reconnect to the next available vertex on failure. The handoff is roughly 100 ms — fast enough that streaming media and SSH sessions usually survive without dropping.',
    'features.vertex.detail1': 'Multiple independent relay vertices, hosted across different infrastructure providers',
    'features.vertex.detail2': 'No replication or clustering between vertices — each is fully standalone',
    'features.vertex.detail3': 'Vertex list is discovered automatically; clients always reach the freshest set',
    'features.vertex.detail4': 'Exit servers connect to every vertex, replies travel back through the path the client used',
    'features.vertex.detail5': 'Sticky reconnect: last-good vertex is tried first',
    'features.exit.heading': 'Auto exit-select',
    'features.exit.body':
      'On connect the client measures the round-trip to each reachable exit and reads its current load. It picks the lowest RTT under load weighting and routes through that exit. Re-evaluation happens every five minutes; a manual override in the UI takes immediate precedence.',
    'features.exit.detail1': 'Exits announce themselves with periodic, lightweight heartbeats',
    'features.exit.detail2': 'Score balances measured RTT against current load — RTT-dominant',
    'features.exit.detail3': 'Re-evaluate every 5 minutes',
    'features.exit.detail4': 'Manual exit pin overrides auto-select',
    'features.exit.detail5': 'Multiple production exits across independent regions',
    'features.identity.heading': 'Device identity (TOFU)',
    'features.identity.body':
      'Each client generates an X25519 keypair on first launch and keeps the private half on the device (Keychain on Apple, EncryptedFile on Android, file on Linux). The public half is sent to the exit during the join handshake; the exit pins it the first time it sees it (Trust On First Use) and refuses any future connection from the same username with a different identity. A leaked password is not enough to get into the network.',
    'features.identity.detail1': 'X25519 keypair, 32 bytes public + 32 bytes private',
    'features.identity.detail2': 'HMAC-SHA256 proof signed with a fixed device-identity label',
    'features.identity.detail3': 'TOFU pinning kept on each exit server',
    'features.identity.detail4': 'Reset via support email if you reinstall or migrate device',
    'features.identity.detail5': 'Private key never leaves the device',
    'features.split.heading': 'Split routing',
    'features.split.body':
      'On-device CIDR table for Russian network space (8 585 subnets) keeps RU-bound traffic outside the tunnel; only foreign destinations go through the relay vertex. The result: lower latency for domestic services and no spurious geo-blocks on banking, payments or government portals.',
    'features.split.detail1': '8 585 RU subnets baked into each release',
    'features.split.detail2': 'iOS / macOS: NEPacketTunnelNetworkSettings excludedRoutes',
    'features.split.detail3': 'Android: VpnService.Builder excludeRoute (capped at 1 500 entries)',
    'features.split.detail4': 'Linux gateway: ipset `ru-nets` + iptables MARK',
    'features.split.detail5': 'Toggle in app: full tunnel ↔ split',
    'features.dpi.heading': 'Censorship-resistant transport',
    'features.dpi.body':
      'Vertex does not carry the wire signature of a typical VPN. To passive observers, the connection blends in with normal encrypted web traffic — so it keeps working on networks that filter dedicated VPN protocols. The client tries the preferred path first and silently falls back to alternatives when something on the way refuses to cooperate.',
    'features.dpi.detail1': 'No dedicated VPN port or protocol fingerprint',
    'features.dpi.detail2': 'Looks like ordinary encrypted web traffic at the network layer',
    'features.dpi.detail3': 'Multiple transport paths chosen at connect time',
    'features.dpi.detail4': 'Automatic fallback when the primary path is blocked',
    'features.dpi.detail5': 'Continually re-evaluated against new filtering techniques',

    // Download page
    'download.title': 'Download — Vertex',
    'download.heading': 'Get Vertex',
    'download.lede':
      'Native clients for every major platform. No installer telemetry, no analytics, no auto-update phone-home.',
    'download.detected': 'It looks like you\'re on',
    'download.iosNote': 'iOS 17 and newer. App Store release pending review.',
    'download.macosNote': 'macOS 14 Sonoma and newer, Apple Silicon. Notarized .app bundle inside a zip.',
    'download.androidNote': 'Android 8 (API 26) and newer. Sideload — not on Google Play.',
    'download.linuxNote': 'Native binary planned. Drop us a line if you need an early build.',
    'download.windowsNote': 'Windows 10 / 11 (x64). Signed MSI installer.',
    'download.gatewayNote': 'Turns a small Linux router into a transparent Vertex gateway with split routing. Install script available on request.',
    'download.requestScript': 'Request install script',
    'download.notify': 'Notify me',
    'download.cta.mac': 'Download for Mac',
    'download.cta.android': 'Download APK',
    'download.cta.windows': 'Download installer',
    'download.verify': 'Verify download',
    'download.size': 'Size',
    'download.sha256': 'SHA-256',
    'download.platform.ios': 'iOS',
    'download.platform.macos': 'macOS',
    'download.platform.android': 'Android',
    'download.platform.linux': 'Linux',
    'download.platform.windows': 'Windows',
    'download.platform.gateway': 'Gateway (R3S)',
    'download.comingSoon': 'Coming soon',

    // Doc layout
    'doc.lastUpdated': 'Last updated',
  },
  ru: {
    'nav.features': 'Возможности',
    'nav.download': 'Загрузить',
    'nav.support': 'Поддержка',

    'cta.download': 'Загрузить',
    'cta.learnMore': 'Подробнее',

    'footer.tagline': 'Точка схождения.',
    'footer.product': 'Продукт',
    'footer.legal': 'Документы и поддержка',
    'footer.privacy': 'Политика конфиденциальности',
    'footer.terms': 'Условия использования',
    'footer.support': 'Поддержка',
    'footer.copyright': '© 2026 Vertex',

    'theme.toggle': 'Переключить тему',
    'theme.dark': 'Тёмная',
    'theme.light': 'Светлая',

    'lang.toggle': 'Сменить язык',

    'hero.heading': 'Vertex',
    'hero.tagline': 'Точка схождения.',
    'hero.subtag':
      'VPN, построенный вокруг приватности. Сквозное шифрование, гибкий транспорт.',
    'hero.graph.title': 'Сетевой граф Vertex',
    'hero.graph.desc':
      'Центральный узел соединён рёбрами с узлами-спутниками; по рёбрам идут пакеты.',
    'hero.graph.caption': 'клиент → вершина → выходной узел',

    'a11y.skipToContent': 'Перейти к содержимому',
    'a11y.openMenu': 'Открыть меню',
    'a11y.closeMenu': 'Закрыть меню',

    'numbers.throughput.value': '100+ / 100+',
    'numbers.throughput.unit': 'Мбит/с',
    'numbers.throughput.label': 'скорость по Ethernet, замер',
    'numbers.failover.value': '~100',
    'numbers.failover.unit': 'мс',
    'numbers.failover.label': 'переключение между вершинами',
    'numbers.split.value': '8585',
    'numbers.split.unit': 'подсетей',
    'numbers.split.label': 'идут напрямую, в обход туннеля',

    'home.features.heading': 'Архитектура непрямого пути',
    'home.features.lede':
      'Шесть свойств, которые вытекают из самой архитектуры — а не приклеены сверху.',
    'home.features.e2e.title': 'Сквозное шифрование',
    'home.features.e2e.desc':
      'Сессионный ключ получается из X25519 Diffie-Hellman при каждом подключении. Пакет запечатывается ChaCha20-Poly1305 ещё до того, как покинет устройство — вершине достаётся только шифротекст.',
    'home.features.vertex.title': 'Несколько независимых вершин',
    'home.features.vertex.desc':
      'Несколько независимых вершин, выходные узлы подключены ко всем сразу, автоматическое переключение клиента примерно за 100 мс. Падение одной вершины незаметно.',
    'home.features.exit.title': 'Автоматический выбор выхода',
    'home.features.exit.desc':
      'Клиент пингует каждую вершину и при подключении смотрит на нагрузку выходных узлов — выбирает кратчайший путь. Переключиться вручную — одно касание.',
    'home.features.identity.title': 'Идентичность устройства (TOFU)',
    'home.features.identity.desc':
      'Утечки пароля недостаточно. Выходной узел запоминает ваше устройство при первом подключении; чтобы войти, атакующему нужен ещё файл с ключом идентичности.',
    'home.features.split.title': 'Split routing',
    'home.features.split.desc':
      'Локальная таблица CIDR направляет российские адреса напрямую, остальное — в туннель. 8 585 подсетей, обновляются с каждым релизом.',
    'home.features.dpi.title': 'Транспорт, устойчивый к блокировкам',
    'home.features.dpi.desc':
      'В сети Vertex неотличим от обычного зашифрованного веб-трафика — продолжает работать там, где привычные VPN-протоколы фильтруются.',

    'home.arch.heading': 'Как устроена топология',
    'home.arch.lede':
      'Вершина — точка встречи. Клиент и выходной узел общаются через неё; сама вершина передаёт только непрозрачный шифротекст.',

    'home.how.heading': 'Три шага',
    'home.how.step1.title': 'Установить',
    'home.how.step1.desc': 'Нативные приложения для iOS, macOS, Android и Windows. Сборки для Linux и шлюза — по запросу.',
    'home.how.step2.title': 'Аутентифицироваться',
    'home.how.step2.desc': 'При первом запуске на устройстве создаётся X25519-ключ идентичности. Выходной узел регистрирует его при первом подключении.',
    'home.how.step3.title': 'Подключиться',
    'home.how.step3.desc': 'Переключите тумблер. Клиент сам выберет лучшую вершину и выходной узел, плавно переподключится при смене сети.',

    'home.download.heading': 'Скачайте под свою платформу',
    'home.download.lede': 'Нативно, без лишнего, без телеметрии.',
    'home.download.cta': 'Все варианты загрузки',

    'home.cta.heading': 'Никакой телеметрии. Никакой аналитики.',
    'home.cta.lede':
      'Vertex построен на одном принципе: сеть не должна видеть ваш трафик. И мы тоже не хотим.',
    'home.cta.primary': 'Получить Vertex',
    'home.cta.secondary': 'Политика конфиденциальности',

    'features.title': 'Возможности — Vertex',
    'features.heading': 'Что внутри',
    'features.lede':
      'Vertex — небольшой набор архитектурных решений, применённых последовательно. На этой странице — те же шесть опор, что на главной, но с подробностями по криптографии и настройкам.',
    'features.e2e.heading': 'Сквозное шифрование',
    'features.e2e.body':
      'Каждый пакет между вашим устройством и выходным узлом запечатан ChaCha20-Poly1305 на сессионном ключе, полученном из X25519 Diffie-Hellman при подключении. У вершины нет ключевого материала; даже если она сохранит каждый переданный байт — у неё останется только шифротекст. Сессионный ключ — свой на каждое устройство и каждое подключение, отсюда Perfect Forward Secrecy на уровне сессии.',
    'features.e2e.detail1': 'Эфемерная пара ключей X25519 на каждое подключение',
    'features.e2e.detail2': 'HKDF-SHA256 даёт 256-битный сессионный ключ',
    'features.e2e.detail3': 'ChaCha20-Poly1305 AEAD на каждом пакете',
    'features.e2e.detail4': '12-байтный nonce + 16-байтный auth tag (28 байт накладных)',
    'features.e2e.detail5': 'Никаких постоянных общих секретов между клиентом и выходным узлом',
    'features.vertex.heading': 'Несколько независимых вершин',
    'features.vertex.body':
      'Vertex держит несколько независимых вершин у разных инфраструктурных провайдеров в разных регионах, без репликации и кластеризации между ними. Выходные узлы подключены ко всем вершинам; клиент хранит упорядоченный список и переходит к следующей доступной вершине при сбое. Переключение занимает порядка 100 мс — обычно этого достаточно, чтобы стрим или SSH-сессия не оборвались.',
    'features.vertex.detail1': 'Несколько независимых вершин у разных инфраструктурных провайдеров',
    'features.vertex.detail2': 'Между вершинами нет ни репликации, ни кластеризации — каждая полностью самостоятельна',
    'features.vertex.detail3': 'Список вершин клиент получает автоматически — всегда самый свежий',
    'features.vertex.detail4': 'Выходные узлы подключены ко всем вершинам; ответ идёт обратно по тому же пути',
    'features.vertex.detail5': 'Sticky reconnect: первой пробуется последняя рабочая вершина',
    'features.exit.heading': 'Автоматический выбор выхода',
    'features.exit.body':
      'При подключении клиент измеряет RTT до каждого доступного выходного узла и смотрит на его текущую нагрузку. Через тот, у кого минимальный RTT с поправкой на нагрузку, идёт трафик. Переоценка — раз в 5 минут; ручной выбор в интерфейсе имеет приоритет.',
    'features.exit.detail1': 'Выходные узлы объявляют о себе короткими периодическими heartbeats',
    'features.exit.detail2': 'Оценка балансирует измеренный RTT и текущую нагрузку (RTT доминирует)',
    'features.exit.detail3': 'Переоценка раз в 5 минут',
    'features.exit.detail4': 'Ручной выбор выходного узла отменяет автоматический',
    'features.exit.detail5': 'Несколько боевых выходных узлов в независимых регионах',
    'features.identity.heading': 'Идентичность устройства (TOFU)',
    'features.identity.body':
      'Каждый клиент при первом запуске создаёт пару ключей X25519 и хранит приватную половину на устройстве (Keychain на Apple, EncryptedFile на Android, файл на Linux). Публичная половина уходит выходному узлу при handshake; узел запоминает её при первом подключении (Trust On First Use) и отказывает любому последующему подключению с тем же именем пользователя, но другим ключом идентичности. Только пароля для входа в сеть мало.',
    'features.identity.detail1': 'Пара ключей X25519: 32 байта публичный + 32 байта приватный',
    'features.identity.detail2': 'HMAC-SHA256 как доказательство, с фиксированной меткой идентичности устройства',
    'features.identity.detail3': 'TOFU-пиннинг хранится на каждом выходном узле',
    'features.identity.detail4': 'Сброс — по письму в поддержку, при переустановке или смене устройства',
    'features.identity.detail5': 'Приватный ключ никогда не покидает устройство',
    'features.split.heading': 'Split routing',
    'features.split.body':
      'Локальная таблица CIDR с российским сетевым пространством (8 585 подсетей) держит трафик до RU-адресов вне туннеля; через вершину уходят только зарубежные направления. В итоге: ниже задержка до отечественных сервисов и никаких ложных гео-блокировок на банкинге, платежах и госпорталах.',
    'features.split.detail1': '8 585 российских подсетей зашиты в каждый релиз',
    'features.split.detail2': 'iOS / macOS: NEPacketTunnelNetworkSettings excludedRoutes',
    'features.split.detail3': 'Android: VpnService.Builder excludeRoute (лимит 1 500 записей)',
    'features.split.detail4': 'Linux-шлюз: ipset `ru-nets` + iptables MARK',
    'features.split.detail5': 'Тумблер в приложении: полный туннель ↔ split',
    'features.dpi.heading': 'Транспорт, устойчивый к блокировкам',
    'features.dpi.body':
      'У Vertex нет характерной сетевой сигнатуры привычного VPN. Пассивному наблюдателю соединение видится как обычный зашифрованный веб-трафик — поэтому оно работает там, где специализированные VPN-протоколы фильтруются. Клиент сначала пробует основной путь и тихо уходит на резервный, если что-то по дороге отказывается пропускать пакеты.',
    'features.dpi.detail1': 'Ни выделенного VPN-порта, ни протокольной сигнатуры',
    'features.dpi.detail2': 'На сетевом уровне неотличимо от обычного зашифрованного веб-трафика',
    'features.dpi.detail3': 'Несколько транспортных путей, выбор — при подключении',
    'features.dpi.detail4': 'Автоматический переход на резервный путь, если основной заблокирован',
    'features.dpi.detail5': 'Регулярно пересматривается под новые методы фильтрации',

    'download.title': 'Загрузить — Vertex',
    'download.heading': 'Скачать Vertex',
    'download.lede':
      'Нативные клиенты под все основные платформы. Без установочной телеметрии, без аналитики, без скрытых обращений к серверам обновлений.',
    'download.detected': 'Похоже, у вас',
    'download.iosNote': 'iOS 17 и новее. Релиз ждёт прохождения review в App Store.',
    'download.macosNote': 'macOS 14 Sonoma и новее, Apple Silicon. Нотарифицированный .app в zip-архиве.',
    'download.androidNote': 'Android 8 (API 26) и новее. Sideload — без Google Play.',
    'download.linuxNote': 'Нативный бинарь в планах. Если нужна ранняя сборка — напишите нам.',
    'download.windowsNote': 'Windows 10 / 11 (x64). Подписанный MSI-установщик.',
    'download.gatewayNote': 'Превращает компактный Linux-роутер в прозрачный Vertex-шлюз со split routing. Скрипт установки — по запросу.',
    'download.requestScript': 'Запросить скрипт установки',
    'download.notify': 'Сообщить о выходе',
    'download.cta.mac': 'Скачать для Mac',
    'download.cta.android': 'Скачать APK',
    'download.cta.windows': 'Скачать установщик',
    'download.verify': 'Проверить загрузку',
    'download.size': 'Размер',
    'download.sha256': 'SHA-256',
    'download.platform.ios': 'iOS',
    'download.platform.macos': 'macOS',
    'download.platform.android': 'Android',
    'download.platform.linux': 'Linux',
    'download.platform.windows': 'Windows',
    'download.platform.gateway': 'Шлюз (R3S)',
    'download.comingSoon': 'В разработке',

    // Doc layout
    'doc.lastUpdated': 'Последнее обновление',
  },
} as const;

export type UIKey = keyof (typeof ui)['en'];

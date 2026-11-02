import Foundation

/// Настройки «Профили и подписки» (доска 12) — как `SubscriptionSettings` Android: обновлять каждые 12 часов, только через
/// VPN, сообщать, если выбранный сервер пропал из подписки или изменился.
public struct SubscriptionSettings: Equatable, Sendable {
    public var refresh: RefreshInterval
    public var onlyViaVpn: Bool
    public var notifySelected: Bool

    public init(refresh: RefreshInterval = .every12Hours, onlyViaVpn: Bool = true, notifySelected: Bool = true) {
        self.refresh = refresh
        self.onlyViaVpn = onlyViaVpn
        self.notifySelected = notifySelected
    }
}

/// Настройки приложения, общие с расширением туннеля, — как `SettingsRepository` Android: выбранный сервер (меняется только
/// нажатием пользователя), туннель — DNS, IPv6 через сервер, локальная сеть, — блок при обрыве, подписки, уведомления,
/// переподключение, подключение при запуске, «Подключение и ядро» и «Маршрутизация».
public struct AppSettings: Equatable, Sendable {
    public var selectedServerId: String?
    public var tunnel: TunnelSettings
    /// Блок при обрыве — системный «весь трафик только через VPN» (`includeAllNetworks`); по умолчанию включен, как решено
    /// для защиты.
    public var killSwitch: Bool
    public var subscriptions: SubscriptionSettings
    /// Какие уведомления показывать; по умолчанию «Только важные».
    public var notifications: NotificationLevel
    /// Что делать при обрыве связи с сервером; по умолчанию «Экспоненц. до 60 с».
    public var reconnect: ReconnectPolicy
    /// «Постоянная защита» — подключение по требованию iOS (On Demand): после перезагрузки, смены сети и сбоя расширения
    /// iOS включает защиту сама, пока ее не выключат кнопкой в приложении. По умолчанию выключена, как «Постоянная VPN»
    /// Android.
    public var alwaysOn: Bool
    /// «Подключаться при запуске» (доска 04, `ConnectOnLaunch` десктопа): открыли приложение, а защита выключена — подключиться
    /// к выбранному серверу. Расширению туннеля не нужна; по умолчанию выключена, как на десктопе.
    public var connectOnLaunch: Bool
    /// «Подключение и ядро» (доска 06): журнал ядра, MTU, определение домена, отпечаток TLS, Mux, фрагментация, таймаут.
    public var connection: ConnectionSettings
    /// «Маршрутизация» (доска 13): весь трафик или раздельный режим и правила для сайтов.
    public var routing: RoutingSettings

    public init(
        selectedServerId: String? = nil, tunnel: TunnelSettings = TunnelSettings(), killSwitch: Bool = true,
        subscriptions: SubscriptionSettings = SubscriptionSettings(), notifications: NotificationLevel = .default, reconnect: ReconnectPolicy = .backoff60Seconds,
        alwaysOn: Bool = false, connectOnLaunch: Bool = false, connection: ConnectionSettings = ConnectionSettings(), routing: RoutingSettings = RoutingSettings()
    ) {
        self.selectedServerId = selectedServerId
        self.tunnel = tunnel
        self.killSwitch = killSwitch
        self.subscriptions = subscriptions
        self.notifications = notifications
        self.reconnect = reconnect
        self.alwaysOn = alwaysOn
        self.connectOnLaunch = connectOnLaunch
        self.connection = connection
        self.routing = routing
    }

    /// Что получают ядро и туннель: DNS, IPv6 и локальная сеть («Защита и утечки»), подключение («Подключение и ядро») и
    /// правила для сайтов («Маршрутизация»).
    public func core() -> CoreSettings {
        tunnel.core(connection, routing)
    }

    /// Нужно ли переподключиться, чтобы `next` вошли в работу, — только ради того, что получают ядро и туннель. Таймаут
    /// подключения и переподключение расширение читает само на каждой пробе.
    public func needsReconnect(_ next: AppSettings) -> Bool {
        core() != next.core()
    }
}

/// Файл — плоский словарь с теми же именами, что ключи DataStore Android: его читают приложение и расширение туннеля разных
/// версий. Значения нет, оно другого типа или неизвестно (из будущей версии) — значение по умолчанию, остальные остаются.
extension AppSettings: Codable {
    private enum Keys: String, CodingKey {
        case selectedServer = "selected_server"
        case dns = "tunnel_dns"
        case customDns = "tunnel_custom_dns"
        case ipv6 = "tunnel_ipv6"
        case allowLan = "tunnel_allow_lan"
        case killSwitch = "kill_switch"
        case refresh = "subs_refresh"
        case onlyViaVpn = "subs_only_vpn"
        case notifySelected = "subs_notify_selected"
        case notifications = "notify_level"
        case reconnect = "reconnect_policy"
        case alwaysOn = "always_on"
        case connectOnLaunch = "connect_on_launch"
        case coreLog = "core_log"
        case mtu = "tunnel_mtu"
        case sniffing = "tunnel_sniffing"
        case fingerprint = "conn_fingerprint"
        case mux = "conn_mux"
        case tlsFragment = "conn_tls_fragment"
        case connectTimeout = "conn_timeout"
        case routingMode = "routing_mode"
        /// Правила — текстом JSON `[{"pattern": …, "action": …}]`, как у Android: словарь остается плоским.
        case routingRules = "routing_rules"
    }

    public init(from decoder: any Decoder) throws {
        let values = try decoder.container(keyedBy: Keys.self)
        let defaults = AppSettings()
        func read<T: Decodable>(_ key: Keys) -> T? {
            try? values.decodeIfPresent(T.self, forKey: key)
        }

        let dns: String? = read(.dns)
        let refresh: String? = read(.refresh)
        let coreLog: String? = read(.coreLog)
        let mtu: Int? = read(.mtu)
        let fingerprint: String? = read(.fingerprint)
        let timeout: Int? = read(.connectTimeout)
        let mode: String? = read(.routingMode)
        // Правило — объект «шаблон и действие»; неполное, с неизвестным действием или шаблоном, которого ядро телефона не
        // примет, пропускается, остальные остаются. Текст не JSON или список не из строк — правил нет.
        let rulesText: String? = read(.routingRules)
        let records = rulesText.flatMap { try? JSONDecoder().decode([[String: String]].self, from: Data($0.utf8)) }
        let rules = (records ?? []).compactMap { record -> SiteRule? in
            guard let pattern = record["pattern"], let action = record["action"].flatMap(RuleAction.init(rawValue:)) else {
                return nil
            }

            return SiteRule(pattern: pattern, action: action)
        }
        let sniffing: Bool? = read(.sniffing)
        let mux: Bool? = read(.mux)
        let tlsFragment: Bool? = read(.tlsFragment)
        let connection = ConnectionSettings(
            coreLog: coreLog.flatMap(CoreLogLevel.init(rawValue:)) ?? defaults.connection.coreLog,
            mtu: mtu.flatMap { ConnectionSettings.validMtu($0) ? $0 : nil },
            sniffing: sniffing ?? defaults.connection.sniffing,
            fingerprint: fingerprint.flatMap(TlsFingerprint.init(rawValue:)) ?? defaults.connection.fingerprint,
            mux: mux ?? defaults.connection.mux, tlsFragment: tlsFragment ?? defaults.connection.tlsFragment,
            connectTimeout: timeout.flatMap { ConnectionSettings.timeoutRange.contains($0) ? $0 : nil } ?? defaults.connection.connectTimeout
        )
        let routing = RoutingSettings(mode: mode.flatMap(RoutingMode.init(rawValue:)) ?? defaults.routing.mode, rules: rules).normalized
        self.init(
            selectedServerId: read(.selectedServer),
            tunnel: TunnelSettings(
                dns: dns.flatMap(DnsChoice.init(rawValue:)) ?? defaults.tunnel.dns, customDns: read(.customDns),
                ipv6: read(.ipv6) ?? defaults.tunnel.ipv6, allowLan: read(.allowLan) ?? defaults.tunnel.allowLan
            ),
            killSwitch: read(.killSwitch) ?? defaults.killSwitch,
            subscriptions: SubscriptionSettings(
                refresh: refresh.flatMap(RefreshInterval.init(rawValue:)) ?? defaults.subscriptions.refresh,
                onlyViaVpn: read(.onlyViaVpn) ?? defaults.subscriptions.onlyViaVpn,
                notifySelected: read(.notifySelected) ?? defaults.subscriptions.notifySelected
            ),
            notifications: NotificationLevel.read(read(.notifications)), reconnect: ReconnectPolicy.read(read(.reconnect)),
            alwaysOn: read(.alwaysOn) ?? defaults.alwaysOn, connectOnLaunch: read(.connectOnLaunch) ?? defaults.connectOnLaunch, connection: connection,
            routing: routing
        )
    }

    public func encode(to encoder: any Encoder) throws {
        var values = encoder.container(keyedBy: Keys.self)
        try values.encodeIfPresent(selectedServerId, forKey: .selectedServer)
        try values.encode(tunnel.dns.rawValue, forKey: .dns)
        try values.encodeIfPresent(tunnel.customDns, forKey: .customDns)
        try values.encode(tunnel.ipv6, forKey: .ipv6)
        try values.encode(tunnel.allowLan, forKey: .allowLan)
        try values.encode(killSwitch, forKey: .killSwitch)
        try values.encode(subscriptions.refresh.rawValue, forKey: .refresh)
        try values.encode(subscriptions.onlyViaVpn, forKey: .onlyViaVpn)
        try values.encode(subscriptions.notifySelected, forKey: .notifySelected)
        try values.encode(notifications.rawValue, forKey: .notifications)
        try values.encode(reconnect.rawValue, forKey: .reconnect)
        try values.encode(alwaysOn, forKey: .alwaysOn)
        try values.encode(connectOnLaunch, forKey: .connectOnLaunch)
        try values.encode(connection.coreLog.rawValue, forKey: .coreLog)
        try values.encodeIfPresent(connection.mtu, forKey: .mtu)
        try values.encode(connection.sniffing, forKey: .sniffing)
        try values.encode(connection.fingerprint.rawValue, forKey: .fingerprint)
        try values.encode(connection.mux, forKey: .mux)
        try values.encode(connection.tlsFragment, forKey: .tlsFragment)
        try values.encode(connection.connectTimeout, forKey: .connectTimeout)
        try values.encode(routing.mode.rawValue, forKey: .routingMode)
        let records = routing.rules.map { JsonText.Value.object([("pattern", .string($0.pattern)), ("action", .string($0.action.rawValue))]) }
        try values.encode(JsonText.write(.array(records)), forKey: .routingRules)
    }
}

/// Настройки в файле общей папки App Group: приложение их меняет, расширение туннеля читает при каждом подъеме. Файла нет
/// или он не разбирается — настройки по умолчанию; файл не прочитать (данные под защитой до первой разблокировки) — ошибка,
/// а не «по умолчанию»: запись поверх стерла бы выбор пользователя. Файл читается заново на каждое действие, запись — целиком.
public actor SettingsStore {
    private let file: any StateFile

    public init(file: any StateFile) {
        self.file = file
    }

    public func load() throws -> AppSettings {
        try loadNow()
    }

    /// То же, что `load`, без очереди актора — для синхронного кода расширения туннеля; только чтение.
    public nonisolated func loadNow() throws -> AppSettings {
        guard let data = try file.read() else {
            return AppSettings()
        }

        return (try? JSONDecoder().decode(AppSettings.self, from: data)) ?? AppSettings()
    }

    public func select(_ id: String?) throws {
        try change { $0.selectedServerId = id }
    }

    /// Свой DNS — без пробелов по краям; пустой не хранится, как у Android.
    public func setTunnel(_ tunnel: TunnelSettings) throws {
        var tunnel = tunnel
        let custom = tunnel.customDns?.trimmingCharacters(in: .whitespacesAndNewlines)
        tunnel.customDns = custom?.isEmpty == false ? custom : nil
        try change { $0.tunnel = tunnel }
    }

    public func setKillSwitch(_ on: Bool) throws {
        try change { $0.killSwitch = on }
    }

    public func setAlwaysOn(_ on: Bool) throws {
        try change { $0.alwaysOn = on }
    }

    public func setConnectOnLaunch(_ on: Bool) throws {
        try change { $0.connectOnLaunch = on }
    }

    public func setSubscriptions(_ subscriptions: SubscriptionSettings) throws {
        try change { $0.subscriptions = subscriptions }
    }

    public func setNotifications(_ level: NotificationLevel) throws {
        try change { $0.notifications = level }
    }

    public func setReconnect(_ policy: ReconnectPolicy) throws {
        try change { $0.reconnect = policy }
    }

    /// Свой MTU вне 1280–1500 и неверный таймаут не хранятся — вместо них «Авто» и таймаут по умолчанию.
    public func setConnection(_ connection: ConnectionSettings) throws {
        var connection = connection
        if let mtu = connection.mtu, !ConnectionSettings.validMtu(mtu) {
            connection.mtu = nil
        }

        if !ConnectionSettings.timeoutRange.contains(connection.connectTimeout) {
            connection.connectTimeout = ConnectionSettings.defaultTimeout
        }

        try change { $0.connection = connection }
    }

    /// Шаблоны, которых ядро телефона не примет, не хранятся; правило для того же сайта — одно (`RoutingSettings.normalized`).
    public func setRouting(_ routing: RoutingSettings) throws {
        try change { $0.routing = routing.normalized }
    }

    private func change(_ body: (inout AppSettings) -> Void) throws {
        var settings = try load()
        body(&settings)
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        try file.write(encoder.encode(settings))
    }
}

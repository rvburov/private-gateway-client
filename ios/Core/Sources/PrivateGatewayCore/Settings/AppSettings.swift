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
/// нажатием пользователя), туннель — DNS, IPv6 через сервер, локальная сеть, — блок при обрыве, подписки, уведомления и
/// переподключение.
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

    public init(
        selectedServerId: String? = nil, tunnel: TunnelSettings = TunnelSettings(), killSwitch: Bool = true,
        subscriptions: SubscriptionSettings = SubscriptionSettings(), notifications: NotificationLevel = .default, reconnect: ReconnectPolicy = .backoff60Seconds,
        alwaysOn: Bool = false
    ) {
        self.selectedServerId = selectedServerId
        self.tunnel = tunnel
        self.killSwitch = killSwitch
        self.subscriptions = subscriptions
        self.notifications = notifications
        self.reconnect = reconnect
        self.alwaysOn = alwaysOn
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
    }

    public init(from decoder: any Decoder) throws {
        let values = try decoder.container(keyedBy: Keys.self)
        let defaults = AppSettings()
        func read<T: Decodable>(_ key: Keys) -> T? {
            try? values.decodeIfPresent(T.self, forKey: key)
        }

        let dns: String? = read(.dns)
        let refresh: String? = read(.refresh)
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
            alwaysOn: read(.alwaysOn) ?? defaults.alwaysOn
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

    public func setSubscriptions(_ subscriptions: SubscriptionSettings) throws {
        try change { $0.subscriptions = subscriptions }
    }

    public func setNotifications(_ level: NotificationLevel) throws {
        try change { $0.notifications = level }
    }

    public func setReconnect(_ policy: ReconnectPolicy) throws {
        try change { $0.reconnect = policy }
    }

    private func change(_ body: (inout AppSettings) -> Void) throws {
        var settings = try load()
        body(&settings)
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        try file.write(encoder.encode(settings))
    }
}

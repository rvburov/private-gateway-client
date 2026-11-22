import Foundation

/// Состояние туннеля — его меняет только расширение туннеля; как `TunnelState` Android и `ConnectionState` десктопа, без
/// «Блокировки».
public enum TunnelState: String, CaseIterable, Codable, Sendable {
    /// VPN выключен: трафик идет напрямую.
    case off

    /// Туннель поднимается.
    case connecting

    /// Туннель работает.
    case connected

    /// Связь с сервером пропала, расширение пробует снова; маршруты туннеля остаются — трафик ждет, а не идет мимо.
    case reconnecting

    /// Туннель закрывается.
    case stopping

    /// Защита не включилась или остановилась: код — в `TunnelStatus.error`.
    case error
}

/// Что показывают окно и виджет: состояние, сервер, с какого момента в сети, код ошибки (PGC-…, тексты — error.<код>) и
/// номер попытки переподключения (1, 2, … в `reconnecting`, вне него 0 — как `Attempt` десктопа).
public struct TunnelStatus: Equatable, Codable, Sendable {
    public var state: TunnelState
    public var serverId: String?
    public var since: Date?
    public var error: String?
    public var attempt: Int
    /// Сети под туннелем нет (`NWPathMonitor` расширения) — «Нет сети», как у десктопа и Android: попытки ждут сеть.
    public var offline: Bool
    /// Включен только местный фильтр: удаленного VPN нет, реальный IP открыт.
    public var localSiteFilter: Bool

    public init(
        state: TunnelState = .off, serverId: String? = nil, since: Date? = nil, error: String? = nil, attempt: Int = 0,
        offline: Bool = false, localSiteFilter: Bool = false
    ) {
        self.state = state
        self.serverId = serverId
        self.since = since
        self.error = error
        self.attempt = attempt
        self.offline = offline
        self.localSiteFilter = localSiteFilter
    }

    private enum CodingKeys: String, CodingKey { case state, serverId, since, error, attempt, offline, localSiteFilter }

    /// Файл прежней версии без `offline` читается как «сеть есть».
    public init(from decoder: any Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        state = try values.decode(TunnelState.self, forKey: .state)
        serverId = try values.decodeIfPresent(String.self, forKey: .serverId)
        since = try values.decodeIfPresent(Date.self, forKey: .since)
        error = try values.decodeIfPresent(String.self, forKey: .error)
        attempt = try values.decodeIfPresent(Int.self, forKey: .attempt) ?? 0
        offline = try values.decodeIfPresent(Bool.self, forKey: .offline) ?? false
        localSiteFilter = try values.decodeIfPresent(Bool.self, forKey: .localSiteFilter) ?? false
    }
}

/// Состояние туннеля, которое расширение пишет в общую папку App Group, — приложению: состояние в системе (`NEVPNStatus`)
/// приложение видит и само, а код ошибки, сервер в работе, время подключения и номер попытки — только отсюда. Файла нет, он
/// не читается или состояние из будущей версии — nil: приложение показывает то, что говорит система.
public struct TunnelStatusFile: Sendable {
    private let file: any StateFile

    public init(file: any StateFile) {
        self.file = file
    }

    public func read() -> TunnelStatus? {
        guard let data = try? file.read() else {
            return nil
        }

        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .millisecondsSince1970
        return try? decoder.decode(TunnelStatus.self, from: data)
    }

    public func write(_ status: TunnelStatus) throws {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .millisecondsSince1970
        encoder.outputFormatting = [.sortedKeys]
        try file.write(encoder.encode(status))
    }
}

/// Коды ошибок туннеля — те же, что у десктопа и Android (docs/architecture/errors.md).
public enum TunnelErrors {
    /// Ядро не запустилось.
    public static let coreFailed = "PGC-SVC-002"

    /// Запрос не выполнить: сервер не выбран или удален из списка.
    public static let noServer = "PGC-SVC-003"

    /// Ядро не умеет работать с этим сервером.
    public static let unsupported = "PGC-SVC-005"

    /// Сервер не отвечает: попытки переподключения кончились.
    public static let serverNotResponding = "PGC-NET-001"

    /// Сети нет: попытки переподключения кончились, пока сети не было (как у десктопа и Android).
    public static let networkUnavailable = "PGC-NET-003"

    /// iOS не дала поднять VPN: VPN другой программы занял место или разрешение отозвано.
    public static let otherVpn = "PGC-NET-010"

    /// Нет доступа: вход в аккаунт сайта не выполнен, отозван или истек (решение 619).
    public static let noAccess = "PGC-ACC-001"
}

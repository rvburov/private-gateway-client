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

    public init(state: TunnelState = .off, serverId: String? = nil, since: Date? = nil, error: String? = nil, attempt: Int = 0) {
        self.state = state
        self.serverId = serverId
        self.since = since
        self.error = error
        self.attempt = attempt
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

    /// iOS не дала поднять VPN: VPN другой программы занял место или разрешение отозвано.
    public static let otherVpn = "PGC-NET-010"
}

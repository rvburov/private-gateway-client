import Foundation

/// Почему у сервера нет задержки — как `LatencyFailure` десктопа и Android.
public enum LatencyFailure: String, CaseIterable, Sendable {
    /// Ядро Xray за этот сервер не возьмется (Hysteria2, TUIC, нет ключа…) — замер не делался.
    case unsupported

    /// Через сервер не пришел ответ за отведенное время.
    case noResponse

    /// Ядро не запустилось или отказало целиком — проверка не состоялась.
    case coreFailed
}

/// Последний замер задержки через сервер: миллисекунды или причина, почему их нет. Сервер, который ни разу не мерили, —
/// не `Latency`, а nil у `ServerItem.latency`.
public struct Latency: Equatable, Hashable, Sendable {
    public let ms: Int?
    public let failure: LatencyFailure?

    public init(ms: Int?, failure: LatencyFailure?) {
        self.ms = ms
        self.failure = failure
    }

    /// Ответ пришел; меньше миллисекунды — 1, чтобы не показывать «0 мс».
    public static func of(_ ms: Int) -> Latency {
        Latency(ms: max(1, ms), failure: nil)
    }

    public static func failed(_ failure: LatencyFailure) -> Latency {
        Latency(ms: nil, failure: failure)
    }
}

/// Как показать задержку: быстрый — зеленым, медленный — серым, нет ответа — «нет» оранжевым, не мерили — «—».
public enum LatencyKind: Sendable {
    case good
    case slow
    case noAnswer
    case never

    /// Задержка больше этой — медленно (у десктопа `ServersPageViewModel.SlowLatencyMs`).
    public static let slowMs = 120

    public static func of(_ latency: Latency?) -> LatencyKind {
        guard let latency else {
            return .never
        }

        guard let ms = latency.ms else {
            return .noAnswer
        }

        return ms > slowMs ? .slow : .good
    }
}

/// Сервер для списка, карточки и главной — как `ServerItem` Android: без адреса и ключа, их знает только запечатанная
/// ссылка в Связке ключей, поэтому ни поиск, ни подписи списка адрес не видят.
public struct ServerItem: Equatable, Hashable, Sendable {
    public var id: String
    /// Название из ссылки или подписки; обновление подписки его меняет.
    public var linkName: String
    public var countryCode: String
    public var `protocol`: ProxyProtocol
    public var security: TransportSecurity
    public var transport: TransportKind
    public var port: Int
    /// Подписка, из которой пришел сервер; nil — добавлен ссылкой («Мои серверы»).
    public var subscriptionId: String?
    public var favorite: Bool
    /// Последний замер; nil — ни разу не мерили.
    public var latency: Latency?
    /// Когда выбирали последний раз; nil — не выбирали.
    public var lastUsedAt: Date?
    /// Когда пропал из подписки; nil — на месте. Пропавший остается, пока выбран.
    public var missingSince: Date?
    /// Свое название пользователя; переживает обновления подписки.
    public var customName: String?

    public init(
        id: String, linkName: String, countryCode: String, protocol: ProxyProtocol, security: TransportSecurity, transport: TransportKind,
        port: Int, subscriptionId: String? = nil, favorite: Bool = false, latency: Latency? = nil, lastUsedAt: Date? = nil,
        missingSince: Date? = nil, customName: String? = nil
    ) {
        self.id = id
        self.linkName = linkName
        self.countryCode = countryCode
        self.protocol = `protocol`
        self.security = security
        self.transport = transport
        self.port = port
        self.subscriptionId = subscriptionId
        self.favorite = favorite
        self.latency = latency
        self.lastUsedAt = lastUsedAt
        self.missingSince = missingSince
        self.customName = customName
    }

    /// Название в списке, карточке и на главной: свое, если пользователь переименовал.
    public var displayName: String { customName ?? linkName }

    /// «VLESS · REALITY».
    public var summary: String { ServerFacts.summary(self.protocol, security, transport) }

    public var encrypted: Bool { ServerFacts.isEncrypted(self.protocol, security) }
}

/// Свое название сервера — правило карточки десктопа и Android.
public enum ServerNames {
    /// Длина своего названия, без пробелов по краям.
    public static let maxLength = 64

    /// Название к сохранению: без пробелов по краям; пустое или длиннее `maxLength` — nil, кнопка «Сохранить» гаснет.
    public static func validate(_ name: String) -> String? {
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        return (1...maxLength).contains(trimmed.count) ? trimmed : nil
    }
}

/// Как узнать тот же сервер — правила десктопа и Android: дубль — тот же адрес, транспорт, защита и данные доступа под
/// любым названием; при обновлении подписки свой сервер узнается сначала так же, потом по выходу — протокол, адрес, порт и
/// данные доступа. Ключи — только для сравнения в памяти, в них секреты: не писать в журнал и на диск.
public enum ServerKeys {
    /// Куда подключаться: протокол, адрес, транспорт и защита — без секретов.
    public static func endpoint(_ server: ServerDefinition) -> String {
        [
            server.protocol.rawValue,
            server.host.lowercased(),
            String(server.port),
            server.transport.kind.rawValue,
            server.transport.path ?? "",
            server.transport.host ?? "",
            server.transport.serviceName ?? "",
            server.security.kind.rawValue,
            server.security.sni ?? "",
            server.security.realityPublicKey ?? "",
        ].joined(separator: "|")
    }

    /// Дубль при импорте и точное совпадение при обновлении подписки.
    public static func key(_ server: ServerDefinition) -> String {
        endpoint(server) + "|" + credentials(server.credentials)
    }

    /// Выход сервера: протокол, адрес, порт и данные доступа — без транспорта и защиты.
    public static func exit(_ server: ServerDefinition) -> String {
        [server.protocol.rawValue, server.host.lowercased(), String(server.port), credentials(server.credentials)].joined(separator: "|")
    }

    private static func credentials(_ credentials: ServerCredentials) -> String {
        [credentials.uuid, credentials.password, credentials.obfsPassword, credentials.privateKey, credentials.presharedKey]
            .map { $0 ?? "" }
            .joined(separator: "|")
    }
}

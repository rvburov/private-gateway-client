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

/// Почему через сервер не пришел ответ — `LatencyDiagnostic` десктопа и `LatencyReason` Android: по тексту ошибки ядра.
/// Сам текст (в нем бывают адреса) не хранится — только вид.
public enum LatencyReason: String, CaseIterable, Sendable {
    case timeout = "Timeout"
    case dns = "Dns"
    case tls = "Tls"
    case connection = "Connection"

    /// Вид по тексту ошибки libXray; непонятный — nil («нет ответа»). Таймаут ядра без текста — `timeout`.
    public static func of(_ error: String?, timedOut: Bool = false) -> LatencyReason? {
        let text = (error ?? "").lowercased()
        func has(_ part: String) -> Bool { text.contains(part) }
        if has("no such host") || has("lookup ") || has("server misbehaving") || has("dns") {
            return .dns
        }

        if has("tls") || has("x509") || has("handshake") || has("reality") || has("certificate") {
            return .tls
        }

        if has("deadline exceeded") || has("timeout") || has("timed out") {
            return .timeout
        }

        if has("refused") || has("reset") || has("unreachable") || has("eof") || has("broken pipe") || has("no route") {
            return .connection
        }

        return timedOut ? .timeout : nil
    }
}

/// Последний замер задержки через сервер: миллисекунды или причина, почему их нет. Сервер, который ни разу не мерили, —
/// не `Latency`, а nil у `ServerItem.latency`.
public struct Latency: Equatable, Hashable, Sendable {
    public let ms: Int?
    public let failure: LatencyFailure?
    /// Почему нет ответа (`noResponse`); nil — неизвестно.
    public let reason: LatencyReason?

    public init(ms: Int?, failure: LatencyFailure?, reason: LatencyReason? = nil) {
        self.ms = ms
        self.failure = failure
        self.reason = reason
    }

    /// Запись отказа для хранилища: «noResponse» или «noResponse/Timeout» — прежние записи без «/» читаются как были.
    public var stored: String? {
        failure.map { $0.rawValue + (reason.map { "/" + $0.rawValue } ?? "") }
    }

    /// Отказ из записи хранилища (`stored`); неизвестный вид — «нет ответа».
    public static func read(_ stored: String) -> Latency {
        let parts = stored.split(separator: "/", maxSplits: 1).map(String.init)
        return .failed(LatencyFailure(rawValue: parts.first ?? "") ?? .noResponse, reason: parts.count > 1 ? LatencyReason(rawValue: parts[1]) : nil)
    }

    /// Ответ пришел; меньше миллисекунды — 1, чтобы не показывать «0 мс».
    public static func of(_ ms: Int) -> Latency {
        Latency(ms: max(1, ms), failure: nil)
    }

    public static func failed(_ failure: LatencyFailure, reason: LatencyReason? = nil) -> Latency {
        Latency(ms: nil, failure: failure, reason: reason)
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

    /// Подпись с отступами вокруг эмодзи; переименование и экспорт используют исходный `displayName`.
    public var title: String { ServerNames.forDisplay(displayName) }

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

    /// Только показ: Character сохраняет флаги, модификаторы кожи и ZWJ как целые текстовые элементы.
    public static func forDisplay(_ name: String) -> String {
        var result = ""
        var previous = Kind.other
        for element in name {
            let scalars = element.unicodeScalars
            guard let first = scalars.first else { continue }
            let emoji = (0x1F000...0x1FAFF).contains(first.value) || (0x2600...0x27BF).contains(first.value)
                || (0x231A...0x231B).contains(first.value) || (0x23E9...0x23EC).contains(first.value)
                || first.value == 0x23F0 || first.value == 0x23F3 || (0x25FD...0x25FE).contains(first.value)
                || first.value == 0x2B50 || first.value == 0x2B55
                || scalars.contains { $0.value == 0xFE0F || $0.value == 0x20E3 }
            let current = emoji ? Kind.emoji : CharacterSet.alphanumerics.contains(first) ? Kind.word : Kind.other
            if (previous == .emoji && (current == .emoji || current == .word)) || (previous == .word && current == .emoji) {
                result.append(" ")
            }
            result.append(element)
            previous = current
        }
        return result
    }

    private enum Kind { case other, word, emoji }
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

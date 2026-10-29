import Foundation

/// Адрес подписки — секрет (токен доступа обычно в пути или в запросе): для подписи только «https://example.com/sub/••••»,
/// как `SubscriptionAddress` десктопа и Android; целиком он хранится запечатанным.
public enum SubscriptionAddress {
    public static let hidden = "••••"

    /// Сегмент пути длиннее — скорее токен, чем слово вроде «sub» или «clash».
    private static let wordLength = 12

    /// Адрес подписки: http или https с сервером; иначе nil.
    public static func parse(_ text: String) -> URLComponents? {
        guard let url = URLComponents(string: text.trimmingCharacters(in: .whitespacesAndNewlines)),
              let scheme = url.scheme?.lowercased(), scheme == "https" || scheme == "http", let host = url.host, !host.isEmpty
        else {
            return nil
        }

        return url
    }

    /// Подписка без шифрования: ключи серверов шли открытым текстом (import.insecure).
    public static func insecure(_ url: URLComponents) -> Bool {
        url.scheme?.lowercased() == "http"
    }

    /// Схема, сервер и первый сегмент пути, если он похож на слово; остальное — «••••». Имя и пароль перед сервером, остаток
    /// пути, запрос и якорь не показываются никогда.
    public static func mask(_ url: URLComponents) -> String {
        let scheme = url.scheme?.lowercased() ?? ""
        let defaultPort = scheme == "https" ? 443 : 80
        let port = url.port.flatMap { $0 == defaultPort ? nil : ":\($0)" } ?? ""
        let head = scheme + "://" + (url.host ?? "") + port
        let segments = url.percentEncodedPath.split(separator: "/").map(String.init)
        let tail = segments.count > 1 || !(url.percentEncodedQuery ?? "").isEmpty || !(url.percentEncodedFragment ?? "").isEmpty
        guard let first = segments.first else {
            return tail ? "\(head)/\(hidden)" : head
        }

        return word(first) ? head + "/" + first + (tail ? "/\(hidden)" : "") : "\(head)/\(hidden)"
    }

    private static func word(_ segment: String) -> Bool {
        segment.count <= wordLength && segment.unicodeScalars.allSatisfy { ("a"..."z").contains($0) || ("A"..."Z").contains($0) || $0 == "-" || $0 == "_" }
    }
}

/// Название подписки из заголовка ответа Profile-Title — как у десктопа и Android: «base64:…» — раскодировать; иначе только
/// печатный ASCII (кириллица без base64 приходит испорченной); управляющие символы — нет названия; длина — не больше `max`.
public enum SubscriptionTitle {
    public static let max = 100

    public static func from(_ header: String?) -> String? {
        guard var value = header?.trimmingCharacters(in: .whitespacesAndNewlines), !value.isEmpty else {
            return nil
        }

        if value.lowercased().hasPrefix("base64:") {
            guard let decoded = LinkParser.decodeBase64(String(value.dropFirst("base64:".count))) else {
                return nil
            }

            value = decoded.trimmingCharacters(in: .whitespacesAndNewlines)
        } else if value.unicodeScalars.contains(where: { $0.value > 0x7E }) {
            return nil
        }

        if value.isEmpty || value.unicodeScalars.contains(where: { $0.value < 0x20 || (0x7F...0x9F).contains($0.value) }) {
            return nil
        }

        guard value.count > max else {
            return value
        }

        var cut = String(value.prefix(max))
        while cut.last?.isWhitespace == true {
            cut.removeLast()
        }

        return cut
    }
}

/// Почему подписка не загрузилась — те же причины, что у десктопа и Android; тексты — import.download.*.
public enum DownloadFailure: String, CaseIterable, Sendable {
    case network = "Network"
    case nameResolution = "NameResolution"
    case connection = "Connection"
    case tls = "Tls"
    case timeout = "Timeout"

    /// Сервер ответил не 2xx; код ответа хранится рядом.
    case status = "Status"
    case tooLarge = "TooLarge"

    /// Загрузилось, но ни одного сервера не разобрано — прошлый список оставлен.
    case noServers = "NoServers"

    /// Адрес подписки не открылся: запечатанный адрес не расшифровать (запись Связки ключей пропала).
    case noAddress = "NoAddress"
    /// Ядро туннеля не дало защищенный вход: напрямую при «только через VPN» не загружаем.
    case protectedPathUnavailable = "ProtectedPathUnavailable"
    /// На iOS 16 нет API, гарантирующего отсутствие прямого повтора/обхода локального адреса URLSession.
    case protectedTransportUnsupported = "ProtectedTransportUnsupported"
    /// Локальный или неоднозначный URL может обойти явный прокси на Apple; защищенная загрузка отказывает до запроса.
    case protectedAddressUnsupported = "ProtectedAddressUnsupported"

    /// Причина, сохраненная у подписки: nil — неудачи не было; неизвестное имя (из будущей версии) — сеть, как у десктопа.
    public static func parse(_ name: String?) -> DownloadFailure? {
        name.map { DownloadFailure(rawValue: $0) ?? .network }
    }
}

/// Пределы загрузки подписки — как у десктопа и Android: 20 секунд и 5 МБ после распаковки.
public enum DownloadLimits {
    public static let timeout: TimeInterval = 20
    public static let maxBytes = 5 * 1024 * 1024
}

/// Почему подписка не загрузилась — для текста на экране и в уведомлении: 404, 401 и 403 и остальные коды ответа сказаны
/// по-разному. Адреса подписки здесь нет.
public enum FailureReason: Equatable, Sendable {
    /// import.download.not_found
    case notFound
    /// import.download.forbidden
    case forbidden(Int)
    /// import.download.status
    case status(Int)
    case tooLarge
    case timeout
    case nameResolution
    case connection
    case tls
    case noServers
    case noAddress
    case network
    case protectedPathUnavailable
    case protectedTransportUnsupported
    case protectedAddressUnsupported

    /// Причина и код ответа, как их дает загрузка или хранит подписка. Код важен только для `.status`; без кода сказать
    /// «сервер ответил» нечего — «ошибка сети».
    public static func of(_ failure: DownloadFailure, status: Int?) -> FailureReason {
        switch failure {
        case .status:
            switch status {
            case nil: return .network
            case 404?: return .notFound
            case let code? where code == 401 || code == 403: return .forbidden(code)
            case let code?: return .status(code)
            }
        case .tooLarge: return .tooLarge
        case .timeout: return .timeout
        case .nameResolution: return .nameResolution
        case .connection: return .connection
        case .tls: return .tls
        case .noServers: return .noServers
        case .noAddress: return .noAddress
        case .network: return .network
        case .protectedPathUnavailable: return .protectedPathUnavailable
        case .protectedTransportUnsupported: return .protectedTransportUnsupported
        case .protectedAddressUnsupported: return .protectedAddressUnsupported
        }
    }
}

/// Как часто обновлять подписки сами — как у десктопа и Android («Профили и подписки», доска 12); по умолчанию — 12 часов.
public enum RefreshInterval: String, CaseIterable, Sendable {
    /// Только кнопкой «Обновить».
    case off
    case every6Hours
    case every12Hours
    case every24Hours

    public var hours: Int? {
        switch self {
        case .off: return nil
        case .every6Hours: return 6
        case .every12Hours: return 12
        case .every24Hours: return 24
        }
    }
}

/// Когда подписку обновляли: успешно и последняя попытка; nil — ни разу.
public struct SubscriptionTimes: Sendable {
    public let id: String
    public let updatedAt: Date?
    public let lastAttemptAt: Date?

    public init(id: String, updatedAt: Date?, lastAttemptAt: Date?) {
        self.id = id
        self.updatedAt = updatedAt
        self.lastAttemptAt = lastAttemptAt
    }
}

/// Каким подпискам пора обновиться: успех старше промежутка из настроек, неудача — не чаще раза в час, чтобы не стучаться
/// в недоступный сервер подписки каждую проверку.
public enum SubscriptionSchedule {
    public static let retryAfterFailure: TimeInterval = 60 * 60

    public static func due(_ subscriptions: [SubscriptionTimes], _ interval: RefreshInterval, now: Date) -> [String] {
        guard let hours = interval.hours else {
            return []
        }

        let period = TimeInterval(hours) * 60 * 60
        return subscriptions.filter { subscription in
            (subscription.updatedAt.map { now.timeIntervalSince($0) >= period } ?? true)
                && (subscription.lastAttemptAt.map { now.timeIntervalSince($0) >= retryAfterFailure } ?? true)
        }.map(\.id)
    }
}

/// Почему обновление подписки сейчас не начинается — как `UpdateBlock` десктопа и Android.
public enum UpdateBlock: Sendable {
    case none

    /// «Обновлять только через VPN», а защита не включена.
    case protectionOff

    /// Эта подписка уже обновляется.
    case busy
}

/// Можно ли обновлять подписку сейчас: с «только через VPN» адрес подписки уходит в сеть только через туннель — пока защита
/// не подключена, ничего не загружается; потом — не обновляется ли подписка уже. Проверка по расписанию смотрит только на
/// защиту (`busy` = false).
public enum UpdateGate {
    public static func block(onlyViaVpn: Bool, connected: Bool, busy: Bool = false) -> UpdateBlock {
        if onlyViaVpn, !connected {
            return .protectionOff
        }

        return busy ? .busy : .none
    }
}

/// Загруженная подписка: текст и название из заголовка Profile-Title.
public struct SubscriptionContent: Sendable {
    public let text: String
    public let title: String?

    public init(text: String, title: String?) {
        self.text = text
        self.title = title
    }
}

/// Подписка не загрузилась: причина и код ответа сервера (для `.status`). Адреса в тексте нет.
public struct SubscriptionDownloadError: Error, Equatable, Sendable {
    public let failure: DownloadFailure
    public let status: Int?

    public init(_ failure: DownloadFailure, status: Int? = nil) {
        self.failure = failure
        self.status = status
    }
}

/// Откуда загружается подписка: по сети — загрузчик приложения, в тестах — подделка.
public protocol SubscriptionSource: Sendable {
    /// Текст и название подписки; не загрузилась — `SubscriptionDownloadError` с причиной.
    func download(_ url: URLComponents) async throws -> SubscriptionContent
    /// Только через аутентифицированный вход ядра выбранного сервера; прямого запасного пути нет.
    func downloadProtected(_ url: URLComponents) async throws -> SubscriptionContent
}

extension SubscriptionSource {
    /// Источник должен явно реализовать защищенную загрузку: обычный download не является безопасным запасным путем.
    public func downloadProtected(_ url: URLComponents) async throws -> SubscriptionContent {
        throw SubscriptionDownloadError(.protectedPathUnavailable)
    }
}

/// Итог загрузки подписки для обновления: серверы и название либо почему не вышло. Адреса подписки здесь нет.
public enum FetchResult: Equatable, Sendable {
    /// Загружено и разобрано: только строки, которые разобрались; список не пустой.
    case fetched([ServerDefinition], title: String?)

    /// Не загрузилось или в ответе нет серверов — прошлый список остается.
    case failed(DownloadFailure, status: Int?)
}

/// Загрузка и разбор подписки при обновлении — как `SubscriptionFetch` Android: загрузить, разобрать, оставить строки,
/// которые разобрались; ни одного сервера — `.noServers`, и прошлый список не трогается.
public enum SubscriptionFetch {
    public static func fetch(_ url: URLComponents, from source: some SubscriptionSource, protected: Bool = false) async -> FetchResult {
        let content: SubscriptionContent
        do {
            try Task.checkCancellation()
            if protected {
                content = try await source.downloadProtected(url)
            } else {
                content = try await source.download(url)
            }
            try Task.checkCancellation()
        } catch let error as SubscriptionDownloadError {
            return .failed(error.failure, status: error.status)
        } catch {
            return .failed(.network, status: nil)
        }

        let servers = ImportParser.parse(content.text).lines.compactMap(\.result.server)
        return servers.isEmpty ? .failed(.noServers, status: nil) : .fetched(servers, title: content.title)
    }

    /// Название новой подписки: из заголовка Profile-Title, без него — сервер подписки.
    public static func suggestName(_ title: String?, _ url: URLComponents) -> String {
        title ?? url.host ?? ""
    }
}

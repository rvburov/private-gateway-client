#if canImport(Darwin)
import Foundation
import Network

/// Загрузка подписки по http(s) — как `SubscriptionDownloader` Android и `HttpSubscriptionDownloader` десктопа: не дольше
/// 20 секунд, не больше 5 МБ после распаковки, перенаправления — только внутри той же схемы (https не уходит на http, ответ
/// перенаправления тогда — отказ с его кодом). Адрес подписки — секрет: в ошибках только причина и код ответа; куки и кеш
/// не хранятся. Только для iPhone и Mac: загрузчик Linux (FoundationNetworking) ведет себя иначе, а приложению он не нужен.
public struct SubscriptionDownloader: SubscriptionSource {
    public static let userAgent = "PrivateGateway/0.1"

    /// Старый connectionProxyDictionary может обходить localhost даже с пустыми исключениями. Защищенная загрузка
    /// требует документированный HTTP CONNECT transport с allowFailover=false; остальные функции iOS 16 доступны.
    public static var supportsProtectedTransport: Bool {
        if #available(iOS 17.0, macOS 14.0, *) { return true }
        return false
    }

    private let timeout: TimeInterval
    private let maxBytes: Int
    /// Подмена сети в тестах (`URLProtocol`); nil — настоящая сеть.
    nonisolated(unsafe) private let protocols: [AnyClass]?
    private let protectedProxy: (@Sendable () async -> SubscriptionProxy?)?

    public init(
        timeout: TimeInterval = DownloadLimits.timeout, maxBytes: Int = DownloadLimits.maxBytes, protocols: [AnyClass]? = nil,
        protectedProxy: (@Sendable () async -> SubscriptionProxy?)? = nil
    ) {
        self.timeout = timeout
        self.maxBytes = maxBytes
        self.protocols = protocols
        self.protectedProxy = protectedProxy
    }

    public func download(_ url: URLComponents) async throws -> SubscriptionContent {
        try await transfer(url, proxy: nil)
    }

    /// Вход ядра получаем заново для каждой загрузки. Ядро сменилось/остановилось — старый вход отказывает; у URLSession
    /// явный прокси и для HTTP, и для HTTPS, поэтому напрямую или через системный прокси этот запрос не повторится.
    public func downloadProtected(_ url: URLComponents) async throws -> SubscriptionContent {
        try Task.checkCancellation()
        guard Self.supportsProtectedTransport else {
            throw SubscriptionDownloadError(.protectedTransportUnsupported)
        }
        let resolved = await protectedProxy?()
        try Task.checkCancellation()
        guard let proxy = resolved, proxy.valid else {
            throw SubscriptionDownloadError(.protectedPathUnavailable)
        }

        return try await transfer(url, proxy: proxy)
    }

    private func transfer(_ url: URLComponents, proxy: SubscriptionProxy?) async throws -> SubscriptionContent {
        try Task.checkCancellation()
        guard let text = url.string, SubscriptionAddress.parse(text) != nil, let target = url.url else {
            throw SubscriptionDownloadError(.network)
        }

        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = timeout
        configuration.timeoutIntervalForResource = timeout
        configuration.httpCookieStorage = nil
        configuration.urlCache = nil
        configuration.requestCachePolicy = .reloadIgnoringLocalCacheData
        // Исключение ATS разрешает пользовательские HTTP-подписки; HTTPS по-прежнему требует TLS 1.2 и доверенный
        // сертификат: обычную системную проверку сервера URLSession не переопределяем.
        configuration.tlsMinimumSupportedProtocolVersion = .TLSv12
        if let proxy {
            guard #available(iOS 17.0, macOS 14.0, *), let port = NWEndpoint.Port(rawValue: UInt16(proxy.port)) else {
                throw SubscriptionDownloadError(.protectedTransportUnsupported)
            }
            // Modern API задает именно CONNECT transport, включая HTTP и локальные адреса. Запрещен любой failover
            // к непроксированному соединению; credential относится к proxy, а не к заголовкам запроса сайта.
            var requiredProxy = ProxyConfiguration(
                httpCONNECTProxy: .hostPort(host: NWEndpoint.Host(SubscriptionProxy.host), port: port), tlsOptions: nil
            )
            requiredProxy.allowFailover = false
            requiredProxy.applyCredential(username: proxy.user, password: proxy.password)
            configuration.proxyConfigurations = [requiredProxy]
        }
        if let protocols {
            configuration.protocolClasses = protocols
        }

        var request = URLRequest(url: target)
        request.setValue(Self.userAgent, forHTTPHeaderField: "User-Agent")
        let transfer = Transfer(scheme: target.scheme?.lowercased(), maxBytes: maxBytes)
        let session = URLSession(configuration: configuration, delegate: transfer, delegateQueue: nil)
        defer { session.invalidateAndCancel() }
        return try await transfer.run(session.dataTask(with: request))
    }
}

/// Одна загрузка: ответ, тело частями с пределом размера, перенаправления только в той же схеме. Делегат сеанса URLSession
/// вызывается в его очереди; состояние — под замком.
private final class Transfer: NSObject, URLSessionDataDelegate, @unchecked Sendable {
    private let scheme: String?
    private let maxBytes: Int
    private let lock = NSLock()
    private var body = Data()
    private var title: String?
    private var failure: SubscriptionDownloadError?
    private var continuation: CheckedContinuation<SubscriptionContent, any Error>?
    private var task: URLSessionDataTask?
    private var cancelled = false

    init(scheme: String?, maxBytes: Int) {
        self.scheme = scheme
        self.maxBytes = maxBytes
    }

    func run(_ task: URLSessionDataTask) async throws -> SubscriptionContent {
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                let start = locked { () -> Bool in
                    guard !cancelled else { return false }
                    self.continuation = continuation
                    self.task = task
                    return true
                }
                if start {
                    task.resume()
                } else {
                    continuation.resume(throwing: CancellationError())
                }
            }
        } onCancel: {
            self.cancel()
        }
    }

    private func cancel() {
        let (task, continuation) = locked { () -> (URLSessionDataTask?, CheckedContinuation<SubscriptionContent, any Error>?) in
            cancelled = true
            defer { self.continuation = nil }
            return (self.task, self.continuation)
        }
        task?.cancel()
        continuation?.resume(throwing: CancellationError())
    }

    func urlSession(
        _ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest
    ) async -> URLRequest? {
        // Другая схема — не идти: ответ перенаправления станет итогом, и его код скажет, почему не загрузилось.
        request.url?.scheme?.lowercased() == scheme ? request : nil
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive response: URLResponse) async -> URLSession.ResponseDisposition {
        let error: SubscriptionDownloadError?
        if let http = response as? HTTPURLResponse {
            if !(200...299).contains(http.statusCode) {
                error = SubscriptionDownloadError(.status, status: http.statusCode)
            } else if http.expectedContentLength > Int64(maxBytes) {
                error = SubscriptionDownloadError(.tooLarge)
            } else {
                error = nil
            }

            locked { title = SubscriptionTitle.from(http.value(forHTTPHeaderField: "Profile-Title")) }
        } else {
            error = SubscriptionDownloadError(.network)
        }

        guard let error else {
            return .allow
        }

        locked { failure = error }
        return .cancel
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        let tooLarge = locked { () -> Bool in
            guard failure == nil else { return false }
            // Не накапливаем лишний chunk после предела даже при запоздалом callback отмененной задачи.
            if data.count > maxBytes - body.count {
                failure = SubscriptionDownloadError(.tooLarge)
                return true
            }
            body.append(data)
            if body.count > maxBytes, failure == nil {
                failure = SubscriptionDownloadError(.tooLarge)
                return true
            }

            return false
        }
        if tooLarge {
            dataTask.cancel()
        }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: (any Error)?) {
        let (continuation, result) = locked { () -> (CheckedContinuation<SubscriptionContent, any Error>?, Result<SubscriptionContent, any Error>) in
            defer { self.continuation = nil }
            if let failure {
                return (self.continuation, .failure(failure))
            }

            if let error {
                return (self.continuation, .failure(SubscriptionDownloadError(Self.failure(error))))
            }

            return (self.continuation, .success(SubscriptionContent(text: String(decoding: body, as: UTF8.self), title: title)))
        }
        continuation?.resume(with: result)
    }

    /// Причина по ошибке сети — как у Android: имя не найдено, тайм-аут, TLS, нет соединения, прочее.
    static func failure(_ error: any Error) -> DownloadFailure {
        guard let code = (error as? URLError)?.code else {
            return .network
        }

        switch code {
        case .cannotFindHost, .dnsLookupFailed:
            return .nameResolution
        case .timedOut:
            return .timeout
        case .secureConnectionFailed, .serverCertificateHasBadDate, .serverCertificateUntrusted, .serverCertificateHasUnknownRoot,
             .serverCertificateNotYetValid, .clientCertificateRejected, .clientCertificateRequired:
            return .tls
        case .cannotConnectToHost, .networkConnectionLost, .notConnectedToInternet:
            return .connection
        default:
            return .network
        }
    }

    private func locked<T>(_ body: () -> T) -> T {
        lock.lock()
        defer { lock.unlock() }
        return body()
    }
}
#endif

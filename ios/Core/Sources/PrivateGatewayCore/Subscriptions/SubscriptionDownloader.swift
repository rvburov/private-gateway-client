#if canImport(Darwin)
import Foundation

/// Загрузка подписки по http(s) — как `SubscriptionDownloader` Android и `HttpSubscriptionDownloader` десктопа: не дольше
/// 20 секунд, не больше 5 МБ после распаковки, перенаправления — только внутри той же схемы (https не уходит на http, ответ
/// перенаправления тогда — отказ с его кодом). Адрес подписки — секрет: в ошибках только причина и код ответа; куки и кеш
/// не хранятся. Только для iPhone и Mac: загрузчик Linux (FoundationNetworking) ведет себя иначе, а приложению он не нужен.
public struct SubscriptionDownloader: SubscriptionSource {
    public static let userAgent = "PrivateGateway/0.1"

    private let timeout: TimeInterval
    private let maxBytes: Int
    /// Подмена сети в тестах (`URLProtocol`); nil — настоящая сеть.
    nonisolated(unsafe) private let protocols: [AnyClass]?

    public init(timeout: TimeInterval = DownloadLimits.timeout, maxBytes: Int = DownloadLimits.maxBytes, protocols: [AnyClass]? = nil) {
        self.timeout = timeout
        self.maxBytes = maxBytes
        self.protocols = protocols
    }

    public func download(_ url: URLComponents) async throws -> SubscriptionContent {
        guard let text = url.string, SubscriptionAddress.parse(text) != nil, let target = url.url else {
            throw SubscriptionDownloadError(.network)
        }

        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = timeout
        configuration.timeoutIntervalForResource = timeout
        configuration.httpCookieStorage = nil
        configuration.urlCache = nil
        configuration.requestCachePolicy = .reloadIgnoringLocalCacheData
        if let protocols {
            configuration.protocolClasses = protocols
        }

        var request = URLRequest(url: target)
        request.setValue(Self.userAgent, forHTTPHeaderField: "User-Agent")
        let transfer = Transfer(scheme: target.scheme?.lowercased(), maxBytes: maxBytes)
        let session = URLSession(configuration: configuration, delegate: transfer, delegateQueue: nil)
        defer { session.finishTasksAndInvalidate() }
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

    init(scheme: String?, maxBytes: Int) {
        self.scheme = scheme
        self.maxBytes = maxBytes
    }

    func run(_ task: URLSessionDataTask) async throws -> SubscriptionContent {
        try await withCheckedThrowingContinuation { continuation in
            locked { self.continuation = continuation }
            task.resume()
        }
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

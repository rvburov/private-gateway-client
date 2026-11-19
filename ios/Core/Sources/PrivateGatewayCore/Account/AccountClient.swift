#if canImport(Darwin)
import Foundation

/// API приложения на сайте Private Gateway Web (/api/app/…) — `AccountApi` Android: только HTTPS на постоянный адрес,
/// перенаправлениям не следует (токен не уходит на другой адрес), ответ не больше 64 КБ, 15 секунд на запрос, без кук и кеша.
/// Тела ошибок и тексты ошибок сети наружу не идут: итог — причина (`AccountError`) или вид ответа (`StatusResult`). Токен,
/// пароль, коды и почта никуда, кроме тела запроса и заголовка Authorization, не пишутся. Только для iPhone и Mac.
public struct AccountClient: AccountSource {
    public static let timeout: TimeInterval = 15

    private let base: URL
    private let userAgent: String
    /// Подмена сети в тестах (`URLProtocol`); nil — настоящая сеть.
    private let protocols: [AnyClass]?

    /// - Parameter appVersion: версия приложения для User-Agent.
    public init(appVersion: String, base: URL = AccountAnswers.site, protocols: [AnyClass]? = nil) {
        self.base = base
        userAgent = "PrivateGateway/" + (appVersion.isEmpty ? "0.1" : appVersion)
        self.protocols = protocols
    }

    public func login(email: String, password: String, code: String?, recovery: String?, device: DeviceInfo) async -> LoginResult {
        let body = AccountAnswers.loginBody(email: email, password: password, code: code, recovery: recovery, device: device)
        guard let answer = await send("POST", AccountAnswers.loginPath, token: nil, body: body) else {
            return .failed(.network)
        }

        let error = answer.status == 401 || answer.status == 409 ? AccountAnswers.errorCode(answer.body) : nil
        return AccountAnswers.login(
            status: answer.status, body: answer.status == 200 ? answer.body : nil, errorCode: error, retryAfter: answer.retryAfter
        )
    }

    public func status(token: String) async -> StatusResult {
        guard let answer = await send("GET", AccountAnswers.statusPath, token: token, body: nil) else {
            return .unreachable
        }

        return AccountAnswers.status(status: answer.status, body: answer.status == 200 ? answer.body : nil)
    }

    public func logout(token: String) async -> Bool {
        guard let answer = await send("POST", AccountAnswers.logoutPath, token: token, body: Data()) else {
            return false
        }

        return (200 ... 299).contains(answer.status)
    }

    private struct Answer {
        let status: Int
        let body: Data?
        let retryAfter: String?
    }

    /// Запрос к сайту; ответа нет (сеть, DNS, TLS, таймаут, ответ больше предела) — nil.
    private func send(_ method: String, _ path: String, token: String?, body: Data?) async -> Answer? {
        guard let url = URL(string: path, relativeTo: base)?.absoluteURL, url.scheme?.lowercased() == "https" || Self.loopback(url) else {
            return nil
        }

        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = Self.timeout
        configuration.timeoutIntervalForResource = Self.timeout
        configuration.httpCookieStorage = nil
        configuration.urlCache = nil
        configuration.requestCachePolicy = .reloadIgnoringLocalCacheData
        configuration.tlsMinimumSupportedProtocolVersion = .TLSv12
        if let protocols {
            configuration.protocolClasses = protocols
        }

        var request = URLRequest(url: url)
        request.httpMethod = method
        request.setValue(userAgent, forHTTPHeaderField: "User-Agent")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        if let token {
            request.setValue("Bearer " + token, forHTTPHeaderField: "Authorization")
        }

        if let body {
            request.setValue("application/json; charset=utf-8", forHTTPHeaderField: "Content-Type")
            request.httpBody = body
        }

        let session = URLSession(configuration: configuration, delegate: NoRedirects(), delegateQueue: nil)
        defer { session.invalidateAndCancel() }
        guard let (data, response) = try? await session.data(for: request), let http = response as? HTTPURLResponse,
              data.count <= AccountAnswers.maxBytes
        else {
            return nil
        }

        return Answer(status: http.statusCode, body: data, retryAfter: http.value(forHTTPHeaderField: "Retry-After"))
    }

    /// http — только на свой компьютер (тесты): буквальный адрес, имя хоста DNS не решает.
    private static func loopback(_ url: URL) -> Bool {
        url.scheme?.lowercased() == "http" && ["127.0.0.1", "localhost", "::1"].contains(url.host?.lowercased() ?? "")
    }
}

/// Перенаправлениям не следуем: ответ перенаправления становится итогом (код 3xx — «сайт ответил ошибкой»).
private final class NoRedirects: NSObject, URLSessionTaskDelegate, Sendable {
    func urlSession(
        _ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest
    ) async -> URLRequest? {
        nil
    }
}
#endif

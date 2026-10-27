#if canImport(Darwin)
import Foundation
import XCTest
@testable import PrivateGatewayCore

/// Ответ «сервера» подписки без сети: код, заголовки, тело частями или ошибка сети.
private struct Stub: Sendable {
    var status = 200
    var headers: [String: String] = [:]
    var chunks: [Data] = []
    var error: URLError.Code?
}

private final class StubProtocol: URLProtocol {
    nonisolated(unsafe) static var stub = Stub()
    nonisolated(unsafe) static var seen: URLRequest?

    override class func canInit(with request: URLRequest) -> Bool { true }

    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        Self.seen = request
        let stub = Self.stub
        if let code = stub.error {
            client?.urlProtocol(self, didFailWithError: URLError(code))
            return
        }

        let url = request.url ?? URL(fileURLWithPath: "/")
        let response = HTTPURLResponse(url: url, statusCode: stub.status, httpVersion: "HTTP/1.1", headerFields: stub.headers)
        if let response {
            client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        }

        for chunk in stub.chunks {
            client?.urlProtocol(self, didLoad: chunk)
        }

        client?.urlProtocolDidFinishLoading(self)
    }

    override func stopLoading() {}
}

/// Загрузчик подписки — те же случаи, что `SubscriptionDownloaderTest` Android: текст и название, код ответа, предел размера
/// по заголовку и по телу, причины ошибок сети, только http(s).
final class SubscriptionDownloaderTests: XCTestCase {
    private let url = SubscriptionAddress.parse("https://example.com/sub/0123456789abcdef")!

    private func download(maxBytes: Int = DownloadLimits.maxBytes) async throws -> SubscriptionContent {
        try await SubscriptionDownloader(maxBytes: maxBytes, protocols: [StubProtocol.self]).download(url)
    }

    private func failure(maxBytes: Int = DownloadLimits.maxBytes) async -> SubscriptionDownloadError? {
        do {
            _ = try await download(maxBytes: maxBytes)
            return nil
        } catch {
            return error as? SubscriptionDownloadError
        }
    }

    func testTextAndTitleComeThrough() async throws {
        StubProtocol.stub = Stub(headers: ["Profile-Title": "My VPN"], chunks: [Data("vless://a\n".utf8), Data("trojan://b".utf8)])
        let content = try await download()
        XCTAssertEqual(content.text, "vless://a\ntrojan://b")
        XCTAssertEqual(content.title, "My VPN")
        XCTAssertEqual(StubProtocol.seen?.value(forHTTPHeaderField: "User-Agent"), SubscriptionDownloader.userAgent)
    }

    func testStatusIsTheFailure() async {
        StubProtocol.stub = Stub(status: 404, chunks: [Data("not found".utf8)])
        let error = await failure()
        XCTAssertEqual(error, SubscriptionDownloadError(.status, status: 404))
    }

    func testTooLargeByHeaderAndByBody() async {
        StubProtocol.stub = Stub(headers: ["Content-Length": "100"], chunks: [Data(repeating: 65, count: 100)])
        let byHeader = await failure(maxBytes: 10)
        XCTAssertEqual(byHeader?.failure, .tooLarge)

        StubProtocol.stub = Stub(chunks: [Data(repeating: 65, count: 8), Data(repeating: 65, count: 8)])
        let byBody = await failure(maxBytes: 10)
        XCTAssertEqual(byBody?.failure, .tooLarge)
    }

    func testNetworkErrorsAreNamed() async {
        let cases: [(URLError.Code, DownloadFailure)] = [
            (.cannotFindHost, .nameResolution), (.timedOut, .timeout), (.secureConnectionFailed, .tls), (.cannotConnectToHost, .connection),
            (.badServerResponse, .network),
        ]
        for (code, expected) in cases {
            StubProtocol.stub = Stub(error: code)
            let error = await failure()
            XCTAssertEqual(error?.failure, expected, "\(code)")
        }
    }

    func testOnlyHttpAndHttps() async {
        var ftp = URLComponents()
        ftp.scheme = "ftp"
        ftp.host = "example.com"
        do {
            _ = try await SubscriptionDownloader(protocols: [StubProtocol.self]).download(ftp)
            XCTFail("ftp не загружается")
        } catch {
            XCTAssertEqual(error as? SubscriptionDownloadError, SubscriptionDownloadError(.network))
        }
    }
}
#endif

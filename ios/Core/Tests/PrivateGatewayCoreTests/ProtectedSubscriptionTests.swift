#if canImport(Darwin)
import Darwin
import Foundation
import XCTest
import PrivateGatewayCore

/// Настоящий URLSession и собственный HTTP-прокси на петле: адрес .invalid невозможно найти напрямую. До интернета тест
/// не обращается; 407 проверяет передачу учетных данных именно посреднику, CONNECT — тот же путь у HTTPS.
final class ProtectedSubscriptionTests: XCTestCase {
    func testHttpUsesTheAuthenticatedExplicitProxyWithoutResolvingTheTarget() async throws {
        try requireProtectedTransport()
        let server = try LocalHttpProxy()
        defer { server.stop() }
        let proxy = SubscriptionProxy(port: server.port, user: "test-user", password: "test-password")
        let source = SubscriptionDownloader(timeout: 5, protectedProxy: { proxy })
        let url = try XCTUnwrap(SubscriptionAddress.parse("http://subscription.invalid/sub/fake-token"))
        let downloaded = try await source.downloadProtected(url)
        XCTAssertEqual(downloaded.text, "test-subscription")
        let get = try XCTUnwrap(server.requests.first { $0.hasPrefix("GET ") })
        XCTAssertTrue(get.hasPrefix("GET /sub/fake-token ") || get.hasPrefix("GET http://subscription.invalid/sub/fake-token "))
        XCTAssertTrue(server.requests.contains { server.authenticated($0) })
        if server.requests.contains(where: { $0.hasPrefix("CONNECT subscription.invalid:80 ") }) {
            XCTAssertFalse(get.lowercased().contains("proxy-authorization:"), "credential прокси не попадает в запрос сайта внутри CONNECT")
            XCTAssertFalse(get.lowercased().contains("\r\nauthorization:"))
        }
    }

    func testHttpsAlsoUsesTheProxyAndCannotFallBackAfterItsRefusal() async throws {
        try requireProtectedTransport()
        let server = try LocalHttpProxy()
        defer { server.stop() }
        let proxy = SubscriptionProxy(port: server.port, user: "test-user", password: "test-password")
        let url = try XCTUnwrap(SubscriptionAddress.parse("https://subscription.invalid/sub/fake-token"))
        do {
            _ = try await SubscriptionDownloader(timeout: 5, protectedProxy: { proxy }).downloadProtected(url)
            XCTFail("прокси отклонил CONNECT: защищенная загрузка должна отказать")
        } catch {}
        XCTAssertTrue(server.requests.contains { $0.hasPrefix("CONNECT subscription.invalid:443 ") })
        XCTAssertTrue(server.requests.contains { server.authenticated($0) })
    }

    func testAClosedProxyCannotSendTheTokenToADirectLocalTarget() async throws {
        try requireProtectedTransport()
        let origin = try LocalHttpProxy()
        defer { origin.stop() }
        let closed = try LocalHttpProxy()
        let port = closed.port
        closed.stop()
        let proxy = SubscriptionProxy(port: port, user: "test-user", password: "test-password")
        let url = try XCTUnwrap(SubscriptionAddress.parse("http://127.0.0.1:\(origin.port)/sub/fake-token"))
        do {
            _ = try await SubscriptionDownloader(timeout: 3, protectedProxy: { proxy }).downloadProtected(url)
            XCTFail("закрытый вход ядра — ошибка без прямого пути")
        } catch {}
        XCTAssertTrue(origin.requests.isEmpty, "даже локальный адрес не исключается из явного защищенного прокси")
    }

    private func requireProtectedTransport() throws {
        try XCTSkipUnless(SubscriptionDownloader.supportsProtectedTransport, "защищенные подписки требуют iOS 17+ / macOS 14+; legacy отказ проверяет SubscriptionDownloaderTests")
    }
}

private final class LocalHttpProxy: @unchecked Sendable {
    private let fd: Int32
    let port: Int
    let basic = Data("test-user:test-password".utf8).base64EncodedString()
    private let lock = NSLock()
    private var running = true
    private var received: [String] = []
    var requests: [String] {
        lock.lock()
        defer { lock.unlock() }
        return received
    }

    init() throws {
        let socketFd = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP)
        guard socketFd >= 0 else { throw POSIXError(.ENFILE) }
        var address = sockaddr_in()
        address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        address.sin_family = sa_family_t(AF_INET)
        address.sin_addr.s_addr = inet_addr("127.0.0.1")
        let bound = withUnsafePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { bind(socketFd, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) }
        }
        var length = socklen_t(MemoryLayout<sockaddr_in>.size)
        let named = withUnsafeMutablePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { getsockname(socketFd, $0, &length) }
        }
        guard bound == 0, named == 0, listen(socketFd, 8) == 0 else {
            close(socketFd)
            throw POSIXError(.EADDRINUSE)
        }
        fd = socketFd
        port = Int(UInt16(bigEndian: address.sin_port))
        DispatchQueue.global().async { [self] in serve() }
    }

    func stop() {
        lock.lock()
        let wasRunning = running
        running = false
        lock.unlock()
        if wasRunning {
            _ = shutdown(fd, SHUT_RDWR)
            close(fd)
        }
    }

    private func serve() {
        while true {
            lock.lock()
            let active = running
            lock.unlock()
            guard active else { return }
            let client = accept(fd, nil, nil)
            guard client >= 0 else { return }
            respond(client)
            close(client)
        }
    }

    func authenticated(_ request: String) -> Bool {
        request.components(separatedBy: "\r\n").contains { line in
            let parts = line.split(separator: ":", maxSplits: 1)
            return parts.count == 2 && parts[0].lowercased() == "proxy-authorization"
                && parts[1].trimmingCharacters(in: .whitespaces) == "Basic " + basic
        }
    }

    private func respond(_ client: Int32) {
        var timeout = timeval(tv_sec: 3, tv_usec: 0)
        _ = setsockopt(client, SOL_SOCKET, SO_RCVTIMEO, &timeout, socklen_t(MemoryLayout<timeval>.size))
        var on: Int32 = 1
        _ = setsockopt(client, SOL_SOCKET, SO_NOSIGPIPE, &on, socklen_t(MemoryLayout<Int32>.size))
        guard let request = readHeaders(client) else { return }
        record(request)
        if authenticated(request), request.hasPrefix("CONNECT "), request.split(separator: " ").dropFirst().first?.hasSuffix(":80") == true {
            // Modern ProxyConfiguration может туннелировать и HTTP: после CONNECT ответ сайта идет по тому же сокету.
            write("HTTP/1.1 200 Connection Established\r\n\r\n", client)
            guard let inner = readHeaders(client) else { return }
            record(inner)
            write(bodyResponse, client)
            return
        }
        let response: String
        if !authenticated(request) {
            response = "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"test\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
        } else if request.hasPrefix("CONNECT ") {
            response = "HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
        } else {
            response = bodyResponse
        }
        write(response, client)
    }

    private var bodyResponse: String {
        let body = "test-subscription"
        return "HTTP/1.1 200 OK\r\nContent-Length: \(body.utf8.count)\r\nConnection: close\r\n\r\n\(body)"
    }

    private func write(_ response: String, _ client: Int32) {
        _ = Data(response.utf8).withUnsafeBytes { send(client, $0.baseAddress, $0.count, 0) }
    }

    private func record(_ request: String) {
        lock.lock()
        received.append(request)
        lock.unlock()
    }

    private func readHeaders(_ client: Int32) -> String? {
        var data = Data()
        var chunk = [UInt8](repeating: 0, count: 2048)
        while data.count < 16_384 {
            let count = chunk.withUnsafeMutableBytes { recv(client, $0.baseAddress, $0.count, 0) }
            guard count > 0 else { return nil }
            data.append(contentsOf: chunk.prefix(count))
            if data.range(of: Data("\r\n\r\n".utf8)) != nil { break }
        }
        return String(decoding: data, as: UTF8.self)
    }
}
#endif

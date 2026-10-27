import Darwin
import PrivateGatewayCore
import XCTest
@testable import PrivateGateway

/// Замеры на машине CI без внешней сети: порт, который слушает тест, тот же порт закрытым, имя localhost и имя из зоны
/// .invalid (RFC 6761: такого имени нет нигде), TLS к порту, где TLS никто не говорит.
final class ProbeRunnerTests: XCTestCase {
    /// Сокет, слушающий 127.0.0.1 на свободном порту; соединения ядро принимает само, без accept.
    private func listener() -> (fd: Int32, port: Int) {
        let fd = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP)
        XCTAssertGreaterThanOrEqual(fd, 0)
        var address = sockaddr_in()
        address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        address.sin_family = sa_family_t(AF_INET)
        address.sin_addr.s_addr = inet_addr("127.0.0.1")
        let bound = withUnsafePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { bind(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) }
        }
        XCTAssertEqual(bound, 0)
        XCTAssertEqual(listen(fd, 4), 0)
        var length = socklen_t(MemoryLayout<sockaddr_in>.size)
        let named = withUnsafeMutablePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { getsockname(fd, $0, &length) }
        }
        XCTAssertEqual(named, 0)
        return (fd, Int(UInt16(bigEndian: address.sin_port)))
    }

    func testOpenPortAnswers() async {
        let server = listener()
        defer { close(server.fd) }
        let result = await ProbeRunner.tcp("127.0.0.1", server.port)
        XCTAssertEqual(result.steps.map(\.outcome), [.ok])
        XCTAssertEqual(result.steps.first?.address, "127.0.0.1")
        XCTAssertNotNil(result.steps.first?.timeMs)
        XCTAssertEqual(result.summary().key, "probe.summary.tcp")
    }

    func testClosedPortIsRefused() async {
        let server = listener()
        close(server.fd)
        let result = await ProbeRunner.tcp("127.0.0.1", server.port)
        XCTAssertEqual(result.steps.map(\.outcome), [.refused])
        XCTAssertNil(result.steps.first?.timeMs)
        XCTAssertEqual(result.summary().key, "probe.summary.refused")
    }

    func testLocalhostIsFoundAndAnInvalidNameIsNot() async {
        let found = await ProbeRunner.dns("localhost")
        XCTAssertTrue(found.steps.contains { $0.address == "127.0.0.1" || $0.address == "::1" }, "\(found.steps)")
        XCTAssertTrue(found.steps.allSatisfy { $0.outcome == .ok })
        XCTAssertEqual(Set(found.steps.compactMap(\.address)).count, found.steps.count, "без повторов")

        let missing = await ProbeRunner.dns("nothing.invalid")
        XCTAssertEqual(missing.steps.map(\.outcome), [.notFound])
        let tcp = await ProbeRunner.tcp("nothing.invalid", 443)
        XCTAssertEqual(tcp.steps.map(\.outcome), [.notFound])
        XCTAssertEqual(tcp.summary().key, "probe.summary.notFound")
    }

    func testTlsWhereNobodySpeaksTlsEndsWithoutCertificateFacts() async {
        let server = listener()
        defer { close(server.fd) }
        let result = await ProbeRunner.tls("127.0.0.1", server.port)
        XCTAssertEqual(result.steps.count, 1)
        XCTAssertNil(result.steps.first?.tls)
        XCTAssertNotEqual(result.steps.first?.outcome, .ok, "рукопожатия не было — и ответа нет")
    }
}

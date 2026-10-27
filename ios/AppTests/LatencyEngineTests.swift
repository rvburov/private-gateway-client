import PrivateGatewayCore
import XCTest
@testable import PrivateGateway

/// Настоящий libXray в процессе приложения: замер задержки идет здесь, а не в расширении туннеля (решение 468).
final class LatencyEngineTests: XCTestCase {
    /// Сервер на закрытом порту 127.0.0.1 — «нет ответа» у него, а не отказ ядра целиком: libXray в приложении меряет.
    func testPingBatchRunsInTheAppProcess() async throws {
        let link = "vless://11111111-2222-3333-4444-555555555555@127.0.0.1:9?encryption=none&security=none&type=tcp#Local"
        let server = try XCTUnwrap(LinkParser.parse(link).server)
        let config = try XCTUnwrap(PingBatch.outbounds(server, CoreSettings()).json)
        let request = try XCTUnwrap(PingBatch.request([config], url: "http://127.0.0.1:9/", timeoutSeconds: 2))

        let response = await LatencyEngine.ping(request)

        XCTAssertTrue(XrayResponse.parse(response).success, SecretMasker.mask(response))
        XCTAssertEqual(PingBatch.parse(response, 1), [Latency.failed(.noResponse)], SecretMasker.mask(response))
    }
}

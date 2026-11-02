import Foundation
import XCTest
@testable import PrivateGatewayCore

/// «Задержка» на «Проверке»: медиана трех замеров, какие серверы меряются по TCP, ответ расширения туннеля.
final class ServerDelayTests: XCTestCase {
    func testMedianIsTheMiddleOrTheMeanOfTwo() {
        XCTAssertNil(ServerDelay.median([]))
        XCTAssertEqual(ServerDelay.median([40]), 40)
        XCTAssertEqual(ServerDelay.median([160, 38, 42]), 42, "по порядку, а не по времени замера")
        XCTAssertEqual(ServerDelay.median([38, 41]), 40, "половина — вверх")
        XCTAssertEqual(ServerDelay.median([10, 20, 30, 40]), 25)
        XCTAssertEqual(ServerDelay.samples, 3)
    }

    func testOnlyTcpServersAreMeasuredDirectly() {
        XCTAssertEqual(ProxyProtocol.allCases.filter(ServerDelay.measures), [.vless, .vmess, .trojan, .shadowsocks])
    }

    func testTheTunnelAnswersWithMilliseconds() {
        XCTAssertEqual(TunnelMessages.delayMs(TunnelMessages.delayAnswer(38)), 38)
        XCTAssertNil(TunnelMessages.delayMs(TunnelMessages.delayAnswer(nil)), "не измерено — пустой ответ")
        XCTAssertNil(TunnelMessages.delayMs(nil), "расширение не ответило")
        XCTAssertNil(TunnelMessages.delayMs(Data("PGC-SVC-002".utf8)))
        XCTAssertNil(TunnelMessages.delayMs(Data("0".utf8)))
        XCTAssertNotEqual(TunnelMessages.delay, TunnelMessages.reload)
        XCTAssertNotEqual(TunnelMessages.delay, TunnelMessages.subscriptionProxy)
    }
}

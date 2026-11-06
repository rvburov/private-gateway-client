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

    func testTheVerdictTellsASilentServerFromOneThatAnswers() {
        let answered = ProbeStep(index: 1, address: "203.0.113.10", timeMs: 38, outcome: .ok)
        XCTAssertEqual(ServerDelay.verdict(.vless, port: 443, step: answered),
                       "server answers on tcp  port=443 ms=38  profile not accepted or no internet through the server")
        for outcome in [ProbeOutcome.timedOut, .refused, .unreachable, .failed] {
            let silent = ServerDelay.verdict(.trojan, port: 8443, step: ProbeStep(index: 1, address: "203.0.113.10", timeMs: nil, outcome: outcome))
            XCTAssertEqual(silent, "server silent on tcp  port=8443  this network does not reach it or the server is off", outcome.rawValue)
            XCTAssertFalse(silent.contains("203.0.113.10"), "адреса сервера в журнале нет")
        }

        XCTAssertFalse(ServerDelay.verdict(.vless, port: 443, step: answered).contains("203.0.113.10"))
        XCTAssertEqual(ServerDelay.verdict(.vless, port: 443, step: ProbeStep(index: 1, address: nil, timeMs: nil, outcome: .notFound)),
                       "server tcp not checked  name not found in this network")
        XCTAssertEqual(ServerDelay.verdict(.hysteria2, port: 443, step: nil), "server tcp not checked  udp protocol", "UDP по TCP не судим")
        XCTAssertEqual(ServerDelay.verdict(.vless, port: 443, step: nil), "server tcp not checked  no answer from the probe")
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

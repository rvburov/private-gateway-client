import Foundation
import XCTest
@testable import PrivateGatewayCore

/// iPhone, «Ошибка» при блоке при обрыве (DECISIONS.md 2026-11-03, п. 10): «Блокировка» или «Ошибка» — по пробе сети из
/// приложения, а уведомление говорит то же, что главная.
final class BlockProbeTests: XCTestCase {
    private let down = TunnelStatus(state: .error, serverId: "srv-1", error: TunnelErrors.serverNotResponding)
    private let failed = TunnelStatus(state: .error, serverId: "srv-1", error: TunnelErrors.coreFailed)
    private let other = TunnelStatus(state: .error, error: TunnelErrors.otherVpn)

    func testProbesOnlyAnErrorUnderTheKillSwitch() {
        XCTAssertTrue(BlockProbe.needed(down, killSwitch: true))
        XCTAssertTrue(BlockProbe.needed(failed, killSwitch: true))
        XCTAssertFalse(BlockProbe.needed(down, killSwitch: false), "без блока при обрыве «Блокировки» не бывает")
        XCTAssertFalse(BlockProbe.needed(other, killSwitch: true), "другой VPN — своя страница проблемы")
        for state in TunnelState.allCases where state != .error {
            XCTAssertFalse(BlockProbe.needed(TunnelStatus(state: state), killSwitch: true), "\(state)")
        }
    }

    func testTheProbeDecidesOverTheTunnelState() {
        XCTAssertTrue(BlockProbe.blocked(down, answered: false, tunnelUp: false), "сеть закрыта — «Блокировка», как бы ни выглядел туннель")
        XCTAssertFalse(BlockProbe.blocked(down, answered: true, tunnelUp: true), "сеть ответила — трафик идет напрямую: «Ошибка»")
        XCTAssertTrue(BlockProbe.blocked(down, answered: nil, tunnelUp: true), "пробы еще нет — по туннелю без ядра")
        XCTAssertFalse(BlockProbe.blocked(down, answered: nil, tunnelUp: false))
        XCTAssertFalse(BlockProbe.blocked(TunnelStatus(state: .connected), answered: false, tunnelUp: true), "не «Ошибка» — не «Блокировка»")
    }

    func testAnOpenNetworkGetsTheDirectNoticeAndAClosedOneNone() throws {
        let direct = try XCTUnwrap(BlockProbe.notice(down, answered: true, server: "Нидерланды 01"))
        XCTAssertEqual(direct.kind, .serverDown)
        XCTAssertEqual(direct.textKey, "notify.down.direct", "уведомление — как без блока: трафик идет напрямую")
        XCTAssertEqual(direct.server, "Нидерланды 01")
        XCTAssertEqual(direct.tag, Notice.protectionTag, "заменяет «Интернет заблокирован» расширения")

        let failure = try XCTUnwrap(BlockProbe.notice(failed, answered: true, server: nil))
        XCTAssertEqual(failure.textKey, "notify.failed.direct")
        XCTAssertEqual(failure.code, TunnelErrors.coreFailed)

        XCTAssertNil(BlockProbe.notice(down, answered: false, server: nil), "сеть закрыта — расширение уже сказало «Интернет заблокирован»")
        XCTAssertNil(BlockProbe.notice(other, answered: true, server: nil))
        XCTAssertNil(BlockProbe.notice(TunnelStatus(state: .connected), answered: true, server: nil))
    }

    func testTwoTargetsOfDifferentCompaniesWithoutDns() {
        XCTAssertEqual(BlockProbe.targets.count, 2)
        XCTAssertEqual(BlockProbe.targets.first?.host, TunnelProbe.host)
        XCTAssertEqual(Set(BlockProbe.targets.map { $0.host }).count, 2, "один закрытый адрес не дает ложной «Блокировки»")
        XCTAssertTrue(BlockProbe.targets.allSatisfy { $0.host.allSatisfy { $0.isNumber || $0 == "." } }, "без DNS")
    }

    func testTheLogLineHasNoAddresses() {
        XCTAssertEqual(BlockProbe.logLine(answered: true), "network answers without the tunnel  traffic goes direct")
        XCTAssertEqual(BlockProbe.logLine(answered: false), "network closed without the tunnel  traffic blocked")
        XCTAssertFalse(BlockProbe.logLine(answered: true).contains(TunnelProbe.host))
    }
}

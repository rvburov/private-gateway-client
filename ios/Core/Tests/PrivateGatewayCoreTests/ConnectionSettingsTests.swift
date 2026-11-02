import XCTest
@testable import PrivateGatewayCore

/// «Подключение и ядро» телефона: по умолчанию — как в макете и как туннель работал до этой страницы; свой MTU — только
/// 1280–1500; таймаут — срок пробы связи; отпечатки и уровни журнала — значения Xray. Все, что получает ядро, доходит до
/// настроек ядра и плана туннеля.
final class ConnectionSettingsTests: XCTestCase {
    func testDefaultsAreTheBoardAndTheFormerTunnel() {
        let connection = ConnectionSettings()
        XCTAssertEqual(connection.coreLog, .warning)
        XCTAssertNil(connection.mtu, "MTU «Авто»")
        XCTAssertTrue(connection.sniffing)
        XCTAssertEqual(connection.fingerprint, .profile)
        XCTAssertFalse(connection.mux)
        XCTAssertFalse(connection.tlsFragment)
        XCTAssertEqual(connection.connectTimeout, 10)
        XCTAssertEqual(connection.tunMtu, 1500, "«Авто» — тот MTU, с которым туннель работал всегда")
        XCTAssertEqual(AppSettings().core(), TunnelSettings().core(), "по умолчанию ядро получает то же, что и раньше")
        XCTAssertEqual(AppSettings().core(), CoreSettings())
    }

    func testOwnMtuIsUsedOnlyBetween1280And1500() {
        for mtu in [1280, 1360, 1420, 1500] {
            XCTAssertEqual(ConnectionSettings(mtu: mtu).tunMtu, mtu)
        }

        for mtu in [0, 576, 1279, 1501, 9000, -1] {
            XCTAssertEqual(ConnectionSettings(mtu: mtu).tunMtu, ConnectionSettings.autoMtu, "\(mtu)")
        }

        XCTAssertEqual(ConnectionSettings.mtuChoices, [nil, 1500, 1420, 1280], "порядок меню — как на доске")
    }

    func testMtuFieldTakesOnlyDigitsInTheRange() {
        XCTAssertEqual(ConnectionSettings.parseMtu("1420"), 1420)
        XCTAssertEqual(ConnectionSettings.parseMtu(" 1280 "), 1280)
        XCTAssertEqual(ConnectionSettings.parseMtu("1500"), 1500)
        for text in ["", "1279", "1501", "14 20", "1420.0", "-1400", "+1400", "01420", "abcd", "١٤٢٠", "9999"] {
            XCTAssertNil(ConnectionSettings.parseMtu(text), text)
        }
    }

    func testTimeoutIsTheProbeDeadline() {
        XCTAssertEqual(ConnectionSettings().probeTimeout, 10_000)
        for seconds in ConnectionSettings.timeoutChoices {
            XCTAssertEqual(ConnectionSettings(connectTimeout: seconds).probeTimeout, seconds * 1000)
        }

        XCTAssertEqual(ConnectionSettings.timeoutChoices, [10, 5, 20, 30], "порядок меню — как на доске, первый — по умолчанию")
        XCTAssertEqual(ConnectionSettings(connectTimeout: 0).probeTimeout, 10_000, "неверный — по умолчанию")
        XCTAssertEqual(ConnectionSettings(connectTimeout: 121).probeTimeout, 10_000)
        XCTAssertEqual(ReconnectTiming().probeTimeout, ConnectionSettings().probeTimeout, "по умолчанию — прежний срок пробы")
    }

    func testFingerprintsAndLogLevelsAreTheXrayValues() {
        XCTAssertEqual(TlsFingerprint.allCases.map(\.xray), [nil, "chrome", "firefox", "safari", "random"])
        XCTAssertEqual(CoreLogLevel.allCases.map(\.xray), ["warning", "info", "debug"])
    }

    func testEverythingTheCoreGetsReachesCoreSettingsAndThePlan() {
        let routing = RoutingSettings(mode: .split, rules: [SiteRule(pattern: "example.com", action: .direct)])
        let settings = AppSettings(
            tunnel: TunnelSettings(dns: .quad9Doh, ipv6: true),
            connection: ConnectionSettings(coreLog: .info, mtu: 1420, sniffing: false, fingerprint: .safari, mux: true, tlsFragment: true, connectTimeout: 20),
            routing: routing
        )
        let core = settings.core()
        XCTAssertEqual(core.dns, TunnelSettings.quad9Doh)
        XCTAssertTrue(core.ipv6)
        XCTAssertEqual(core.logLevel, .info)
        XCTAssertEqual(core.mtu, 1420)
        XCTAssertFalse(core.sniffing)
        XCTAssertEqual(core.fingerprint, "safari")
        XCTAssertTrue(core.mux)
        XCTAssertTrue(core.tlsFragment)
        XCTAssertEqual(core.siteRules, routing.activeRules)
        XCTAssertEqual(
            AppSettings(routing: RoutingSettings(mode: .allTraffic, rules: routing.rules)).core().siteRules, [],
            "«Напрямую» в режиме «Весь трафик» ядро не получает"
        )
        XCTAssertEqual(TunPlan.of(core).mtu, 1420, "MTU — и в план туннеля iOS")
        XCTAssertEqual(TunPlan.of(AppSettings(connection: ConnectionSettings(mtu: 9000)).core()).mtu, 1500)
    }

    func testOnlyWhatTheCoreGetsNeedsAReconnect() {
        let base = AppSettings()
        var timeout = base
        timeout.connection.connectTimeout = 30
        XCTAssertFalse(base.needsReconnect(timeout), "таймаут расширение читает само на каждой пробе")
        var reconnect = base
        reconnect.reconnect = .never
        XCTAssertFalse(base.needsReconnect(reconnect))
        var idle = base
        idle.routing = RoutingSettings(mode: .allTraffic, rules: [SiteRule(pattern: "example.com", action: .direct)])
        XCTAssertFalse(base.needsReconnect(idle), "«Напрямую» в режиме «Весь трафик» ядро не получает")
        var empty = base
        empty.routing.mode = .split
        XCTAssertFalse(base.needsReconnect(empty), "раздельный режим без правил «Напрямую» ничего в конфиге не меняет")

        let changes: [(inout AppSettings) -> Void] = [
            { $0.connection.mtu = 1280 }, { $0.connection.mux = true }, { $0.connection.tlsFragment = true }, { $0.connection.sniffing = false },
            { $0.connection.fingerprint = .chrome }, { $0.connection.coreLog = .debug },
            { $0.routing = RoutingSettings(mode: .split, rules: [SiteRule(pattern: "example.com", action: .direct)]) },
            { $0.routing.rules = [SiteRule(pattern: "example.com", action: .block)] },
        ]
        for change in changes {
            var next = base
            change(&next)
            XCTAssertTrue(base.needsReconnect(next), "\(next.connection) \(next.routing)")
        }
    }
}

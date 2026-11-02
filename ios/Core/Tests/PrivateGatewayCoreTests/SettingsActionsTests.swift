import XCTest
@testable import PrivateGatewayCore

/// Настройки под защитой — в работу сразу, если меняется то, что получает ядро или профиль VPN; без защиты и для того, что
/// ядро не получает, — только запись.
final class SettingsActionsTests: XCTestCase {
    private let control = FakeControl()
    private lazy var settings = SettingsStore(file: MemoryFile())
    private lazy var actions = SettingsActions(settings: settings, control: control)

    func testWhatTheCoreGetsReconnectsUnderProtection() async throws {
        try await actions.setTunnel(TunnelSettings(dns: .quad9Doh), state: .connected)
        XCTAssertEqual(control.connects, 1)
        try await actions.setTunnel(TunnelSettings(dns: .quad9Doh, ipv6: true), state: .reconnecting)
        XCTAssertEqual(control.connects, 2)
        let stored = try await settings.load().tunnel
        XCTAssertEqual(stored, TunnelSettings(dns: .quad9Doh, ipv6: true))
    }

    func testNothingTheCoreGetsChangesNothing() async throws {
        // «Из профиля» и «Cloudflare» — один и тот же DNS ядра; неверный свой адрес — тоже он.
        try await actions.setTunnel(TunnelSettings(dns: .cloudflareDoh), state: .connected)
        try await actions.setTunnel(TunnelSettings(dns: .custom, customDns: "dns.example.com"), state: .connected)
        XCTAssertEqual(control.connects, 0)
        try await actions.setSubscriptions(SubscriptionSettings(refresh: .off))
        XCTAssertEqual(control.connects, 0)
    }

    func testWithoutProtectionOnlyStores() async throws {
        try await actions.setTunnel(TunnelSettings(allowLan: true), state: .off)
        try await actions.setKillSwitch(false, state: .error)
        try await actions.setTunnel(TunnelSettings(ipv6: true), state: .stopping)
        XCTAssertEqual(control.connects, 0)
        let stored = try await settings.load()
        XCTAssertFalse(stored.killSwitch)
        XCTAssertTrue(stored.tunnel.ipv6)
    }

    func testKillSwitchAndLocalNetworkChangeTheProfile() async throws {
        try await actions.setKillSwitch(false, state: .connected)
        XCTAssertEqual(control.connects, 1)
        try await actions.setKillSwitch(false, state: .connected)
        XCTAssertEqual(control.connects, 1, "то же значение — профиль тот же")
        try await actions.setTunnel(TunnelSettings(allowLan: true), state: .connected)
        XCTAssertEqual(control.connects, 2)
    }

    func testConnectionAndRoutingTheCoreGetsReconnectUnderProtection() async throws {
        try await actions.setConnection(ConnectionSettings(mtu: 1280), state: .connected)
        XCTAssertEqual(control.connects, 1, "MTU — в план туннеля и в ядро")
        try await actions.setConnection(ConnectionSettings(mtu: 1280, tlsFragment: true), state: .reconnecting)
        XCTAssertEqual(control.connects, 2)
        let direct = [SiteRule(pattern: "example.com", action: .direct)]
        try await actions.setRouting(RoutingSettings(mode: .split, rules: direct), state: .connected)
        XCTAssertEqual(control.connects, 3, "«Напрямую» раздельного режима — в конфиг ядра")
        try await actions.setRouting(RoutingSettings(mode: .allTraffic, rules: direct), state: .connecting)
        XCTAssertEqual(control.connects, 4, "весь трафик — правило «Напрямую» уходит из конфига")
        let stored = try await settings.load()
        XCTAssertEqual(stored.connection, ConnectionSettings(mtu: 1280, tlsFragment: true))
        XCTAssertEqual(stored.routing.rules, [SiteRule(pattern: "example.com", action: .direct)])
    }

    func testWhatTheExtensionReadsItselfOrTheCoreDoesNotGetChangesNothing() async throws {
        // Таймаут расширение читает само на каждой пробе; «Напрямую» в режиме «Весь трафик» ядро не получает.
        try await actions.setConnection(ConnectionSettings(connectTimeout: 30), state: .connected)
        try await actions.setRouting(RoutingSettings(mode: .allTraffic, rules: [SiteRule(pattern: "example.com", action: .direct)]), state: .connected)
        XCTAssertEqual(control.connects, 0)
        let stored = try await settings.load()
        XCTAssertEqual(stored.connection.connectTimeout, 30)
        XCTAssertEqual(stored.routing.rules.count, 1, "правило хранится — действует, когда включат раздельный режим")
        try await actions.setRouting(RoutingSettings(mode: .split), state: .connected)
        XCTAssertEqual(control.connects, 0, "раздельный режим без правил «Напрямую» конфиг не меняет")

        try await actions.setRouting(RoutingSettings(mode: .split, rules: stored.routing.rules), state: .off)
        XCTAssertEqual(control.connects, 0, "без защиты — только запись")
    }

    func testAlwaysOnGoesToTheProfileUnderProtectionAndIsOnlyStoredWithout() async throws {
        try await actions.setAlwaysOn(true, state: .off)
        XCTAssertEqual(control.connects, 0, "без защиты — только запись: профиль получит ее следующее подключение")
        let stored = try await settings.load()
        XCTAssertTrue(stored.alwaysOn)

        try await actions.setAlwaysOn(false, state: .connected)
        XCTAssertEqual(control.connects, 1)
        try await actions.setAlwaysOn(false, state: .connected)
        XCTAssertEqual(control.connects, 1, "то же значение — профиль тот же")
    }
}

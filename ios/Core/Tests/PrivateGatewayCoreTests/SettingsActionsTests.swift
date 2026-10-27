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

import Foundation
import XCTest
@testable import PrivateGatewayCore

/// Файл не читается, хотя есть: данные под защитой до первой разблокировки.
private struct LockedFile: StateFile {
    struct Locked: Error {}

    func read() throws -> Data? {
        throw Locked()
    }

    func write(_ data: Data) throws {
        throw Locked()
    }
}

/// Настройки приложения — как `SettingsRepository` Android: по умолчанию — как решено для защиты, неизвестное значение из
/// будущей версии — значение по умолчанию, остальные остаются; приложение и расширение видят одно и то же.
final class AppSettingsTests: XCTestCase {
    private let file = MemoryFile()
    private lazy var store = SettingsStore(file: file)

    func testDefaultsWhenThereIsNoFile() async throws {
        let settings = try await store.load()
        XCTAssertNil(settings.selectedServerId)
        XCTAssertEqual(settings.tunnel, TunnelSettings(dns: .profile, customDns: nil, ipv6: false, allowLan: false))
        XCTAssertTrue(settings.killSwitch, "блок при обрыве включен по умолчанию")
        XCTAssertEqual(settings.subscriptions, SubscriptionSettings(refresh: .every12Hours, onlyViaVpn: true, notifySelected: true))
    }

    func testEveryChangeIsKeptAndSeenByAnotherReader() async throws {
        try await store.select("server-1")
        try await store.setTunnel(TunnelSettings(dns: .custom, customDns: "192.168.1.2", ipv6: true, allowLan: true))
        try await store.setKillSwitch(false)
        try await store.setSubscriptions(SubscriptionSettings(refresh: .every6Hours, onlyViaVpn: false, notifySelected: false))

        // Второй экземпляр на том же файле — как расширение туннеля.
        let other = try await SettingsStore(file: file).load()
        XCTAssertEqual(
            other,
            AppSettings(
                selectedServerId: "server-1", tunnel: TunnelSettings(dns: .custom, customDns: "192.168.1.2", ipv6: true, allowLan: true),
                killSwitch: false, subscriptions: SubscriptionSettings(refresh: .every6Hours, onlyViaVpn: false, notifySelected: false)
            )
        )

        try await store.select(nil)
        let cleared = try await store.load()
        XCTAssertNil(cleared.selectedServerId)
        XCTAssertFalse(cleared.killSwitch, "остальное не тронуто")
    }

    func testCustomDnsIsTrimmedAndEmptyIsNotKept() async throws {
        try await store.setTunnel(TunnelSettings(dns: .custom, customDns: "  1.1.1.1 "))
        let trimmed = try await store.load()
        XCTAssertEqual(trimmed.tunnel.customDns, "1.1.1.1")

        try await store.setTunnel(TunnelSettings(dns: .custom, customDns: "   "))
        let empty = try await store.load()
        XCTAssertNil(empty.tunnel.customDns)
    }

    func testFileKeysAreTheAndroidNames() async throws {
        try await store.select("server-1")
        let text = String(decoding: try XCTUnwrap(file.read()), as: UTF8.self)
        for key in ["selected_server", "tunnel_dns", "tunnel_ipv6", "tunnel_allow_lan", "kill_switch", "subs_refresh", "subs_only_vpn", "subs_notify_selected"] {
            XCTAssertTrue(text.contains("\"\(key)\""), key)
        }
    }

    func testUnknownOrWrongValuesFallBackOneByOne() async throws {
        try file.write(Data("""
        {"selected_server": "server-1", "tunnel_dns": "future", "tunnel_ipv6": true, "kill_switch": "yes",
         "subs_refresh": "every1Hour", "subs_only_vpn": false, "future_setting": 1}
        """.utf8))
        let settings = try await store.load()
        XCTAssertEqual(settings.selectedServerId, "server-1")
        XCTAssertEqual(settings.tunnel.dns, .profile)
        XCTAssertTrue(settings.tunnel.ipv6)
        XCTAssertTrue(settings.killSwitch)
        XCTAssertEqual(settings.subscriptions.refresh, .every12Hours)
        XCTAssertFalse(settings.subscriptions.onlyViaVpn)

        // Запись не теряет то, что прочиталось.
        try await store.setKillSwitch(false)
        let again = try await store.load()
        XCTAssertEqual(again.selectedServerId, "server-1")
        XCTAssertTrue(again.tunnel.ipv6)
    }

    func testBrokenFileGivesDefaults() async throws {
        try file.write(Data("не JSON".utf8))
        let broken = try await store.load()
        XCTAssertEqual(broken, AppSettings())

        try file.write(Data("[1, 2]".utf8))
        let array = try await store.load()
        XCTAssertEqual(array, AppSettings())
    }

    func testUnreadableFileIsAnErrorNotDefaults() async {
        // Иначе запись поверх стерла бы выбор пользователя.
        let locked = SettingsStore(file: LockedFile())
        do {
            _ = try await locked.load()
            XCTFail("файл не прочитать — ошибка")
        } catch {}

        do {
            try await locked.select("server-1")
            XCTFail("файл не прочитать — запись не идет")
        } catch {}
    }
}

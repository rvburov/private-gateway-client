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
        XCTAssertFalse(settings.alwaysOn, "постоянная защита выключена по умолчанию, как «Постоянная VPN» Android")
        XCTAssertFalse(settings.connectOnLaunch, "подключение при запуске выключено по умолчанию, как на десктопе")
        XCTAssertEqual(settings.subscriptions, SubscriptionSettings(refresh: .every12Hours, onlyViaVpn: true, notifySelected: true))
        XCTAssertEqual(settings.connection, ConnectionSettings(), "«Подключение и ядро» — как в макете")
        XCTAssertEqual(settings.routing, RoutingSettings(mode: .allTraffic, rules: []), "весь трафик через VPN, правил нет")
    }

    func testEveryChangeIsKeptAndSeenByAnotherReader() async throws {
        try await store.select("server-1")
        try await store.setTunnel(TunnelSettings(dns: .custom, customDns: "192.168.1.2", ipv6: true, allowLan: true))
        try await store.setKillSwitch(false)
        try await store.setSubscriptions(SubscriptionSettings(refresh: .every6Hours, onlyViaVpn: false, notifySelected: false))
        try await store.setAlwaysOn(true)
        try await store.setConnectOnLaunch(true)
        let connection = ConnectionSettings(coreLog: .debug, mtu: 1360, sniffing: false, fingerprint: .firefox, mux: true, tlsFragment: true, connectTimeout: 30)
        try await store.setConnection(connection)
        let routing = RoutingSettings(mode: .split, rules: [
            SiteRule(pattern: "example.com", action: .direct), SiteRule(pattern: "*.local", action: .direct), SiteRule(pattern: "198.51.100.0/24", action: .block),
            SiteRule(pattern: "example.org", action: .proxy),
        ])
        try await store.setRouting(routing)

        // Второй экземпляр на том же файле — как расширение туннеля.
        let other = try await SettingsStore(file: file).load()
        XCTAssertEqual(
            other,
            AppSettings(
                selectedServerId: "server-1", tunnel: TunnelSettings(dns: .custom, customDns: "192.168.1.2", ipv6: true, allowLan: true),
                killSwitch: false, subscriptions: SubscriptionSettings(refresh: .every6Hours, onlyViaVpn: false, notifySelected: false), alwaysOn: true,
                connectOnLaunch: true, connection: connection, routing: routing
            )
        )

        try await store.select(nil)
        let cleared = try await store.load()
        XCTAssertNil(cleared.selectedServerId)
        XCTAssertFalse(cleared.killSwitch, "остальное не тронуто")
        XCTAssertTrue(cleared.connectOnLaunch)

        try await store.setConnectOnLaunch(false)
        let launch = try await store.load()
        XCTAssertFalse(launch.connectOnLaunch)
        XCTAssertTrue(launch.alwaysOn, "подключение при запуске — отдельно от постоянной защиты")
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
        for key in ["selected_server", "tunnel_dns", "tunnel_ipv6", "tunnel_allow_lan", "kill_switch", "subs_refresh", "subs_only_vpn", "subs_notify_selected",
                    "notify_level", "reconnect_policy", "always_on", "connect_on_launch"] {
            XCTAssertTrue(text.contains("\"\(key)\""), key)
        }
    }

    func testUnknownOrWrongValuesFallBackOneByOne() async throws {
        try file.write(Data("""
        {"selected_server": "server-1", "tunnel_dns": "future", "tunnel_ipv6": true, "kill_switch": "yes",
         "subs_refresh": "every1Hour", "subs_only_vpn": false, "future_setting": 1, "notify_level": "Silent", "reconnect_policy": "UpTo5Minutes",
         "connect_on_launch": "yes", "core_log": "Verbose", "tunnel_mtu": 9000, "tunnel_sniffing": false, "conn_fingerprint": "Edge", "conn_mux": "on",
         "conn_tls_fragment": true, "conn_timeout": 0, "routing_mode": "Future",
         "routing_rules": "[{\\"pattern\\":\\"geosite:category-ads\\",\\"action\\":\\"Block\\"},{\\"pattern\\":\\" Example.COM \\",\\"action\\":\\"Direct\\"},{\\"pattern\\":\\"example.org\\",\\"action\\":\\"Teleport\\"},{\\"action\\":\\"Block\\"},{\\"pattern\\":\\"example.com\\",\\"action\\":\\"Block\\"}]"}
        """.utf8))
        let settings = try await store.load()
        XCTAssertEqual(settings.selectedServerId, "server-1")
        XCTAssertEqual(settings.tunnel.dns, .profile)
        XCTAssertTrue(settings.tunnel.ipv6)
        XCTAssertTrue(settings.killSwitch)
        XCTAssertEqual(settings.subscriptions.refresh, .every12Hours)
        XCTAssertFalse(settings.subscriptions.onlyViaVpn)
        XCTAssertEqual(settings.notifications, .importantOnly, "уровень из будущей версии — по умолчанию")
        XCTAssertEqual(settings.reconnect, .upTo5Minutes)
        XCTAssertFalse(settings.connectOnLaunch, "не тот тип — по умолчанию")
        XCTAssertEqual(
            settings.connection, ConnectionSettings(sniffing: false, tlsFragment: true),
            "журнал, отпечаток, MTU вне 1280–1500, Mux не того типа и таймаут 0 — по умолчанию, остальное — прочитано"
        )
        XCTAssertEqual(settings.routing.mode, .allTraffic, "режим из будущей версии — весь трафик")
        XCTAssertEqual(
            settings.routing.rules, [SiteRule(pattern: "example.com", action: .direct)],
            "geosite, неизвестное действие, правило без шаблона и второе правило того же сайта — пропущены"
        )

        // Запись не теряет то, что прочиталось.
        try await store.setKillSwitch(false)
        let again = try await store.load()
        XCTAssertEqual(again.selectedServerId, "server-1")
        XCTAssertTrue(again.tunnel.ipv6)
    }

    func testConnectionAndRoutingAreKeptUnderTheirKeys() async throws {
        try await store.setConnection(ConnectionSettings(mtu: 1420))
        try await store.setRouting(RoutingSettings(mode: .split, rules: [SiteRule(pattern: "example.com", action: .direct)]))
        let text = String(decoding: try XCTUnwrap(file.read()), as: UTF8.self)
        // Имена ключей и значения — как в DataStore Android (SettingsRepository): правила — текстом JSON.
        for key in ["core_log", "tunnel_mtu", "tunnel_sniffing", "conn_fingerprint", "conn_mux", "conn_tls_fragment", "conn_timeout", "routing_mode", "routing_rules"] {
            XCTAssertTrue(text.contains("\"\(key)\""), key)
        }

        let object = try XCTUnwrap(JSONSerialization.jsonObject(with: try XCTUnwrap(file.read())) as? [String: Any])
        XCTAssertEqual(object["routing_mode"] as? String, "Split")
        XCTAssertEqual(object["tunnel_mtu"] as? Int, 1420)
        XCTAssertEqual(object["routing_rules"] as? String, #"[{"pattern":"example.com","action":"Direct"}]"#)

        // «Авто» — ключа MTU нет вовсе.
        try await store.setConnection(ConnectionSettings())
        XCTAssertFalse(String(decoding: try XCTUnwrap(file.read()), as: UTF8.self).contains("tunnel_mtu"))
    }

    func testWrongConnectionAndRulesAreNotKept() async throws {
        try await store.setConnection(ConnectionSettings(mtu: 9000, connectTimeout: 500))
        let connection = try await store.load().connection
        XCTAssertNil(connection.mtu, "MTU вне 1280–1500 — «Авто»")
        XCTAssertEqual(connection.connectTimeout, ConnectionSettings.defaultTimeout)

        try await store.setRouting(RoutingSettings(mode: .split, rules: [
            SiteRule(pattern: "geosite:category-ads", action: .block), SiteRule(pattern: "Example.com", action: .direct),
            SiteRule(pattern: "example.com", action: .block),
        ]))
        let routing = try await store.load().routing
        XCTAssertEqual(routing, RoutingSettings(mode: .split, rules: [SiteRule(pattern: "example.com", action: .direct)]))
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

    /// Настройки в резервной копии (решение 559): строками, без выбранного сервера; обратно — в свои типы, выбранный не меняется.
    func testSettingsGoIntoTheBackupAsTextAndComeBackTyped() {
        let saved = AppSettings(
            selectedServerId: "srv-1", tunnel: TunnelSettings(dns: .custom, customDns: "192.0.2.53", ipv6: true), killSwitch: false,
            connectOnLaunch: true, connection: ConnectionSettings(mtu: 1360, connectTimeout: 15)
        )
        let values = saved.backupValues()
        XCTAssertNil(values["selected_server"], "выбранный сервер сменится вместе со списком")
        XCTAssertEqual(values["tunnel_dns"], "custom")
        XCTAssertEqual(values["kill_switch"], "false")
        XCTAssertEqual(values["tunnel_mtu"], "1360")
        XCTAssertEqual(values["connect_on_launch"], "true")

        let restored = AppSettings(selectedServerId: "srv-2").restoring(values)
        XCTAssertEqual(restored.selectedServerId, "srv-2")
        XCTAssertEqual(restored.tunnel, saved.tunnel)
        XCTAssertFalse(restored.killSwitch)
        XCTAssertTrue(restored.connectOnLaunch)
        XCTAssertEqual(restored.connection.mtu, 1360)
        XCTAssertEqual(restored.connection.connectTimeout, 15)

        // Чужое и испорченное — значение по умолчанию, остальное остается.
        let broken = AppSettings().restoring(["kill_switch": "может быть", "tunnel_mtu": "много", "unknown": "x"])
        XCTAssertTrue(broken.killSwitch)
        XCTAssertNil(broken.connection.mtu)
    }
}

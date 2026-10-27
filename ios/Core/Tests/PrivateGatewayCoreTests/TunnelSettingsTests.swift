import XCTest
@testable import PrivateGatewayCore

/// Настройки туннеля и свой DNS — те же случаи, что `TunnelSettingsTest` Android: свой DNS только по адресу, адрес в частной
/// сети спрашивается напрямую, переподключение — только ради того, что получает ядро.
final class TunnelSettingsTests: XCTestCase {
    func testDefaultsCloseTheLocalNetworkAndIpv6AndUseDohByAddress() {
        let core = TunnelSettings().core()
        XCTAssertEqual(core.dns, XrayConfig.defaultDns)
        XCTAssertFalse(core.allowLan)
        XCTAssertFalse(core.ipv6)
    }

    func testChoicesMapToCoreSettings() {
        XCTAssertEqual(TunnelSettings(dns: .quad9Doh).core().dns, TunnelSettings.quad9Doh)
        XCTAssertEqual(TunnelSettings(dns: .custom, customDns: " 192.0.2.53 ").core().dns, "192.0.2.53")
        XCTAssertEqual(TunnelSettings(dns: .custom, customDns: "dns.example.com").core().dns, XrayConfig.defaultDns)
        let open = TunnelSettings(ipv6: true, allowLan: true).core()
        XCTAssertTrue(open.ipv6 && open.allowLan)
    }

    func testCustomDnsByAddressIsAccepted() {
        let accepted = [
            "192.0.2.53", "2001:db8::53", "https://198.51.100.1/dns-query", "https://[2001:db8::1]/dns-query", "https://203.0.113.5:8443/q",
            " 2001:DB8::53 ", "::ffff:192.0.2.53", "HTTPS://198.51.100.1/dns-query", "https://198.51.100.1", "https://[2001:db8::1]:443/q",
            "https://203.0.113.5:65535/q",
        ]
        for value in accepted {
            XCTAssertTrue(TunnelSettings.validCustomDns(value), value)
        }
    }

    func testCustomDnsByNameOrBadAddressIsRefused() {
        let refused = [
            "dns.example.com", "https://dns.example.com/dns-query", "http://192.0.2.53/dns-query", "256.1.1.1", "", "tls://192.0.2.53",
            "::::", "1::2::3", ":::", "1.2", "1:2:3:4:5:6:7", "fe80::1%wlan0", "https://[::::]/dns-query", "https://[1::2::3]/q",
            "https://[2001:db8::1/dns-query", "https://[2001:db8::1]x/q", "https://2001:db8::1/dns-query", "https://[192.0.2.53]/q",
            "https://203.0.113.5:/q", "https://203.0.113.5:0/q", "https://203.0.113.5:65536/q", "https://203.0.113.5:84a3/q",
            "https://user@203.0.113.5/q", "https://", "010.0.0.1", "1:2:3:4:5:6:7:8:9", "12345::1", "١٢٣.0.0.1",
        ]
        for value in refused {
            XCTAssertFalse(TunnelSettings.validCustomDns(value), value)
        }
    }

    func testAddressesParseToTheirBytes() {
        XCTAssertEqual(IpAddress.parse("192.0.2.1"), [192, 0, 2, 1])
        XCTAssertEqual(IpAddress.parse("::"), Array(repeating: 0, count: 16))
        XCTAssertEqual(IpAddress.parse("::ffff:192.0.2.1"), Array(repeating: 0, count: 10) + [0xFF, 0xFF, 192, 0, 2, 1])
        XCTAssertEqual(IpAddress.parse("2001:db8::1"), [0x20, 0x01, 0x0D, 0xB8] + Array(repeating: 0, count: 11) + [1])
        XCTAssertEqual(IpAddress.parse("1:2:3:4:5:6:7:8")?.count, 16)
        XCTAssertNil(IpAddress.parse("1:2:3:4:5:6:7::8"), "«::» заменяет хотя бы одну группу, а их уже восемь")
    }

    func testPrivateNetworksAreTheDesktopOnes() {
        for address in ["10.1.2.3", "172.16.0.1", "172.31.255.254", "192.168.1.53", "169.254.1.1", "fd00::53", "fe80::1", "::ffff:192.168.1.53"] {
            XCTAssertTrue(PrivateNetwork.contains(address), address)
        }

        for address in ["172.32.0.1", "192.0.2.1", "9.9.9.9", "2001:db8::1", "fec0::1", "not an address"] {
            XCTAssertFalse(PrivateNetwork.contains(address), address)
        }
    }

    func testOwnDnsInAPrivateNetworkIsOnlyACustomIpAddress() {
        var custom = TunnelSettings(dns: .custom)
        XCTAssertNil(custom.lanDns)
        custom.customDns = " 192.168.1.53 "
        XCTAssertEqual(custom.lanDns, "192.168.1.53")
        custom.customDns = "fd00::53"
        XCTAssertEqual(custom.lanDns, "fd00::53")
        custom.customDns = "192.168.1.53"
        XCTAssertEqual(custom.core().lanDns, "192.168.1.53", "ядро получает адрес для правила")
        custom.customDns = "9.9.9.9"
        XCTAssertNil(custom.lanDns, "адрес в интернете — через сервер")
        custom.customDns = "https://192.168.1.53/dns-query"
        XCTAssertNil(custom.lanDns, "DoH — по порту 443")
        custom.customDns = "192.168.1"
        XCTAssertNil(custom.lanDns, "не адрес")
        XCTAssertNil(TunnelSettings(dns: .cloudflareDoh, customDns: "192.168.1.53").lanDns, "свой адрес не выбран")
        XCTAssertNil(TunnelSettings(dns: .profile, customDns: "192.168.1.53").core().lanDns)
    }

    func testOnlyWhatTheCoreGetsNeedsAReconnect() {
        let base = TunnelSettings()
        for next in [
            TunnelSettings(dns: .quad9Doh), TunnelSettings(dns: .custom, customDns: "192.0.2.53"), TunnelSettings(ipv6: true),
            TunnelSettings(allowLan: true),
        ] {
            XCTAssertTrue(base.needsReconnect(next), "ядро получает: \(next)")
        }

        let own = TunnelSettings(dns: .custom, customDns: "192.0.2.53")
        XCTAssertTrue(own.needsReconnect(TunnelSettings(dns: .custom, customDns: "198.51.100.53")), "другой свой адрес")
        XCTAssertTrue(
            TunnelSettings(dns: .custom, customDns: "192.168.1.53").needsReconnect(TunnelSettings(dns: .custom, customDns: "192.168.1.54")),
            "другой свой адрес в частной сети"
        )

        XCTAssertFalse(base.needsReconnect(base))
        XCTAssertFalse(base.needsReconnect(TunnelSettings(dns: .cloudflareDoh)), "«Из профиля» — тот же DoH Cloudflare")
        XCTAssertFalse(base.needsReconnect(TunnelSettings(dns: .custom, customDns: "::::")), "неверный свой адрес равен «Из профиля»")
        XCTAssertFalse(base.needsReconnect(TunnelSettings(dns: .custom)), "своего адреса нет — DNS по умолчанию")
        XCTAssertFalse(base.needsReconnect(TunnelSettings(customDns: "192.0.2.53")), "свой адрес не выбран")
        XCTAssertFalse(own.needsReconnect(TunnelSettings(dns: .custom, customDns: " 192.0.2.53 ")), "тот же адрес с пробелами")
    }
}

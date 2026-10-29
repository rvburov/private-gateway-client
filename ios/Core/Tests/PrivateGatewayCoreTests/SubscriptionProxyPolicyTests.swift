import XCTest
@testable import PrivateGatewayCore

/// Политика защищенной подписки следует точным сетям правила Xray AllowLan, включая границы и IPv4-mapped IPv6.
/// Проверка работает только с байтами адреса: запроса DNS для выяснения того, можно ли делать запрос DNS, здесь нет.
final class SubscriptionProxyPolicyTests: XCTestCase {
    private let local = [
        "0.0.0.0", "0.9.8.7", "10.1.2.3", "100.64.0.1", "100.127.255.255", "127.0.0.53", "169.254.1.2",
        "172.16.0.1", "172.31.255.255", "192.0.0.1", "192.0.2.1", "192.88.99.3", "192.168.0.1",
        "198.18.0.1", "198.19.255.255", "198.51.100.1", "203.0.113.1", "224.0.0.1", "239.255.255.255",
        "240.0.0.1", "255.255.255.255", "::", "::1", "fc00::53", "fdff::1", "fe80::53", "febf::1", "ff02::53",
        "::ffff:127.0.0.53", "::ffff:100.64.0.53", "::ffff:192.168.1.53",
    ]
    private let outside = [
        "1.1.1.1", "9.9.9.9", "100.63.255.255", "100.128.0.1", "126.255.255.255", "128.0.0.1",
        "169.253.255.255", "169.255.0.1", "172.15.255.255", "172.32.0.1", "192.167.255.255", "192.169.0.1",
        "198.17.255.255", "198.20.0.1", "2001:4860:4860::8888", "2001:db8::53", "fbff::53", "fe00::53",
        "fe7f::1", "fec0::1", "::2", "::ffff:9.9.9.9",
    ]

    private func doh(_ address: String) -> String {
        "https://" + (address.contains(":") ? "[\(address)]" : address) + ":443/dns-query"
    }

    func testWireGuardRejectsNumericAndDohDnsInEveryAllowLanRange() {
        for address in local {
            for dns in [address, doh(address)] {
                XCTAssertFalse(SubscriptionProxy.isAllowed(protocol: .wireGuard, settings: CoreSettings(dns: dns, allowLan: true)), dns)
            }
        }
    }

    func testPublicDnsAndAddressesImmediatelyOutsideTheExactXrayRangesStayAvailable() {
        for address in outside {
            for dns in [address, doh(address)] {
                XCTAssertTrue(SubscriptionProxy.isAllowed(protocol: .wireGuard, settings: CoreSettings(dns: dns, allowLan: true)), dns)
            }
        }
    }

    func testClosedLanDoesNotInventADirectExceptionButExplicitLanDnsAlwaysRefuses() {
        for address in local {
            XCTAssertTrue(SubscriptionProxy.isAllowed(protocol: .wireGuard, settings: CoreSettings(dns: address)), address)
        }
        for allowLan in [false, true] {
            let settings = CoreSettings(dns: XrayConfig.defaultDns, allowLan: allowLan, lanDns: "192.168.1.53")
            XCTAssertFalse(SubscriptionProxy.isAllowed(protocol: .wireGuard, settings: settings))
        }
    }

    func testDnsHostnamesAndUnknownFormatsFailClosedWithAllowedLanWithoutAnAddressLookup() {
        for dns in ["dns.example.com", "localhost", "https://dns.example.com/dns-query", "https+local://1.1.1.1/dns-query", "", "1.2", "010.0.0.1"] {
            XCTAssertFalse(SubscriptionProxy.isAllowed(protocol: .wireGuard, settings: CoreSettings(dns: dns, allowLan: true)), dns)
            XCTAssertTrue(SubscriptionProxy.isAllowed(protocol: .wireGuard, settings: CoreSettings(dns: dns)), "LAN закрыта, прямого DNS-исключения нет: \(dns)")
        }
    }

    func testProtocolsThatSendTheDestinationNameToTheServerAreNotRestricted() {
        for proto in [ProxyProtocol.vless, .vmess, .trojan, .shadowsocks] {
            for dns in local + ["https://dns.example.com/dns-query"] {
                XCTAssertTrue(SubscriptionProxy.isAllowed(protocol: proto, settings: CoreSettings(dns: dns, allowLan: true, lanDns: "192.168.1.53")))
            }
        }
    }
}

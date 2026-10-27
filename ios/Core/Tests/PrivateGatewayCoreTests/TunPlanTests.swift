import XCTest
@testable import PrivateGatewayCore

/// План туннеля — те же адреса, что `TunPlanTest` Android, но IPv6 в туннель идет всегда: iOS, в отличие от Android, сама
/// IPv6 не закрывает (решение 459).
final class TunPlanTests: XCTestCase {
    func testWholeInternetGoesIntoTheTunnel() {
        let plan = TunPlan.of(CoreSettings())
        XCTAssertEqual(plan.ipv4.description, "172.19.0.1/30")
        XCTAssertEqual(plan.ipv6.description, "fd66:7067:6763::1/126")
        XCTAssertEqual(plan.routes.map(\.description), ["0.0.0.0/0", "::/0"])
        XCTAssertEqual(plan.dnsServers, ["172.19.0.2"])
        XCTAssertEqual(plan.dnsMatchDomains, [""], "все имена — к DNS туннеля")
        XCTAssertEqual(plan.mtu, 1500)
    }

    func testIpv6IsRoutedIntoTheTunnelWithAndWithoutTheSetting() {
        // Без настройки ядро отправляет IPv6 в блок, но мимо туннеля он не уходит.
        XCTAssertEqual(TunPlan.of(CoreSettings(ipv6: false)), TunPlan.of(CoreSettings(ipv6: true)))
        XCTAssertTrue(TunPlan.of(CoreSettings(ipv6: false)).routes.contains(TunPrefix("::", 0)))
    }

    func testMtuComesFromSettings() {
        XCTAssertEqual(TunPlan.of(CoreSettings(mtu: 1280)).mtu, 1280)
    }

    func testTunnelDnsLiesInTheTunnelSubnet() throws {
        // DNS туннеля — второй адрес той же /30: запросы к нему идут в интерфейс туннеля, а не в сеть телефона.
        let plan = TunPlan.of(CoreSettings())
        let own = try XCTUnwrap(IpAddress.parse(plan.ipv4.address))
        let dns = try XCTUnwrap(IpAddress.parse(plan.dnsServers[0]))
        let mask = TunPlan.subnetMask(plan.ipv4.length).split(separator: ".").map { UInt8($0)! }
        XCTAssertEqual(zip(own, mask).map { $0 & $1 }, zip(dns, mask).map { $0 & $1 })
        XCTAssertNotEqual(own, dns)
    }

    func testSubnetMask() {
        XCTAssertEqual(TunPlan.subnetMask(30), "255.255.255.252")
        XCTAssertEqual(TunPlan.subnetMask(24), "255.255.255.0")
        XCTAssertEqual(TunPlan.subnetMask(1), "128.0.0.0")
        XCTAssertEqual(TunPlan.subnetMask(0), "0.0.0.0")
        XCTAssertEqual(TunPlan.subnetMask(32), "255.255.255.255")
    }

    func testRemoteAddressIsNotTheServer() {
        // Адрес сервера в настройки iOS не попадает — он запечатан в Связке ключей.
        XCTAssertEqual(TunPlan.remoteAddress, "127.0.0.1")
    }
}

import Foundation
import XCTest
@testable import PrivateGatewayCore

final class LocalFilterAndHistoryTests: XCTestCase {
    func testOnlyActualControlledBlockRecordsAreAccepted() {
        let history = BlockedSiteHistory()
        let now = Date()
        history.receive("[Info] app/dispatcher: taking detour [block] for [tcp:blocked.example:443]", now: now)
        history.receive("[Info] app/dispatcher: taking detour [block] for [udp:blocked.example:443]", now: now)
        history.receive("[Info] app/dispatcher: taking detour [proxy] for [tcp:allowed.example:443]", now: now)
        history.receive("from 127.0.0.1:1234 rejected tcp:rejected.example:443 [tun-in -> proxy]", now: now)
        history.receive("[Info] app/dispatcher: taking detour [block] for [tcp:user@secret.example:443]", now: now)
        history.receive("[Info] app/dispatcher: taking detour [block] for [tcp:secret.example:443/private]", now: now)
        history.receive("[Info] app/dispatcher: taking detour [block] for [tcp:198.18.0.2:443]", now: now)
        let events = history.snapshot(now: now)
        XCTAssertEqual(events.map(\.target), ["blocked.example"])
        XCTAssertEqual(events.first?.count, 2)
        XCTAssertTrue(events.first?.canAllow == true)
    }

    func testHistoryIsBoundedTodayOnlyAndSharedIpIsNotQuickAllowed() {
        let history = BlockedSiteHistory()
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(secondsFromGMT: 0)!
        let yesterday = Date(timeIntervalSince1970: 1_700_000_000)
        let now = yesterday.addingTimeInterval(86_400)
        history.receive("[Info] app/dispatcher: taking detour [block] for [tcp:yesterday.example:443]", now: yesterday, calendar: calendar)
        for index in 0...BlockedSiteHistory.limit {
            history.receive("[Info] app/dispatcher: taking detour [block] for [tcp:site\(index).example:443]", now: now, calendar: calendar)
        }
        XCTAssertEqual(history.snapshot(now: now, calendar: calendar).count, BlockedSiteHistory.limit)
        XCTAssertFalse(history.snapshot(now: now, calendar: calendar).contains { $0.target == "yesterday.example" })
        history.receive("[Info] app/dispatcher: taking detour [block] for [tcp:192.0.2.10:443]", now: now, calendar: calendar)
        XCTAssertFalse(history.snapshot(now: now, calendar: calendar).first { $0.target == "192.0.2.10" }?.canAllow ?? true)
        history.clear()
        XCTAssertEqual(history.snapshot(now: now, calendar: calendar), [])
    }

    func testPostSniffDetourUsesTheConfirmedDomainWithoutDnsCorrelation() {
        let plain = "2026/11/23 00:00:00 [Info] [1234] app/dispatcher: taking detour [block] for [tcp:blocked.example:443]"
        let tagged = "[Info] app/dispatcher: Hit route rule: [site-rule] so taking detour [block] for [udp:blocked.example:443]"
        XCTAssertEqual(BlockedSiteHistory.target(plain), "blocked.example")
        XCTAssertEqual(BlockedSiteHistory.target(tagged), "blocked.example")
        XCTAssertNil(BlockedSiteHistory.target("[Info] app/dispatcher: taking detour [proxy] for [tcp:allowed.example:443]"))
        XCTAssertNil(BlockedSiteHistory.target("[Info] app/dispatcher: sniffed domain: unknown.example"))
    }

    func testQuotedDetourInWarningsCannotBecomeActualBlockedHistory() {
        let actual = "[Info] app/dispatcher: taking detour [block] for [tcp:blocked.example:443]"
        XCTAssertEqual(BlockedSiteHistory.target(actual), "blocked.example")
        XCTAssertEqual(BlockedSiteHistory.target("2026/11/23 00:00:00.123456 " + actual), "blocked.example")
        for quoted in [
            "[Warning] app/dispatcher: taking detour [block] for [tcp:blocked.example:443]",
            "[Warning] invalid header value: " + actual,
            "[Info] transport/internet/splithttp: warning quoted " + actual,
            "prefix " + actual,
            actual + " quoted trailer",
            "from 127.0.0.1:1234 accepted tcp:blocked.example:443 [tun-in -> block]",
        ] {
            XCTAssertNil(BlockedSiteHistory.target(quoted))
            let history = BlockedSiteHistory()
            history.receive(quoted)
            XCTAssertTrue(history.snapshot().isEmpty)
        }
    }

    func testLocalFilterNeverCreatesARemoteOrSubscriptionOutbound() throws {
        let core = CoreSettings(siteRules: [SiteRule(pattern: "blocked.example", action: .block), SiteRule(pattern: "allowed.example", action: .proxy)],
                                blockOthers: true, fakeDns: true)
        let json = XrayConfig.buildLocal(core, tunFd: 17)
        let config = try object(json)
        let outbounds = try XCTUnwrap(config["outbounds"] as? [[String: Any]])
        XCTAssertEqual(outbounds.compactMap { $0["protocol"] as? String }, ["freedom", "blackhole", "dns"])
        let inbounds = try XCTUnwrap(config["inbounds"] as? [[String: Any]])
        XCTAssertEqual(inbounds.compactMap { $0["tag"] as? String }, [XrayConfig.tunTag])
        XCTAssertFalse(json.contains("\"vnext\""))
        XCTAssertFalse(json.contains("\"accounts\""))
        let rules = try rules(config)
        XCTAssertFalse(rules.contains { $0["outboundTag"] as? String == XrayConfig.proxyTag })
        XCTAssertTrue(rules.contains { ($0["domain"] as? [String])?.contains("domain:blocked.example") == true && $0["outboundTag"] as? String == XrayConfig.blockTag })
        XCTAssertFalse(rules.contains { ($0["ip"] as? [String]) == [TunnelProbe.host + "/32"] && $0["port"] as? String == String(TunnelProbe.port) })
    }

    func testBlockOthersPermitsOnlyTheExactHealthDestinationThroughVpn() throws {
        let server = try XCTUnwrap(LinkParser.parse("vless://11111111-2222-3333-4444-555555555555@vpn.example:443?security=tls&sni=vpn.example&type=tcp&encryption=none").server)
        let config = try object(XCTUnwrap(XrayConfig.build(server, CoreSettings(blockOthers: true), tunFd: 0).json))
        let all = try rules(config)
        let health = try XCTUnwrap(all.first { ($0["ip"] as? [String]) == [TunnelProbe.host + "/32"] })
        XCTAssertEqual(health["inboundTag"] as? [String], [XrayConfig.tunTag])
        XCTAssertEqual(health["network"] as? String, "tcp")
        XCTAssertEqual(health["port"] as? String, String(TunnelProbe.port))
        XCTAssertEqual(health["outboundTag"] as? String, XrayConfig.proxyTag)
        XCTAssertTrue(all.contains { ($0["ip"] as? [String]) == ["::/0"] && $0["outboundTag"] as? String == XrayConfig.blockTag })
    }

    func testNewAllowCannotReplaceOrOutrankABlockedRuleWithoutPin() {
        let blocked = SiteRule(pattern: "example.org", action: .block)
        let routing = RoutingSettings(rules: [blocked])
        XCTAssertEqual(BlockGuard.forSave(original: nil, rule: SiteRule(pattern: "example.org", action: .proxy), routing: routing), .rule("example.org"))
        XCTAssertEqual(BlockGuard.forSave(original: nil, rule: SiteRule(pattern: "sub.example.org", action: .proxy, exact: true), routing: routing), .allow("sub.example.org"))
        XCTAssertNil(BlockGuard.forSave(original: blocked, rule: blocked, routing: routing))
    }

    func testLocalFilterAndOfflineStatusSurviveTheSystemView() {
        let report = TunnelStatus(state: .connected, offline: true, localSiteFilter: true)
        let view = TunnelView.status(system: .connected, report: report)
        XCTAssertTrue(view.localSiteFilter)
        XCTAssertTrue(view.offline)
        XCTAssertFalse(TunnelView.status(system: .disconnected, report: report).localSiteFilter)
        let home = HomeState(status: report, servers: [ServerItem(id: "s", linkName: "saved", countryCode: "", protocol: .vless,
                                                                security: .tls, transport: .tcp, port: 443)], selectedId: "s")
        XCTAssertNil(home.inUse)
    }

    private func object(_ json: String) throws -> [String: Any] {
        try XCTUnwrap(JSONSerialization.jsonObject(with: Data(json.utf8)) as? [String: Any])
    }

    private func rules(_ config: [String: Any]) throws -> [[String: Any]] {
        let routing = try XCTUnwrap(config["routing"] as? [String: Any])
        return try XCTUnwrap(routing["rules"] as? [[String: Any]])
    }
}

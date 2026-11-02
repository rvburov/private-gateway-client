import XCTest
@testable import PrivateGatewayCore

/// «Маршрутизация» телефона — те же правила, что `RoutingSettings` и `SitePattern` десктопа, без geosite: шаблон приводится к
/// обычному виду, правило для того же сайта заменяется, «Напрямую» действует только в раздельном режиме.
final class RoutingSettingsTests: XCTestCase {
    func testSitePatternIsNormalizedLikeOnTheDesktop() {
        let cases: [(String, String)] = [
            ("example.com", "example.com"), (" Example.COM ", "example.com"), ("*.Local", "*.local"), ("*.example.org", "*.example.org"),
            ("https://www.example.org/path?q=1", "www.example.org"), ("http://example.com", "example.com"), ("ads-1.example.org", "ads-1.example.org"),
            ("203.0.113.5", "203.0.113.5"), ("198.51.100.77/24", "198.51.100.0/24"), ("192.0.2.1/32", "192.0.2.1/32"), ("10.1.2.3/8", "10.0.0.0/8"),
            ("2001:DB8::1", "2001:db8::1"), ("2001:db8:1:2:3:4:5:6/32", "2001:db8::/32"), ("2001:0db8:0000::0001", "2001:db8::1"),
        ]
        for (text, expected) in cases {
            XCTAssertEqual(SitePattern.normalize(text), expected, text)
        }
    }

    func testBadPatternsAndGeositeAreRefused() {
        let refused = [
            "", "   ", "localhost", "example..com", ".example.com", "-example.com", "example-.com", "example.com.", "1.2.3", "example.123",
            "*.", "*", "203.0.113.5/0", "203.0.113.5/33", "203.0.113.5/", "203.0.113.5/2a", "2001:db8::/129", "fe80::1%en0",
            "пример.рф", "exa mple.com", "example.com:8080", "ftp://example.com", "https://203.0.113.5/x", "010.0.0.1",
            "geosite:category-ads", "GEOSITE:ru", String(repeating: "a", count: 64) + ".com",
            String(repeating: "abcdefgh.", count: 28) + "com",
        ]
        for text in refused {
            XCTAssertNil(SitePattern.normalize(text), text)
        }
    }

    func testAddressesAreIpConditionsAndNamesAreDomainsWithSubdomains() {
        XCTAssertTrue(SitePattern.isAddress("203.0.113.5"))
        XCTAssertTrue(SitePattern.isAddress("198.51.100.0/24"))
        XCTAssertTrue(SitePattern.isAddress("2001:db8::/32"))
        XCTAssertFalse(SitePattern.isAddress("example.com"))
        XCTAssertFalse(SitePattern.isAddress("*.local"))
        XCTAssertEqual(SitePattern.domainCondition("example.com"), "domain:example.com")
        XCTAssertEqual(SitePattern.domainCondition("*.example.com"), "domain:example.com", "«*.» — тот же домен с поддоменами")
    }

    func testIpv6IsWrittenTheShortWay() throws {
        XCTAssertEqual(IpAddress.format([192, 0, 2, 1]), "192.0.2.1")
        XCTAssertEqual(IpAddress.format(Array(repeating: 0, count: 16)), "::")
        XCTAssertEqual(IpAddress.format(Array(repeating: 0, count: 15) + [1]), "::1")
        XCTAssertEqual(IpAddress.format(try XCTUnwrap(IpAddress.parse("2001:db8::"))), "2001:db8::")
        XCTAssertEqual(IpAddress.format(try XCTUnwrap(IpAddress.parse("2001:db8:0:1:0:0:0:1"))), "2001:db8:0:1::1", "сжимается самая длинная серия")
        XCTAssertEqual(IpAddress.format(try XCTUnwrap(IpAddress.parse("2001:db8:0:0:1:0:0:1"))), "2001:db8::1:0:0:1", "из равных — первая")
        XCTAssertEqual(IpAddress.format(try XCTUnwrap(IpAddress.parse("2001:db8:1:0:1:1:1:1"))), "2001:db8:1:0:1:1:1:1", "одна нулевая группа не сжимается")
        XCTAssertEqual(IpAddress.format(try XCTUnwrap(IpAddress.parse("FE80::ABCD"))), "fe80::abcd")
    }

    func testDirectRulesActOnlyInSplitMode() {
        let rules = [SiteRule(pattern: "example.com", action: .direct), SiteRule(pattern: "ads.example.org", action: .block), SiteRule(pattern: "example.org", action: .proxy)]
        let all = RoutingSettings(mode: .allTraffic, rules: rules)
        XCTAssertFalse(all.isSplit)
        XCTAssertFalse(all.hasBypassSites)
        XCTAssertTrue(all.hasIdleRules, "«Напрямую» в режиме «Весь трафик» молчит — над списком подпись")
        XCTAssertEqual(all.activeRules.map(\.pattern), ["ads.example.org", "example.org"])

        let split = RoutingSettings(mode: .split, rules: rules)
        XCTAssertTrue(split.hasBypassSites)
        XCTAssertFalse(split.hasIdleRules)
        XCTAssertEqual(split.activeRules, rules, "в раздельном режиме — все по порядку списка")

        XCTAssertFalse(RoutingSettings(mode: .split, rules: [SiteRule(pattern: "example.org", action: .block)]).hasBypassSites)
        XCTAssertEqual(RoutingSettings(), RoutingSettings(mode: .allTraffic, rules: []), "по умолчанию весь трафик через VPN")
    }

    func testSavingAddsReplacesTheSameSiteAndKeepsTheOrder() {
        let a = SiteRule(pattern: "a.example.com", action: .direct)
        let b = SiteRule(pattern: "b.example.com", action: .block)
        let c = SiteRule(pattern: "c.example.com", action: .proxy)
        let start = RoutingSettings(mode: .split, rules: [a, b, c])

        let added = start.saving(SiteRule(pattern: "d.example.com", action: .block))
        XCTAssertEqual(added.rules.map(\.pattern), ["a.example.com", "b.example.com", "c.example.com", "d.example.com"], "новое — в конец")
        XCTAssertEqual(added.mode, .split)

        let edited = start.saving(SiteRule(pattern: "b.example.com", action: .proxy), replacing: b)
        XCTAssertEqual(edited.rules, [a, SiteRule(pattern: "b.example.com", action: .proxy), c], "измененное — на свое место")

        let renamed = start.saving(SiteRule(pattern: "a.example.com", action: .proxy), replacing: c)
        XCTAssertEqual(renamed.rules, [b, SiteRule(pattern: "a.example.com", action: .proxy)], "правило для того же сайта заменяется — одно на сайт")

        let duplicate = start.saving(SiteRule(pattern: "c.example.com", action: .direct))
        XCTAssertEqual(duplicate.rules, [a, b, SiteRule(pattern: "c.example.com", action: .direct)])

        XCTAssertEqual(start.removing(b).rules, [a, c])
        XCTAssertEqual(start.removing(SiteRule(pattern: "b.example.com", action: .direct)).rules, [a, b, c], "удаляется только то самое правило")
    }

    func testNormalizedKeepsOnlyPatternsTheCoreAccepts() {
        let routing = RoutingSettings(mode: .split, rules: [
            SiteRule(pattern: " Example.com ", action: .direct), SiteRule(pattern: "geosite:category-ads", action: .block),
            SiteRule(pattern: "example.com", action: .block), SiteRule(pattern: "198.51.100.7/24", action: .proxy), SiteRule(pattern: "not a site", action: .block),
        ])
        XCTAssertEqual(
            routing.normalized,
            RoutingSettings(mode: .split, rules: [SiteRule(pattern: "example.com", action: .direct), SiteRule(pattern: "198.51.100.0/24", action: .proxy)])
        )
        XCTAssertEqual(routing.normalized.normalized, routing.normalized, "обычный вид не меняется")
    }
}

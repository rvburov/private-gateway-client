import Foundation
import XCTest
@testable import PrivateGatewayCore

/// «Все адреса сервиса» (решение 741): список сервисов — код из shared/specifications/routing/services.json (scripts/
/// gen-services.cs), тот же у десктопа и Android; из «geosite:» телефон берет только сервисы списка, ядро получает их домены.
final class ServiceSitesTests: XCTestCase {
    func testPlainDomainFindsItsService() {
        let cases: [(String, String?)] = [
            ("youtube.com", "youtube"), ("m.youtube.com", "youtube"), ("googlevideo.com", "youtube"), ("t.me", "telegram"),
            ("chatgpt.com", "openai"), ("example.com", nil), ("notyoutube.com", nil), ("*.youtube.com", nil), ("geosite:youtube", nil),
            ("203.0.113.9", nil),
        ]
        for (pattern, id) in cases {
            XCTAssertEqual(ServiceSites.find(pattern)?.id, id, pattern)
        }
    }

    func testRuleOfAServiceIsItsGeositeCategory() throws {
        let youtube = try XCTUnwrap(ServiceSites.of(pattern: "geosite:youtube"))
        XCTAssertEqual(youtube.name, "YouTube")
        XCTAssertEqual(youtube.pattern, "geosite:youtube")
        XCTAssertNil(ServiceSites.of(pattern: "geosite:category-ads-all"), "не из списка сервисов")
        XCTAssertEqual(SitePattern.normalize(" GEOSITE:YouTube "), "geosite:youtube")
        XCTAssertNil(SitePattern.normalize("geosite:google"), "базы geosite у телефона нет")
        XCTAssertEqual(SitePattern.rank(SiteRule(pattern: "geosite:youtube", action: .block)).group, 2)
        XCTAssertTrue(youtube.conditions.contains("domain:googlevideo.com"))
        XCTAssertTrue(youtube.conditions.contains("full:yt3.googleusercontent.com"))
        XCTAssertFalse(youtube.contains("x.yt3.googleusercontent.com"), "у точного имени поддоменов нет")
    }

    /// Код списка совпадает с общим файлом: правят services.json и запускают scripts/gen-services.cs.
    func testListIsTheSharedOne() throws {
        let file = LinkParserTests.testData.deletingLastPathComponent().appendingPathComponent("specifications/routing/services.json")
        let shared = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(contentsOf: file)) as? [String: Any])
        let services = try XCTUnwrap(shared["services"] as? [[String: Any]])
        XCTAssertEqual(services.map { ServiceSite(id: $0["id"] as? String ?? "", name: $0["name"] as? String ?? "", domains: $0["domains"] as? [String] ?? [], full: $0["full"] as? [String] ?? []) }, ServiceSites.all)
    }
}

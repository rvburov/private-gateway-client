import Foundation
import XCTest
@testable import PrivateGatewayCore

/// Подписки — те же случаи, что у Android (`SubscriptionMergeTest`, `SubscriptionAddressTest`, `SubscriptionTitleTest`,
/// `FailureReasonTest`, `SubscriptionScheduleTest`, `UpdateGateTest`, `SubscriptionFetchTest`): что остается и что
/// добавляется при обновлении, адрес без токена, название из заголовка, причины отказа, расписание, разбор загруженного.
final class SubscriptionTests: XCTestCase {
    private func server(_ link: String) -> ServerDefinition {
        LinkParser.parse(link).server!
    }

    private func stored(_ links: String...) -> [StoredServer] {
        links.enumerated().map { StoredServer(id: "s\($0.offset)", server: server($0.element)) }
    }

    // MARK: Слияние

    func testUpdateKeepsKnownServersAddsNewAndRemovesGone() {
        let own = stored("trojan://pw-a@198.51.100.1:443#A", "trojan://pw-b@198.51.100.2:443#B", "trojan://pw-c@198.51.100.3:443#C")
        let result = SubscriptionMerge.merge(
            own: own,
            incoming: [
                server("trojan://pw-a@198.51.100.1:443#A"),
                // Поставщик поправил SNI и название: тот же выход — тот же сервер, номер (избранное, свое название) остается.
                server("trojan://pw-b@198.51.100.2:443?sni=example.org#B2"),
                server("trojan://pw-d@198.51.100.4:443#D"),
            ],
            elsewhere: [], selectedId: nil
        )

        XCTAssertEqual(result.kept.map { $0.id }, ["s0", "s1"])
        XCTAssertEqual(result.kept.first { $0.id == "s1" }?.server.security.sni, "example.org", "описание — из новой версии подписки")
        XCTAssertEqual(result.added.map(\.name), ["D"])
        XCTAssertEqual(result.removed, ["s2"])
        XCTAssertNil(result.missing)
        XCTAssertEqual(result.selected, .none)
    }

    func testSelectedServerThatDisappearedStaysUntilAnotherIsChosen() {
        let own = stored("trojan://pw-a@198.51.100.1:443#A", "trojan://pw-b@198.51.100.2:443#B")
        let result = SubscriptionMerge.merge(own: own, incoming: [server("trojan://pw-b@198.51.100.2:443#B")], elsewhere: [], selectedId: "s0")
        XCTAssertEqual(result.selected, .missing)
        XCTAssertEqual(result.missing, "s0")
        XCTAssertEqual(result.removed, [], "выбранный не удаляется")

        let chosenOther = SubscriptionMerge.merge(own: own, incoming: [server("trojan://pw-b@198.51.100.2:443#B")], elsewhere: [], selectedId: "s1")
        XCTAssertEqual(chosenOther.removed, ["s0"])
    }

    func testChangedOrMissingIsDecidedByTheLinkName() {
        let own = stored("trojan://pw-a@198.51.100.1:443#A")
        let changed = SubscriptionMerge.merge(own: own, incoming: [server("trojan://pw-new@198.51.100.1:443#A")], elsewhere: [], selectedId: "s0")
        XCTAssertEqual(changed.selected, .changed)
        XCTAssertEqual(changed.missing, "s0")
        XCTAssertEqual(changed.added.map(\.name), ["A"], "новая версия — рядом, выбирает пользователь")

        // Тот же адрес, но новые ключи и другое название — для пользователя это другой сервер.
        let renamed = SubscriptionMerge.merge(own: own, incoming: [server("trojan://pw-new@198.51.100.1:443#A2")], elsewhere: [], selectedId: "s0")
        XCTAssertEqual(renamed.selected, .missing)
        // Другой адрес и ключи, но то же название — та же точка у поставщика, ее новая версия.
        let moved = SubscriptionMerge.merge(own: own, incoming: [server("trojan://pw-new@203.0.113.7:8443#A")], elsewhere: [], selectedId: "s0")
        XCTAssertEqual(moved.selected, .changed)
    }

    func testServerThatCameBackIsNoLongerMissing() {
        let own = stored("trojan://pw-a@198.51.100.1:443#A", "trojan://pw-b@198.51.100.2:443#B")
        let incoming = [server("trojan://pw-a@198.51.100.1:443#A"), server("trojan://pw-b@198.51.100.2:443#B")]
        let result = SubscriptionMerge.merge(own: own, incoming: incoming, elsewhere: [], selectedId: "s0")
        XCTAssertEqual(result.kept.map { $0.id }, ["s0", "s1"])
        XCTAssertNil(result.missing)
        XCTAssertEqual(result.selected, .none)
    }

    func testUnreadableServersCountAsGone() {
        let unreadable = [UnreadableServer(id: "u1", name: "U"), UnreadableServer(id: "u2", name: "V")]
        let result = SubscriptionMerge.merge(
            own: stored("trojan://pw-a@198.51.100.1:443#A"),
            incoming: [server("trojan://pw-a@198.51.100.1:443#A"), server("trojan://pw-u@198.51.100.5:443#U")],
            elsewhere: [], selectedId: "s0", unreadable: unreadable
        )
        XCTAssertEqual(result.kept.map { $0.id }, ["s0"])
        XCTAssertEqual(result.added.map(\.name), ["U"], "нечитаемый не узнать — его версия из подписки добавляется заново")
        XCTAssertEqual(result.removed, ["u1", "u2"])
        XCTAssertNil(result.missing)

        let own = stored("trojan://pw-a@198.51.100.1:443#A")
        let missing = SubscriptionMerge.merge(own: own, incoming: [server("trojan://pw-a@198.51.100.1:443#A")], elsewhere: [], selectedId: "u1", unreadable: unreadable)
        XCTAssertEqual(missing.removed, ["u2"], "выбранный не удаляется")
        XCTAssertEqual(missing.missing, "u1")
        XCTAssertEqual(missing.selected, .missing)

        let changed = SubscriptionMerge.merge(own: own, incoming: [server("trojan://pw-u@198.51.100.5:443#U")], elsewhere: [], selectedId: "u1", unreadable: unreadable)
        XCTAssertEqual(changed.removed, ["s0", "u2"])
        XCTAssertEqual(changed.selected, .changed, "в подписке есть сервер с тем же названием — его новая версия")
    }

    func testServersAlreadyInAnotherGroupOrRepeatedAreNotDuplicated() {
        let elsewhere: Set = [ServerKeys.key(server("trojan://pw-x@198.51.100.9:443#X"))]
        let result = SubscriptionMerge.merge(
            own: stored("trojan://pw-a@198.51.100.1:443#A"),
            incoming: [server("trojan://pw-a@198.51.100.1:443#A"), server("trojan://pw-x@198.51.100.9:443#Other")],
            elsewhere: elsewhere, selectedId: nil
        )
        XCTAssertEqual(result.added, [])

        let repeated = SubscriptionMerge.merge(
            own: [], incoming: [server("trojan://pw-d@198.51.100.4:443#D"), server("trojan://pw-d@198.51.100.4:443#D2")], elsewhere: [], selectedId: nil
        )
        XCTAssertEqual(repeated.added.map(\.name), ["D"])
    }

    func testKeysIgnoreTheNameAndTheHostCaseButNotTheCredentials() {
        let a = server("trojan://pw-a@Example.COM:443#A")
        XCTAssertEqual(ServerKeys.key(a), ServerKeys.key(server("trojan://pw-a@example.com:443#Other")))
        XCTAssertEqual(ServerKeys.exit(a), ServerKeys.exit(server("trojan://pw-a@example.com:443?sni=example.org&type=ws#A")))
        XCTAssertNotEqual(ServerKeys.key(a), ServerKeys.key(server("trojan://pw-b@example.com:443#A")))
        XCTAssertNotEqual(ServerKeys.key(a), ServerKeys.key(server("trojan://pw-a@example.com:443?sni=example.org#A")))
    }

    // MARK: Адрес и название

    func testMaskKeepsTheHostAndAWordAndHidesTheRest() throws {
        let cases = [
            ("https://example.com/sub/0123456789abcdef", "https://example.com/sub/••••"),
            ("https://example.org/clash/abc?token=1", "https://example.org/clash/••••"),
            ("https://sub.example.com/api/v1/client/subscribe?token=fake", "https://sub.example.com/api/••••"),
            ("https://example.com/AbCdEf0123456789xyz/clash", "https://example.com/••••"),
            ("https://example.com/0123456789abcdef", "https://example.com/••••"),
            ("https://example.com/AbCdEfGhIjKlMnOpQr/clash", "https://example.com/••••"),
            ("https://example.com/?token=fake", "https://example.com/••••"),
            ("https://user:pass@example.com:8443/sub", "https://example.com:8443/sub"),
            ("http://203.0.113.5:2096/sub/fake-id", "http://203.0.113.5:2096/sub/••••"),
            ("https://example.com", "https://example.com"),
            ("https://example.com:443/sub#x", "https://example.com/sub/••••"),
        ]
        for (url, expected) in cases {
            XCTAssertEqual(SubscriptionAddress.mask(try XCTUnwrap(SubscriptionAddress.parse(url), url)), expected, url)
        }
    }

    func testOnlyHttpAndHttpsWithAHostAreSubscriptions() throws {
        XCTAssertNotNil(SubscriptionAddress.parse("  https://example.com/sub  "))
        XCTAssertNil(SubscriptionAddress.parse("vless://11111111-2222-3333-4444-555555555555@example.com:443"))
        XCTAssertNil(SubscriptionAddress.parse("https:///nohost"))
        XCTAssertNil(SubscriptionAddress.parse("not a url"))
        XCTAssertNil(SubscriptionAddress.parse("ftp://example.com/sub"))
        XCTAssertTrue(SubscriptionAddress.insecure(try XCTUnwrap(SubscriptionAddress.parse("http://example.com/sub"))))
        XCTAssertFalse(SubscriptionAddress.insecure(try XCTUnwrap(SubscriptionAddress.parse("HTTPS://example.com/sub"))))
    }

    func testTitleComesFromTheProfileTitleHeader() {
        let cases: [(String?, String?)] = [
            ("Office VPN", "Office VPN"),
            ("  base64:0J3QvtGD0YLQsdGD0Lo=  ", "Ноутбук"),
            ("BASE64:0J3QvtGD0YLQsdGD0Lo", "Ноутбук"),
            ("base64:not base64!", nil),
            ("base64:AQID", nil),
            ("Caf\u{00C3}\u{00A9}", nil),
            ("   ", nil),
            (nil, nil),
            (String(repeating: "a", count: 150), String(repeating: "a", count: 100)),
        ]
        for (header, title) in cases {
            XCTAssertEqual(SubscriptionTitle.from(header), title, header ?? "nil")
        }
    }

    // MARK: Причины, расписание, очередь

    func testFailureReasonsTellCodesApart() {
        for code in [400, 402, 500, 502] {
            XCTAssertEqual(FailureReason.of(.status, status: code), .status(code))
        }

        XCTAssertEqual(FailureReason.of(.status, status: 404), .notFound)
        XCTAssertEqual(FailureReason.of(.status, status: 401), .forbidden(401))
        XCTAssertEqual(FailureReason.of(.status, status: 403), .forbidden(403))
        XCTAssertEqual(FailureReason.of(.status, status: nil), .network, "без кода «сервер ответил» сказать нечего")

        let expected: [DownloadFailure: FailureReason] = [
            .network: .network, .nameResolution: .nameResolution, .connection: .connection, .tls: .tls, .timeout: .timeout,
            .status: .status(500), .tooLarge: .tooLarge, .noServers: .noServers, .noAddress: .noAddress,
            .protectedPathUnavailable: .protectedPathUnavailable,
            .protectedTransportUnsupported: .protectedTransportUnsupported,
        ]
        XCTAssertEqual(Set(expected.keys), Set(DownloadFailure.allCases))
        for (failure, reason) in expected {
            XCTAssertEqual(FailureReason.of(failure, status: 500), reason, failure.rawValue)
        }

        XCTAssertNil(DownloadFailure.parse(nil))
        for failure in DownloadFailure.allCases {
            XCTAssertEqual(DownloadFailure.parse(failure.rawValue), failure)
        }

        XCTAssertEqual(DownloadFailure.parse("Gone"), .network)
    }

    func testScheduleFollowsTheIntervalAndWaitsAnHourAfterAFailure() {
        let hour: TimeInterval = 60 * 60
        let now = Date(timeIntervalSince1970: 1_000 * hour)
        let subscriptions = [
            SubscriptionTimes(id: "fresh", updatedAt: now - 11 * hour, lastAttemptAt: now - 11 * hour),
            SubscriptionTimes(id: "old", updatedAt: now - 12 * hour, lastAttemptAt: now - 12 * hour),
            SubscriptionTimes(id: "never", updatedAt: nil, lastAttemptAt: nil),
        ]
        XCTAssertEqual(SubscriptionSchedule.due(subscriptions, .every12Hours, now: now), ["old", "never"])
        XCTAssertEqual(SubscriptionSchedule.due(subscriptions, .every6Hours, now: now), ["fresh", "old", "never"])
        XCTAssertEqual(SubscriptionSchedule.due(subscriptions, .every24Hours, now: now), ["never"])
        XCTAssertEqual(SubscriptionSchedule.due(subscriptions, .off, now: now), [], "только кнопкой")

        let failed = SubscriptionTimes(id: "failed", updatedAt: now - 48 * hour, lastAttemptAt: now - 59 * 60)
        XCTAssertEqual(SubscriptionSchedule.due([failed], .every6Hours, now: now), [])
        let later = SubscriptionTimes(id: "failed", updatedAt: now - 48 * hour, lastAttemptAt: now - hour)
        XCTAssertEqual(SubscriptionSchedule.due([later], .every6Hours, now: now), ["failed"])
    }

    func testOnlyViaVpnWaitsForProtectionAndARunningUpdateIsNotStartedTwice() {
        let cases: [(Bool, Bool, Bool, UpdateBlock)] = [
            (true, false, false, .protectionOff), (true, false, true, .protectionOff), (true, true, false, .none), (true, true, true, .busy),
            (false, false, false, .none), (false, false, true, .busy), (false, true, false, .none),
        ]
        for (onlyViaVpn, connected, busy, block) in cases {
            XCTAssertEqual(UpdateGate.block(onlyViaVpn: onlyViaVpn, connected: connected, busy: busy), block, "\(onlyViaVpn) \(connected) \(busy)")
        }

        XCTAssertEqual(UpdateGate.block(onlyViaVpn: true, connected: false), .protectionOff)
        XCTAssertEqual(UpdateGate.block(onlyViaVpn: true, connected: true), .none)
    }

    // MARK: Загрузка и разбор

    /// Подделка сети: отдает текст или ошибку загрузки.
    private struct FakeSource: SubscriptionSource {
        var text = ""
        var title: String?
        var failure: SubscriptionDownloadError?

        func download(_ url: URLComponents) async throws -> SubscriptionContent {
            if let failure {
                throw failure
            }

            return SubscriptionContent(text: text, title: title)
        }
    }

    private let url = SubscriptionAddress.parse("https://sub.example.com/sub/fake-token-1")!

    func testProtectedFetchDoesNotUseASourceThatOnlyImplementsOrdinaryDownloads() async {
        let source = FakeSource(text: "trojan://fake-password@192.0.2.1:443#A")
        let result = await SubscriptionFetch.fetch(url, from: source, protected: true)
        XCTAssertEqual(result, .failed(.protectedPathUnavailable, status: nil), "подмена защищенной загрузки обычной запрещена даже при connected")
    }

    func testEverySharedSubscriptionGivesTheServersThatParsed() async throws {
        let folder = LinkParserTests.testData.appendingPathComponent("subscriptions")
        let files = (FileManager.default.enumerator(atPath: folder.path)?.compactMap { $0 as? String } ?? []).filter { $0.hasSuffix(".expected.json") }.sorted()
        XCTAssertGreaterThan(files.count, 5, "эталоны найдены")
        for file in files {
            let json = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(contentsOf: folder.appendingPathComponent(file))) as? [String: Any])
            let lines = try XCTUnwrap(json["lines"] as? [[String: Any]])
            let expected = lines.filter { $0["error"] == nil }.compactMap { line in
                (line["name"] as? String) ?? ((line["server"] as? [String: Any])?["name"] as? String)
            }
            let text = try String(contentsOf: folder.appendingPathComponent(String(file.dropLast(".expected.json".count)) + ".txt"), encoding: .utf8)

            let result = await SubscriptionFetch.fetch(url, from: FakeSource(text: text))

            XCTAssertFalse(expected.isEmpty, file)
            guard case let .fetched(servers, _) = result else {
                XCTFail("\(file): \(result)")
                continue
            }

            XCTAssertEqual(servers.map(\.name), expected, file)
        }
    }

    func testLinesThatDoNotParseAreSkippedAndTheTitleComesThrough() async {
        let source = FakeSource(text: "trojan://pw-a@198.51.100.1:443#A\ntrojan://pw-b@198.51.100.2:443#B\nbroken", title: "Ноутбук")

        guard case let .fetched(servers, title) = await SubscriptionFetch.fetch(url, from: source) else {
            return XCTFail("не загружено")
        }

        XCTAssertEqual(servers.map(\.name), ["A", "B"])
        XCTAssertEqual(title, "Ноутбук")
    }

    func testAnswerWithoutServersOrAFailedDownloadKeepsThePreviousList() async {
        for text in ["<html>не подписка</html>", "vless://broken@203.0.113.24:abc\n", ""] {
            let result = await SubscriptionFetch.fetch(url, from: FakeSource(text: text))
            XCTAssertEqual(result, .failed(.noServers, status: nil), text)
        }

        let forbidden = await SubscriptionFetch.fetch(url, from: FakeSource(failure: SubscriptionDownloadError(.status, status: 403)))
        XCTAssertEqual(forbidden, .failed(.status, status: 403))
        let timeout = await SubscriptionFetch.fetch(url, from: FakeSource(failure: SubscriptionDownloadError(.timeout)))
        XCTAssertEqual(timeout, .failed(.timeout, status: nil))
    }

    func testSuggestedNameIsTheProfileTitleOrTheSubscriptionHost() throws {
        XCTAssertEqual(SubscriptionFetch.suggestName("Ноутбук", url), "Ноутбук")
        XCTAssertEqual(SubscriptionFetch.suggestName(nil, url), "sub.example.com")
        XCTAssertEqual(SubscriptionFetch.suggestName(nil, try XCTUnwrap(SubscriptionAddress.parse("http://203.0.113.5:2096/sub/fake-id"))), "203.0.113.5")
    }
}

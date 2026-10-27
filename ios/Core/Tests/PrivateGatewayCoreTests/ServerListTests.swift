import Foundation
import XCTest
@testable import PrivateGatewayCore

/// Список серверов, задержка, быстрая смена, подписи и названия — те же случаи, что у Android (`ServerListTest`,
/// `LatencyTest`, `QuickSwitchTest`, `ServerFactsTest`, `ServerNamesTest`).
final class ServerListTests: XCTestCase {
    private let ru = Locale(identifier: "ru_RU")
    private let now = Date(timeIntervalSince1970: 1_793_029_320)
    private let minute: TimeInterval = 60

    private lazy var main = SubscriptionRef(id: "main", name: "Основная подписка", updatedAt: now)
    private let spare = SubscriptionRef(id: "spare", name: "Резервная", updatedAt: nil)

    private lazy var nl = ServerItem(
        id: "nl", linkName: "Нидерланды 01", countryCode: "NL", protocol: .vless, security: .reality, transport: .tcp, port: 443,
        subscriptionId: "main", favorite: true, latency: .of(42), lastUsedAt: now
    )
    private let fi = ServerItem(
        id: "fi", linkName: "Финляндия 01", countryCode: "FI", protocol: .hysteria2, security: .tls, transport: .quic, port: 8443,
        subscriptionId: "main", favorite: true, latency: .of(38)
    )
    private lazy var de = ServerItem(
        id: "de", linkName: "Германия 02", countryCode: "DE", protocol: .vless, security: .tls, transport: .xhttp, port: 443,
        subscriptionId: "spare", latency: .of(51), lastUsedAt: now.addingTimeInterval(-24 * 60 * minute)
    )
    private let us = ServerItem(
        id: "us", linkName: "США 01", countryCode: "US", protocol: .trojan, security: .tls, transport: .webSocket, port: 443, latency: .of(148)
    )
    private let tr = ServerItem(
        id: "tr", linkName: "Турция 01", countryCode: "TR", protocol: .vless, security: .tls, transport: .grpc, port: 443,
        subscriptionId: "main", latency: .failed(.noResponse)
    )

    private var all: [ServerItem] { [nl, fi, de, us, tr] }

    private func build(_ query: ServerListQuery) -> [ServerGroup] {
        ServerList.build(all, [main, spare], query, locale: ru)
    }

    private func ids(_ groups: [ServerGroup]) -> [String] {
        groups.flatMap { $0.servers.map(\.id) }
    }

    private func copy(_ server: ServerItem, _ change: (inout ServerItem) -> Void) -> ServerItem {
        var result = server
        change(&result)
        return result
    }

    func testAllTabGroupsBySubscriptionInTheirOrderAndManualServersLast() {
        let groups = build(ServerListQuery())

        XCTAssertEqual(groups.map { $0.subscription?.id }, ["main", "spare", nil])
        XCTAssertEqual(groups[0].servers.map(\.id), ["fi", "nl", "tr"], "по задержке, без ответа — в конце")
        XCTAssertEqual(groups[1].servers.map(\.id), ["de"])
        XCTAssertEqual(groups[2].servers.map(\.id), ["us"])
        XCTAssertEqual(groups[0].subscription, main, "заголовку группы — название и время обновления подписки")
        XCTAssertEqual(build(ServerListQuery(search: "германия")).map { $0.subscription?.id }, ["spare"], "группы без строк не видны")
    }

    func testServerOfARemovedSubscriptionIsShownWithManualOnes() {
        let orphan = copy(fi) {
            $0.id = "orphan"
            $0.subscriptionId = "gone"
        }

        let groups = ServerList.build([orphan, us], [main], ServerListQuery(), locale: ru)

        XCTAssertEqual(groups.count, 1)
        XCTAssertNil(groups[0].subscription)
        XCTAssertEqual(groups[0].servers.map(\.id), ["orphan", "us"])
    }

    func testSearchFindsByNameCountryAndProtocolButNotTheAddress() {
        for (search, expected) in [("германия", "de"), ("ФИНЛЯНДИЯ", "fi"), ("us", "us"), ("hysteria", "fi"), ("  сша  ", "us")] {
            XCTAssertEqual(ids(build(ServerListQuery(search: search))), [expected], search)
        }

        // Адрес запечатан вместе с ключом (решение 420): ни адрес, ни порт поиск не видит.
        for search in ["203.0.113.24", "8443", "443"] {
            XCTAssertEqual(build(ServerListQuery(search: search)), [], search)
        }

        XCTAssertTrue(ServerList.matches(nl, "   "))
        XCTAssertFalse(ServerList.matches(nl, "Польша"))
    }

    func testRenamedServerIsFoundAndSortedByItsOwnName() {
        let work = copy(nl) { $0.customName = "Школа, работа" }
        let servers = [work, fi]

        XCTAssertEqual(work.displayName, "Школа, работа")
        XCTAssertEqual(nl.displayName, "Нидерланды 01", "без своего — название из ссылки")
        XCTAssertEqual(ids(ServerList.build(servers, [], ServerListQuery(search: "работа"), locale: ru)), ["nl"])
        XCTAssertEqual(ServerList.build(servers, [], ServerListQuery(search: "нидерланды"), locale: ru), [])
        let byName = ServerList.build(servers, [], ServerListQuery(sort: .name), locale: ru)
        XCTAssertEqual(ids(byName), ["fi", "nl"], "«Школа» после «Финляндии», хотя «Нидерланды» — раньше")
    }

    func testHiddenProtocolsAreFilteredOut() {
        XCTAssertEqual(ids(build(ServerListQuery(hiddenProtocols: [.vless]))), ["fi", "us"])
    }

    func testSortByNameFollowsTheLanguageAlphabet() {
        let favorites = build(ServerListQuery(tab: .favorites, sort: .name))
        XCTAssertEqual(favorites.first?.servers.map(\.displayName), ["Нидерланды 01", "Финляндия 01"])

        // Без учета регистра и по алфавиту языка: по кодам «бета» встала бы после «Яблоко», а «е» с точками (escape-ом: в
        // текстах проекта этой буквы нет) — в начало, перед «А».
        func byName(_ names: [String]) -> [String] {
            let servers = names.enumerated().map { index, name in
                copy(us) {
                    $0.id = "s\(index)"
                    $0.linkName = name
                }
            }

            return ServerList.build(servers, [], ServerListQuery(sort: .name), locale: ru).first?.servers.map(\.displayName) ?? []
        }

        XCTAssertEqual(byName(["Яблоко", "бета", "Жук", "\u{0401}лка", "Альфа", "Дом"]), ["Альфа", "бета", "Дом", "\u{0401}лка", "Жук", "Яблоко"])
        XCTAssertEqual(byName(["Beta", "alpha"]), ["alpha", "Beta"])
        XCTAssertEqual(byName(["Caf\u{00E9}", "Cafe"]), ["Cafe", "Caf\u{00E9}"])

        // Названия, которые различаются только регистром, равны — остаются в порядке списка, как у десктопа.
        let upper = copy(us) {
            $0.id = "upper"
            $0.linkName = "США 01"
        }
        let lower = copy(us) {
            $0.id = "lower"
            $0.linkName = "сша 01"
        }
        for input in [[upper, lower], [lower, upper]] {
            let rows = ServerList.build(input, [], ServerListQuery(sort: .name), locale: ru).first?.servers ?? []
            XCTAssertEqual(rows.map(\.id), input.map(\.id))
        }
    }

    func testSortByLatencyPutsMeasuredFirstAndTheRestByName() {
        func server(_ id: String, _ name: String, _ latency: Latency?) -> ServerItem {
            copy(us) {
                $0.id = id
                $0.linkName = name
                $0.latency = latency
            }
        }

        let servers = [
            server("apple", "Яблоко", .of(30)), server("beta", "бета", nil), server("alpha", "Альфа", .failed(.noResponse)),
            server("home", "Дом", .of(30)), server("fast", "Жук", .of(5)), server("slow", "Аист", .of(300)),
        ]

        let sorted = ServerList.build(servers, [], ServerListQuery(), locale: ru).first?.servers ?? []

        XCTAssertEqual(sorted.map(\.id), ["fast", "home", "apple", "slow", "alpha", "beta"], "равная задержка — по названию")
    }

    func testFavoritesTabIsOneGroupOfStarredServers() {
        let groups = build(ServerListQuery(tab: .favorites))

        XCTAssertEqual(groups.count, 1)
        XCTAssertNil(groups[0].subscription)
        XCTAssertEqual(groups[0].servers.map(\.id), ["fi", "nl"])
    }

    func testRecentTabKeepsTheLastTenChosenServersInTheOrderTheyWereChosen() {
        var many = (0..<15).map { index in
            copy(us) {
                $0.id = "s\(index)"
                $0.linkName = "США \(Character(Unicode.Scalar(UInt8(ascii: "z") - UInt8(index))))"
                $0.latency = .of(100 - index)
                $0.lastUsedAt = now.addingTimeInterval(-Double(index) * minute)
            }
        } + [tr]
        many.reverse()

        for sort in [ServerSort.latency, .name] {
            let rows = ServerList.build(many, [], ServerListQuery(tab: .recent, sort: sort), locale: ru).first?.servers ?? []
            XCTAssertEqual(ServerList.recentLimit, 10)
            XCTAssertEqual(rows.map(\.id), (0..<10).map { "s\($0)" }, "свежие сверху; задержка и название порядок не меняют (решение 421)")
        }

        let chosen = copy(us) { $0.lastUsedAt = now.addingTimeInterval(5 * minute) }
        let recent = ServerList.build([nl, chosen, de], [], ServerListQuery(tab: .recent, sort: .name), locale: ru)
        XCTAssertEqual(ids(recent), ["us", "nl", "de"])
    }

    func testFilterListsOnlyPresentProtocolsAndCountsWhatItHides() {
        XCTAssertEqual(ServerList.protocolsIn(all), [.vless, .hysteria2, .trojan])
        XCTAssertEqual(Set(ServerList.protocolOrder), Set(ProxyProtocol.allCases), "в порядке нет ни одного протокола")
        XCTAssertEqual(ServerList.protocolOrder.count, ProxyProtocol.allCases.count)

        XCTAssertEqual(ServerList.filterCount(all, ServerListQuery()), 0)
        // TUIC в списке нет — его флажка не видно, в число он не входит.
        XCTAssertEqual(ServerList.filterCount(all, ServerListQuery(hiddenProtocols: [.vless, .tuic])), 1)
        XCTAssertEqual(ServerList.filterCount(all, ServerListQuery(hiddenProtocols: [.vless], sort: .name)), 2)
        XCTAssertEqual(ServerList.filterCount(all, ServerListQuery(sort: .name)), 1)
    }

    func testEmptyListExplainsWhy() {
        func empty(_ servers: [ServerItem], _ query: ServerListQuery) -> ServerListEmpty? {
            ServerList.empty(servers, ServerList.build(servers, [], query, locale: ru), query)
        }

        XCTAssertNil(empty(all, ServerListQuery()))
        XCTAssertEqual(ServerList.build([us], [], ServerListQuery(tab: .favorites), locale: ru), [], "пустая вкладка — без групп")
        XCTAssertEqual(empty([], ServerListQuery(tab: .favorites)), .noServers)
        XCTAssertEqual(empty([us], ServerListQuery(tab: .favorites)), .noFavorites)
        XCTAssertEqual(empty([us], ServerListQuery(tab: .favorites, sort: .name)), .noFavorites, "порядок строк ничего не скрывает")
        XCTAssertEqual(empty([us], ServerListQuery(search: "   ", tab: .favorites)), .noFavorites, "пробелы — не поиск")
        XCTAssertEqual(empty([us], ServerListQuery(tab: .favorites, hiddenProtocols: [.tuic])), .noFavorites, "скрыт протокол, которого в списке нет")
        XCTAssertEqual(empty([us], ServerListQuery(tab: .recent)), .noRecent)
        XCTAssertEqual(empty(all, ServerListQuery(search: "Польша")), .nothingFound)
        XCTAssertEqual(empty(all, ServerListQuery(search: "США", tab: .favorites)), .nothingFound)
        XCTAssertEqual(empty([nl, us], ServerListQuery(tab: .recent, hiddenProtocols: [.vless])), .nothingFound, "выбранный раньше скрыт фильтром")
    }

    // MARK: Задержка и быстрая смена

    func testLatencyAbove120MsIsSlowAndNoAnswerDiffersFromNeverMeasured() {
        XCTAssertEqual(LatencyKind.slowMs, 120)
        for (ms, kind) in [(1, LatencyKind.good), (42, .good), (120, .good), (121, .slow), (148, .slow)] {
            XCTAssertEqual(LatencyKind.of(.of(ms)), kind, "\(ms)")
        }

        XCTAssertEqual(LatencyKind.of(nil), .never)
        for failure in LatencyFailure.allCases {
            XCTAssertEqual(LatencyKind.of(.failed(failure)), .noAnswer, failure.rawValue)
        }

        XCTAssertEqual(Latency.of(0), Latency(ms: 1, failure: nil), "меньше миллисекунды — 1")
        XCTAssertNil(Latency.failed(.coreFailed).ms)
    }

    func testQuickSwitchOffersTheServerInUseAndTheFastestMeasured() {
        func server(_ id: String, _ latency: Latency?) -> ServerItem {
            ServerItem(id: id, linkName: id.uppercased(), countryCode: id.uppercased(), protocol: .vless, security: .reality, transport: .tcp, port: 443, latency: latency)
        }

        let nl = server("nl", .of(42))
        let fi = server("fi", .of(38))
        let de = server("de", .of(51))
        let us = server("us", .of(148))
        let tr = server("tr", .failed(.noResponse))
        let pl = server("pl", nil)
        let all = [us, tr, nl, pl, de, fi]

        XCTAssertEqual(QuickSwitch.size, 3)
        XCTAssertEqual(QuickSwitch.pick(all, inUse: nl).map(\.id), ["nl", "fi", "de"])
        XCTAssertEqual(QuickSwitch.pick(all, inUse: tr).map(\.id), ["tr", "fi", "nl"], "у текущего «—», но он первый")
        XCTAssertEqual(QuickSwitch.pick(all, inUse: fi).map(\.id), ["fi", "nl", "de"], "самый быстрый — он же текущий, второй раз не идет")
        XCTAssertEqual(QuickSwitch.pick(all, inUse: nil).map(\.id), ["fi", "nl", "de"])
        XCTAssertEqual(QuickSwitch.pick([tr, us, pl], inUse: us).map(\.id), ["us"])
        XCTAssertEqual(QuickSwitch.pick([tr, pl], inUse: nil), [])
        let twin = server("twin", .of(38))
        XCTAssertEqual(QuickSwitch.pick([fi, twin, nl], inUse: us).map(\.id), ["us", "fi", "twin"], "равная задержка — в порядке списка")
    }

    // MARK: Подписи и названия

    func testSummaryNamesWhatDistinguishesTheServer() {
        let cases: [(ProxyProtocol, TransportSecurity, TransportKind, String)] = [
            (.vless, .reality, .tcp, "VLESS · REALITY"), (.vless, .reality, .grpc, "VLESS · REALITY"), (.vless, .tls, .xhttp, "VLESS · XHTTP"),
            (.vless, .tls, .grpc, "VLESS · gRPC"), (.vless, .tls, .tcp, "VLESS · TLS"), (.vless, .none, .tcp, "VLESS"),
            (.trojan, .tls, .webSocket, "Trojan · WS"), (.trojan, .tls, .tcp, "Trojan"), (.hysteria2, .tls, .quic, "Hysteria2"),
            (.tuic, .tls, .quic, "TUIC v5"), (.wireGuard, .none, .udp, "WireGuard"), (.vmess, .none, .httpUpgrade, "VMess · HTTPUpgrade"),
            // TLS пишется у протоколов, которые бывают и без него: VLESS и VMess, но не Trojan.
            (.vmess, .tls, .tcp, "VMess · TLS"),
        ]
        for (proto, security, transport, expected) in cases {
            XCTAssertEqual(ServerFacts.summary(proto, security, transport), expected)
        }
    }

    func testVlessIsEncryptedOnlyWithTlsOrReality() {
        XCTAssertFalse(ServerFacts.isEncrypted(.vless, .none))
        XCTAssertTrue(ServerFacts.isEncrypted(.vless, .tls))
        XCTAssertTrue(ServerFacts.isEncrypted(.vless, .reality))
        for proto in ProxyProtocol.allCases where proto != .vless {
            XCTAssertTrue(ServerFacts.isEncrypted(proto, .none), proto.rawValue)
        }
    }

    func testAddressPutsIpv6InBrackets() {
        XCTAssertEqual(ServerFacts.address("203.0.113.24", 443), "203.0.113.24:443")
        XCTAssertEqual(ServerFacts.address("example.com", 8443), "example.com:8443")
        XCTAssertEqual(ServerFacts.address("2001:db8::10", 8443), "[2001:db8::10]:8443")
        XCTAssertEqual(ServerFacts.address("::1", 1), "[::1]:1")
    }

    func testCoreNameIsXrayExceptForProtocolsTheCoreRefuses() throws {
        XCTAssertEqual(ServerFacts.coreName(.vless), "Xray-core")
        XCTAssertEqual(ServerFacts.coreName(.wireGuard), "Xray-core", "у десктопа — sing-box, на телефоне — Xray")
        XCTAssertNil(ServerFacts.coreName(.hysteria2))
        XCTAssertNil(ServerFacts.coreName(.tuic))

        // Сверка с конфигом ядра на всех эталонах: «ядра нет» ровно у тех, от кого Xray отказывается по протоколу.
        let folder = LinkParserTests.testData.appendingPathComponent("links")
        let files = FileManager.default.enumerator(atPath: folder.path)?.compactMap { $0 as? String }.filter { $0.hasSuffix(".txt") } ?? []
        let servers = try files.compactMap { file in
            LinkParser.parse(try String(contentsOf: folder.appendingPathComponent(file), encoding: .utf8).trimmingCharacters(in: .whitespacesAndNewlines)).server
        }
        XCTAssertEqual(Set(servers.map(\.protocol)), Set(ProxyProtocol.allCases), "эталоны есть на каждый протокол")
        for server in servers {
            let refused = XrayConfig.build(server, CoreSettings(), tunFd: 1).rejection == .unsupportedProtocol
            XCTAssertEqual(refused, ServerFacts.coreName(server.protocol) == nil, server.protocol.rawValue)
        }
    }

    func testOwnNameIsTrimmedAndAtMost64Characters() {
        XCTAssertEqual(ServerNames.validate("  Варшава, работа "), "Варшава, работа")
        XCTAssertEqual(ServerNames.validate("\tМой сервер\n"), "Мой сервер")
        for name in ["", "   ", " "] {
            XCTAssertNil(ServerNames.validate(name), "«\(name)»")
        }

        let longest = String(repeating: "Н", count: 64)
        XCTAssertEqual(ServerNames.maxLength, 64)
        XCTAssertEqual(ServerNames.validate(longest), longest)
        XCTAssertEqual(ServerNames.validate("  \(longest)  "), longest, "пробелы по краям в длину не идут")
        XCTAssertNil(ServerNames.validate(longest + "1"))
    }

    func testSameServerUnderAnotherNameIsTheSameKeyButNotAnotherExit() throws {
        let first = try XCTUnwrap(LinkParser.parse("trojan://pw@198.51.100.7:443?sni=example.org#One").server)
        let renamed = try XCTUnwrap(LinkParser.parse("trojan://pw@198.51.100.7:443?sni=example.org#Other").server)
        let newSni = try XCTUnwrap(LinkParser.parse("trojan://pw@198.51.100.7:443?sni=example.com#One").server)
        let otherKey = try XCTUnwrap(LinkParser.parse("trojan://other@198.51.100.7:443?sni=example.org#One").server)

        XCTAssertEqual(ServerKeys.key(first), ServerKeys.key(renamed), "название ключ не меняет")
        XCTAssertNotEqual(ServerKeys.key(first), ServerKeys.key(newSni))
        XCTAssertEqual(ServerKeys.exit(first), ServerKeys.exit(newSni), "поставщик поправил SNI — выход тот же")
        XCTAssertNotEqual(ServerKeys.exit(first), ServerKeys.exit(otherKey))
        XCTAssertEqual(ServerKeys.endpoint(first), ServerKeys.endpoint(otherKey), "куда подключаться — без секретов")
    }
}

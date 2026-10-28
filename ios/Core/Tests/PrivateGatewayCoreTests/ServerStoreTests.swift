import Foundation
import XCTest
@testable import PrivateGatewayCore

/// Хранилище серверов и групп — те же случаи, что `ServerRepositoryTest` и `GroupRepositoryTest` Android: каждый эталонный
/// сервер возвращается как был, адрес, ключи и токен подписки лежат только в Связке ключей (здесь — память), дубль под
/// другим названием не добавляется, обновление подписки сохраняет свое название, избранное, замер и выбор.
final class ServerStoreTests: XCTestCase {
    private let file = MemoryFile()
    private let vault = MemoryVault()
    private lazy var store = ServerStore(file: file, vault: vault, clock: { Date(timeIntervalSince1970: 1_793_029_320) })

    /// Все серверы эталонных ссылок shared/testdata, которые разбираются.
    private static let fixtures: [ServerDefinition] = {
        let folder = LinkParserTests.testData.appendingPathComponent("links")
        let files = (FileManager.default.enumerator(atPath: folder.path)?.compactMap { $0 as? String } ?? []).filter { $0.hasSuffix(".txt") }.sorted()
        return files.compactMap { file in
            (try? String(contentsOf: folder.appendingPathComponent(file), encoding: .utf8)).flatMap {
                LinkParser.parse($0.trimmingCharacters(in: .whitespacesAndNewlines)).server
            }
        }
    }()

    /// Эталоны без дублей — первый с каждым отпечатком.
    private static let unique: [ServerDefinition] = {
        var seen = Set<String>()
        return fixtures.filter { seen.insert(ServerKeys.key($0)).inserted }
    }()

    private let url = SubscriptionAddress.parse("https://example.com/sub/0123456789abcdef0123456789abcdef")!

    private func renamed(_ server: ServerDefinition, _ name: String) -> ServerDefinition {
        var copy = server
        copy.name = name
        return copy
    }

    private func state() throws -> String {
        String(decoding: try XCTUnwrap(file.read()), as: UTF8.self)
    }

    // MARK: Серверы

    func testEveryFixtureServerComesBackAsItWentIn() async throws {
        let servers = Self.fixtures
        XCTAssertGreaterThanOrEqual(servers.count, 15, "эталонов слишком мало")

        let result = try await store.add(servers)
        XCTAssertEqual(result, AddResult(added: Self.unique.count, duplicates: servers.count - Self.unique.count))

        let items = try await store.servers()
        XCTAssertEqual(items.count, Self.unique.count)
        let byKey = Dictionary(uniqueKeysWithValues: Self.unique.map { (ServerKeys.key($0), $0) })
        for item in items {
            let loaded = try await store.load(item.id)
            let server = try XCTUnwrap(loaded, item.displayName)
            XCTAssertEqual(byKey[ServerKeys.key(server)], server, item.displayName)
            XCTAssertEqual(item.port, server.port)
            XCTAssertEqual(item.protocol, server.protocol)
        }
    }

    func testAddressAndKeysAreStoredOnlyInTheVault() async throws {
        try await store.add(Self.fixtures)
        let open = try state()
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .millisecondsSince1970
        let stored = try decoder.decode(StoredState.self, from: XCTUnwrap(file.read()))
        let items = try await store.servers()
        XCTAssertEqual(vault.all.count, items.count, "ссылка каждого сервера — в Связке ключей")
        for item in items {
            let loaded = try await store.load(item.id)
            let server = try XCTUnwrap(loaded)
            // Адрес открыт только в названии, если названия в ссылке не было: тогда сервер называется адресом (как на
            // десктопе и Android). Поэтому адрес ищется в открытой записи самого сервера — в файле он может быть названием
            // другого сервера. Ключи и пароли не открыты нигде в файле.
            let record = try XCTUnwrap(stored.servers.first { $0.id == item.id })
            let own = try String(decoding: JSONEncoder().encode(record), as: UTF8.self)
            if server.host != item.linkName {
                XCTAssertFalse(own.contains(server.host), "\(item.linkName): адрес «\(server.host)» лежит открыто")
            }

            let secrets = [server.credentials.uuid, server.credentials.password, server.credentials.privateKey, server.credentials.presharedKey]
                .compactMap { $0 }.filter { $0.count >= 4 }
            for secret in secrets {
                XCTAssertFalse(open.contains(secret), "\(item.linkName): «\(secret)» лежит открыто")
            }
        }
    }

    func testTheSameServerUnderAnotherNameIsADuplicate() async throws {
        let server = try XCTUnwrap(Self.unique.first)
        let first = try await store.add([server])
        XCTAssertEqual(first, AddResult(added: 1, duplicates: 0))
        let again = try await store.add([renamed(server, "Другое название")])
        XCTAssertEqual(again, AddResult(added: 0, duplicates: 1))
        var otherPort = server
        otherPort.port = server.port % 65535 + 1
        let other = try await store.add([otherPort])
        XCTAssertEqual(other, AddResult(added: 1, duplicates: 0))
    }

    func testAServerWhoseLinkIsGoneIsListedButNotLoaded() async throws {
        try await store.add(Array(Self.unique.prefix(2)))
        let items = try await store.servers()
        // Запись Связки ключей пропала (восстановление на другом iPhone): сервер виден, удалить можно, подключиться — нет.
        try vault.remove(ServerStore.serverKey(items[1].id))

        let listed = try await store.servers()
        XCTAssertEqual(listed.map(\.id), items.map(\.id))
        let loaded = try await store.load(items[1].id)
        XCTAssertNil(loaded)
        try await store.delete(items[1].id)
        let left = try await store.servers()
        XCTAssertEqual(left.map(\.id), [items[0].id])
    }

    func testAddsAtTheSameTimeDoNotAddTheSameServersTwice() async throws {
        let servers = Array(Self.unique.prefix(5))
        let store = self.store
        let results = try await withThrowingTaskGroup(of: AddResult.self) { group in
            for _ in 0..<3 {
                group.addTask { try await store.add(servers) }
            }

            return try await group.reduce(into: [AddResult]()) { $0.append($1) }
        }

        XCTAssertEqual(results.map(\.added).reduce(0, +), servers.count)
        XCTAssertEqual(results.map(\.duplicates).reduce(0, +), 2 * servers.count)
        let stored = try await store.servers()
        XCTAssertEqual(stored.map(\.linkName), servers.map(\.name))
    }

    func testTheSameServerImportedAgainTakesThePinnedCertificateAndKeepsItsName() async throws {
        let insecure = try XCTUnwrap(LinkParser.parse("trojan://pw-a@198.51.100.1:443?sni=example.com&allowInsecure=1#Self-signed").server)
        let first = try await store.add([insecure])
        XCTAssertEqual(first, AddResult(added: 1, duplicates: 0))
        let id = try await store.servers()[0].id
        try await store.rename(id, "Мой сервер")

        // Тот же сервер с закрепленным сертификатом — дубль, но защита берется из новой ссылки (решение 484).
        let pinned = try XCTUnwrap(LinkParser.parse("trojan://pw-a@198.51.100.1:443?sni=example.com&pcs=a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90#Pinned").server)
        let again = try await store.add([pinned])
        XCTAssertEqual(again, AddResult(added: 0, duplicates: 1))

        let loaded = try await store.load(id)
        XCTAssertEqual(loaded?.security.pinnedPeerCertSha256, ["a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90"])
        XCTAssertEqual(loaded?.security.allowInsecure, false)
        let item = try await store.servers()[0]
        XCTAssertEqual(item.displayName, "Мой сервер", "свое название остается")
    }

    func testServerOfASubscriptionWithAnAddressTakesItsProtectionOnlyFromTheRefresh() async throws {
        let pin = "a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90"
        let subscribed = try XCTUnwrap(LinkParser.parse("trojan://pw-a@198.51.100.1:443?sni=example.com&allowInsecure=1#A").server)
        let fromFile = try XCTUnwrap(LinkParser.parse("trojan://pw-b@198.51.100.2:443?sni=example.com&allowInsecure=1#B").server)
        try await store.add([subscribed], group: "Провайдер", url: URLComponents(string: "https://example.com/sub/not-a-real-token"))
        try await store.add([fromFile], group: "Из файла")
        let items = try await store.servers()
        let a = try XCTUnwrap(items.first { $0.linkName == "A" }).id
        let b = try XCTUnwrap(items.first { $0.linkName == "B" }).id

        // Сервер подписки с адресом ведет ее обновление, иначе импорт и обновление переписывали бы защиту по очереди; группа без
        // адреса (профиль из файла) не обновляется — ее сервер берет защиту из импорта, как «Мои серверы» (как у десктопа).
        let pinned = try [
            XCTUnwrap(LinkParser.parse("trojan://pw-a@198.51.100.1:443?sni=example.com&pcs=\(pin)#A").server),
            XCTUnwrap(LinkParser.parse("trojan://pw-b@198.51.100.2:443?sni=example.com&pcs=\(pin)#B").server),
        ]
        let result = try await store.add(pinned)
        XCTAssertEqual(result, AddResult(added: 0, duplicates: 2))

        let loadedA = try await store.load(a)
        let loadedB = try await store.load(b)
        XCTAssertNil(loadedA?.security.pinnedPeerCertSha256)
        XCTAssertEqual(loadedB?.security.pinnedPeerCertSha256, [pin])
    }

    func testOwnNameIsShownInTheListWhileTheLinkKeepsItsName() async throws {
        let server = try XCTUnwrap(Self.unique.first)
        try await store.add([server])
        let id = try await store.servers()[0].id

        try await store.rename(id, "  Мой сервер ")
        let item = try await store.servers()[0]
        XCTAssertEqual(item.displayName, "Мой сервер")
        XCTAssertEqual(item.linkName, server.name)
        let loaded = try await store.load(id)
        XCTAssertEqual(loaded?.name, server.name, "подписка узнает сервер по названию из ссылки, «Поделиться» отдает его же")

        try await store.rename(id, "   ")
        let back = try await store.servers()[0]
        XCTAssertNil(back.customName, "пустое — снова название из ссылки")
    }

    func testNewServersGoAfterTheOldOnes() async throws {
        try await store.add(Array(Self.unique.prefix(2)))
        try await store.add(Array(Self.unique.dropFirst(2).prefix(2)))
        let names = try await store.servers().map(\.linkName)
        XCTAssertEqual(names, Self.unique.prefix(4).map(\.name))
    }

    func testLatencyFavoriteAndChoiceAreStoredPerServer() async throws {
        try await store.add(Array(Self.unique.prefix(3)))
        let ids = try await store.servers().map(\.id)

        try await store.setLatency([ids[0]: .of(42), ids[1]: .failed(.noResponse)])
        try await store.setFavorite(ids[0], true)
        try await store.markUsed(ids[2], at: Date(timeIntervalSince1970: 100))
        var items = Dictionary(uniqueKeysWithValues: try await store.servers().map { ($0.id, $0) })
        XCTAssertEqual(items[ids[0]]?.latency, .of(42))
        XCTAssertEqual(items[ids[1]]?.latency, .failed(.noResponse))
        XCTAssertNil(items[ids[2]]?.latency, "не мерили — не «нет ответа»")
        XCTAssertEqual(items[ids[0]]?.favorite, true)
        XCTAssertEqual(items[ids[2]]?.lastUsedAt, Date(timeIntervalSince1970: 100))

        try await store.setLatency([ids[1]: .of(7)])
        items = Dictionary(uniqueKeysWithValues: try await store.servers().map { ($0.id, $0) })
        XCTAssertEqual(items[ids[1]]?.latency, .of(7), "новый замер снимает причину")
    }

    func testCardAddressComesFromTheLinkWithIpv6InBrackets() async throws {
        let ipv6 = try XCTUnwrap(Self.unique.first { $0.host.contains(":") })
        try await store.add([ipv6])
        let id = try await store.servers()[0].id
        let address = try await store.address(id)
        XCTAssertEqual(address, "[\(ipv6.host)]:\(ipv6.port)")
        let gone = try await store.address("gone")
        XCTAssertNil(gone)
        let link = try await store.shareLink(id)
        XCTAssertEqual(link.flatMap { LinkParser.parse($0).server }, ipv6, "«Поделиться» — та же ссылка")
    }

    // MARK: Группы

    func testSubscriptionKeepsItsAddressOnlyInTheVault() async throws {
        let result = try await store.add(Array(Self.unique.prefix(3)), group: " Провайдер ", url: url)
        XCTAssertEqual(result, AddResult(added: 3, duplicates: 0))
        let groups = try await store.groups()
        let group = try XCTUnwrap(groups.first)
        XCTAssertEqual(group.name, "Провайдер")
        XCTAssertEqual(group.servers, 3)
        XCTAssertEqual(group.maskedUrl, "https://example.com/sub/••••")
        XCTAssertTrue(group.canUpdate)
        XCTAssertNotNil(group.updatedAt, "подписка по адресу только что загружена")
        let stored = try await store.subscriptionUrl(group.id)
        XCTAssertEqual(stored?.string, url.string)
        let manual = try await store.manualCount()
        XCTAssertEqual(manual, 0)
        XCTAssertFalse(try state().contains("0123456789abcdef0123456789abcdef"), "токена подписки нет открытым текстом")
    }

    func testProfileWithoutAnAddressDoesNotUpdateAndServersWithoutAGroupAreMine() async throws {
        try await store.add(Array(Self.unique.prefix(2)), group: "Профиль", url: nil)
        try await store.add(Array(Self.unique.dropFirst(2).prefix(1)), group: "   ", url: url)
        let groups = try await store.groups()
        XCTAssertEqual(groups.count, 1)
        XCTAssertNil(groups[0].maskedUrl)
        XCTAssertFalse(groups[0].canUpdate)
        let stored = try await store.subscriptionUrl(groups[0].id)
        XCTAssertNil(stored)
        let manual = try await store.manualCount()
        XCTAssertEqual(manual, 1, "без названия — в «Мои серверы», адрес не хранится")
        let refs = try await store.subscriptionRefs()
        XCTAssertEqual(refs.map(\.name), ["Профиль"])
        XCTAssertNil(refs[0].updatedAt, "профиль без адреса не обновлялся")
    }

    func testUpdateKeepsOwnNameFavoriteLatencyAndChoiceAndLeavesTheSelectedServerMarkedMissing() async throws {
        let (a, b, c, d) = (renamed(Self.unique[0], "S0"), renamed(Self.unique[1], "S1"), renamed(Self.unique[2], "S2"), renamed(Self.unique[3], "S3"))
        try await store.add([a, b, c], group: "Подписка", url: url)
        let group = try await store.groups()[0].id
        let ids = try await store.servers().map(\.id)
        try await store.rename(ids[0], "Мой")
        try await store.setFavorite(ids[0], true)
        try await store.setLatency([ids[0]: .of(42)])
        try await store.markUsed(ids[0], at: Date(timeIntervalSince1970: 100))

        // Пришли a и новый d; b ушел; c ушел, но он выбран — остается с пометкой.
        let result = try await store.applyUpdate(group, incoming: [a, d], selectedId: ids[2])
        XCTAssertEqual(result, UpdateResult(added: 1, removed: 1, selected: .missing, selectedName: "S2"))

        let after = Dictionary(uniqueKeysWithValues: try await store.servers().map { ($0.linkName, $0) })
        XCTAssertEqual(Set(after.keys), ["S0", "S2", "S3"])
        let kept = try XCTUnwrap(after["S0"])
        XCTAssertEqual(kept.id, ids[0])
        XCTAssertEqual(kept.customName, "Мой")
        XCTAssertTrue(kept.favorite)
        XCTAssertEqual(kept.latency, .of(42))
        XCTAssertEqual(kept.lastUsedAt, Date(timeIntervalSince1970: 100))
        XCTAssertNil(kept.missingSince)
        XCTAssertNotNil(after["S2"]?.missingSince)
        XCTAssertEqual(after["S3"]?.subscriptionId, group)
        let removedLink = try vault.load(ServerStore.serverKey(ids[1]))
        XCTAssertNil(removedLink, "ссылка удаленного сервера убрана из Связки ключей")

        let subscription = try await store.groups()[0]
        XCTAssertNil(subscription.failure)
        XCTAssertNotNil(subscription.updatedAt)
    }

    func testSelectedServerWhoseNameCameBackWithOtherKeysIsChanged() async throws {
        let (a, b, c) = (renamed(Self.unique[0], "S0"), renamed(Self.unique[1], "S1"), renamed(Self.unique[2], "S1"))
        try await store.add([a, b], group: "Подписка", url: url)
        let group = try await store.groups()[0].id
        let selected = try await store.servers()[1].id

        // Под тем же названием — другой сервер: у поставщика новый адрес или ключи.
        let result = try await store.applyUpdate(group, incoming: [a, c], selectedId: selected)
        XCTAssertEqual(result.selected, .changed)
        XCTAssertEqual(result.selectedName, "S1")
    }

    func testServerWhoseLinkIsGoneCountsAsGone() async throws {
        try await store.add(Array(Self.unique.prefix(2)), group: "Подписка", url: url)
        let group = try await store.groups()[0].id
        let broken = try await store.servers()[1].id
        try vault.remove(ServerStore.serverKey(broken))

        let result = try await store.applyUpdate(group, incoming: Array(Self.unique.prefix(1)), selectedId: nil)
        XCTAssertEqual(result.removed, 1)
        let left = try await store.servers()
        XCTAssertEqual(left.count, 1)
    }

    func testFailureIsRememberedAndANewAddressClearsIt() async throws {
        try await store.add(Array(Self.unique.prefix(1)), group: "Подписка", url: url)
        let group = try await store.groups()[0].id
        try await store.markFailed(group, failure: .status, status: 403)
        let failed = try await store.groups()[0]
        XCTAssertEqual(failed.failure, .status)
        XCTAssertEqual(failed.status, 403)
        XCTAssertNotNil(failed.lastAttemptAt)

        try await store.edit(group, name: "Новое", url: nil)
        let renamedOnly = try await store.groups()[0]
        XCTAssertEqual(renamedOnly.failure, .status, "только название — неудача остается")

        let next = try XCTUnwrap(SubscriptionAddress.parse("https://example.org/feed/abcdefabcdefabcdefabcdef"))
        try await store.edit(group, name: "Новое", url: next)
        let edited = try await store.groups()[0]
        XCTAssertEqual(edited.name, "Новое")
        XCTAssertNil(edited.failure)
        XCTAssertNil(edited.status)
        XCTAssertEqual(edited.maskedUrl, "https://example.org/feed/••••")
        let stored = try await store.subscriptionUrl(group)
        XCTAssertEqual(stored?.string, next.string)
    }

    func testRemovingAGroupTakesItsServersAndReturnsThem() async throws {
        try await store.add(Array(Self.unique.prefix(2)), group: "Подписка", url: url)
        try await store.add(Array(Self.unique.dropFirst(2).prefix(1)))
        let group = try await store.groups()[0].id
        let ids = try await store.servers().filter { $0.subscriptionId == group }.map(\.id)

        let removed = try await store.remove(group: group)
        XCTAssertEqual(removed, ids)
        let groups = try await store.groups()
        XCTAssertTrue(groups.isEmpty)
        let left = try await store.servers()
        XCTAssertEqual(left.count, 1, "«Мои серверы» не тронуты")
        let address = try await store.subscriptionUrl(group)
        XCTAssertNil(address, "адрес подписки убран из Связки ключей")

        let mine = try await store.remove(group: nil)
        XCTAssertEqual(mine.count, 1)
        let none = try await store.servers()
        XCTAssertTrue(none.isEmpty)
        XCTAssertTrue(vault.all.isEmpty, "в Связке ключей ничего не осталось")
    }

    func testStateSurvivesANewStoreOnTheSameFile() async throws {
        try await store.add(Array(Self.unique.prefix(2)), group: "Подписка", url: url)
        // Расширение туннеля открывает то же хранилище своим экземпляром — и видит то же.
        let other = ServerStore(file: file, vault: vault)
        let mine = try await store.servers()
        let theirs = try await other.servers()
        XCTAssertEqual(theirs, mine)
        let loaded = try await other.load(mine[0].id)
        XCTAssertEqual(loaded, Self.unique[0])
    }
}

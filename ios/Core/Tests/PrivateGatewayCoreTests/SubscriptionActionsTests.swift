import Foundation
import XCTest
@testable import PrivateGatewayCore

/// «Профили и подписки» — те же случаи, что `SubscriptionsViewModelTest` Android: неверный новый адрес не сохраняется, новый
/// адрес хранится маской и сразу проверяется, удаление группы в работе отключает защиту и снимает выбор; тексты итогов.
final class SubscriptionActionsTests: XCTestCase {
    private let source = StubSource()
    private let control = FakeControl()
    private lazy var servers = ServerStore(file: MemoryFile(), vault: MemoryVault())
    private lazy var settings = SettingsStore(file: MemoryFile())
    private lazy var actions = SubscriptionActions(
        servers: servers, settings: settings, control: control, updater: SubscriptionUpdater(source: source, servers: servers, settings: settings)
    )

    private func link(_ path: String) throws -> ServerDefinition {
        let text = try String(contentsOf: LinkParserTests.testData.appendingPathComponent("links/\(path).txt"), encoding: .utf8)
        return try XCTUnwrap(LinkParser.parse(text.trimmingCharacters(in: .whitespacesAndNewlines)).server)
    }

    private func group() async throws -> String {
        let list = try [link("vless/reality-vision"), link("trojan/ws")]
        _ = try await servers.add(list, group: "Провайдер", url: SubscriptionAddress.parse("https://example.com/sub/aaaa1111bbbb2222"))
        let groups = try await servers.groups()
        return try XCTUnwrap(groups.first).id
    }

    func testABadNewAddressIsRefusedAndNothingChanges() async throws {
        let id = try await group()
        let result = try await actions.edit(id, name: "Новое", url: "ftp://example.com/sub", connected: true)
        XCTAssertEqual(result, .badAddress)
        let groups = try await servers.groups()
        XCTAssertEqual(groups.first { $0.id == id }?.name, "Провайдер")
        XCTAssertEqual(source.downloads, 0)
    }

    func testANewAddressIsSavedMaskedAndCheckedAtOnce() async throws {
        let id = try await group()
        let result = try await actions.edit(id, name: " Новое ", url: "https://example.org/new/cccc3333dddd4444", connected: false)
        // Проверка загрузкой сразу — без защиты ее не будет, и окно это скажет.
        XCTAssertEqual(result, .saved(UpdateReport(.blocked(.protectionOff))))
        let groups = try await servers.groups()
        let edited = try XCTUnwrap(groups.first)
        XCTAssertEqual(edited.id, id)
        XCTAssertEqual(edited.name, "Новое")
        XCTAssertEqual(edited.maskedUrl, "https://example.org/new/••••")

        let renamed = try await actions.edit(id, name: "Еще", url: "  ", connected: true)
        XCTAssertEqual(renamed, .saved(nil), "пустой адрес — прежний, без загрузки")
        XCTAssertEqual(source.downloads, 0)
    }

    func testRemovingTheGroupInUseDisconnectsAndClearsTheChoice() async throws {
        let id = try await group()
        let all = try await servers.servers()
        let selected = try XCTUnwrap(all.first).id
        try await settings.select(selected)

        try await actions.remove(group: id, inUse: true)
        XCTAssertEqual(control.disconnects, 1)
        let left = try await servers.servers()
        XCTAssertTrue(left.isEmpty)
        let choice = try await settings.load().selectedServerId
        XCTAssertNil(choice)
    }

    func testRemovingMineKeepsTheSubscriptionAndTheChoice() async throws {
        let id = try await group()
        _ = try await servers.add([link("vmess/ws-tls")])
        let all = try await servers.servers()
        let selected = try XCTUnwrap(all.first { $0.subscriptionId == id }).id
        try await settings.select(selected)

        try await actions.remove(group: nil, inUse: false)
        XCTAssertEqual(control.disconnects, 0)
        let left = try await servers.servers()
        XCTAssertEqual(left.count, 2)
        let choice = try await settings.load().selectedServerId
        XCTAssertEqual(choice, selected)
    }

    func testOutcomesAndNoticesHaveTexts() {
        XCTAssertEqual(TextKeys.updated(.updated(added: 2, removed: 1), name: "Провайдер")?.arguments, ["Провайдер", "2", "1"])
        XCTAssertEqual(TextKeys.updated(.failed(.timeout), name: "Провайдер")?.key, "subs.failed")
        XCTAssertEqual(TextKeys.updated(.blocked(.protectionOff), name: "Провайдер")?.key, "subs.blocked.off")
        XCTAssertNil(TextKeys.updated(.blocked(.busy), name: "Провайдер"), "второе нажатие — молча")
        XCTAssertNil(TextKeys.updated(.gone, name: "Провайдер"))
        XCTAssertEqual(TextKeys.notice(.gone(server: "A", subscription: "B")).key, "subs.notice.missing")
        XCTAssertEqual(TextKeys.notice(.changed(server: "A", subscription: "B")).arguments, ["A", "B"])
        XCTAssertEqual(TextKeys.notice(.stale(subscription: "B", reason: .timeout)).key, "subs.failed")
    }
}

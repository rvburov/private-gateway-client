import Foundation
import XCTest
@testable import PrivateGatewayCore

/// Подписка «по сети» без сети: ответ или неудача, которые задал тест; запоминает, сколько раз загружали.
final class StubSource: SubscriptionSource, @unchecked Sendable {
    var content = SubscriptionContent(text: "", title: nil)
    var failure: SubscriptionDownloadError?
    private(set) var downloads = 0

    func download(_ url: URLComponents) async throws -> SubscriptionContent {
        downloads += 1
        if let failure {
            throw failure
        }

        return content
    }
}

/// «Добавить» — те же случаи, что `MainViewModelTest` Android: разбор текста, адрес подписки загружается и называется по
/// Profile-Title, http предупреждает об открытых ключах, без названия серверы идут в «Мои серверы» и адрес не хранится,
/// первый импорт выбирает первый новый сервер, дубли считаются.
final class ImportFlowTests: XCTestCase {
    private let source = StubSource()
    private let vault = MemoryVault()
    private lazy var servers = ServerStore(file: MemoryFile(), vault: vault)
    private lazy var settings = SettingsStore(file: MemoryFile())

    private func link(_ path: String) throws -> String {
        try String(contentsOf: LinkParserTests.testData.appendingPathComponent("links/\(path).txt"), encoding: .utf8)
            .trimmingCharacters(in: .whitespacesAndNewlines)
    }

    func testLinksAreParsedWithoutDownloading() async throws {
        let text = try [link("vless/reality-vision"), link("trojan/ws"), "garbage"].joined(separator: "\n")
        let preview = await ImportFlow.preview(text, source: source)
        XCTAssertEqual(source.downloads, 0)
        XCTAssertNil(preview.url)
        XCTAssertEqual(preview.batch?.found, 3)
        XCTAssertEqual(preview.batch?.failed, 1)
        XCTAssertNil(preview.suggestedName)
    }

    func testSubscriptionAddressIsDownloadedAndNamedByItsTitle() async throws {
        source.content = SubscriptionContent(text: try [link("vless/reality-vision"), link("vmess/ws-tls")].joined(separator: "\n"), title: "Мой VPN")
        let preview = await ImportFlow.preview("  https://example.com/sub/0123456789abcdef  ", source: source)
        XCTAssertEqual(source.downloads, 1)
        XCTAssertEqual(preview.batch?.found, 2)
        XCTAssertEqual(preview.url?.host, "example.com")
        XCTAssertFalse(preview.insecure)
        XCTAssertEqual(preview.suggestedName, "Мой VPN")

        source.content = SubscriptionContent(text: try link("trojan/ws"), title: nil)
        let plain = await ImportFlow.preview("http://example.org/sub", source: source)
        XCTAssertTrue(plain.insecure, "http — ключи шли без шифрования")
        XCTAssertEqual(plain.suggestedName, "example.org", "без Profile-Title — сервер подписки")
    }

    func testFailedDownloadSaysWhy() async {
        source.failure = SubscriptionDownloadError(.status, status: 404)
        let notFound = await ImportFlow.preview("https://example.com/sub", source: source)
        XCTAssertEqual(notFound.failure, .notFound)
        XCTAssertNil(notFound.batch)

        source.failure = SubscriptionDownloadError(.timeout)
        let timeout = await ImportFlow.preview("https://example.com/sub", source: source)
        XCTAssertEqual(timeout.failure, .timeout)
    }

    func testAddressAmongOtherLinesIsText() async throws {
        let preview = await ImportFlow.preview(try link("trojan/ws") + "\nhttps://example.com/sub", source: source)
        XCTAssertEqual(source.downloads, 0, "несколько строк — текст, а не адрес подписки")
        XCTAssertEqual(preview.batch?.found, 2)
    }

    func testFirstImportSelectsTheFirstNewServerAndALaterOneKeepsTheChoice() async throws {
        let first = ImportParser.parse(try link("vless/reality-vision"))
        try await ImportFlow.add(first, name: "", url: nil, servers: servers, settings: settings)
        let items = try await servers.servers()
        let selected = try await settings.load().selectedServerId
        XCTAssertEqual(selected, items.first?.id)

        let second = ImportParser.parse(try [link("trojan/ws"), link("vless/reality-vision")].joined(separator: "\n"))
        let result = try await ImportFlow.add(second, name: "", url: nil, servers: servers, settings: settings)
        XCTAssertEqual(result, AddResult(added: 1, duplicates: 1))
        let still = try await settings.load().selectedServerId
        XCTAssertEqual(still, selected, "выбор не меняется")
    }

    func testWithANameASubscriptionIsAGroupAndWithoutItTheAddressIsNotKept() async throws {
        let url = try XCTUnwrap(SubscriptionAddress.parse("https://example.com/sub/0123456789abcdef"))
        let batch = ImportParser.parse(try link("trojan/ws"))
        try await ImportFlow.add(batch, name: "  ", url: url, servers: servers, settings: settings)
        var groups = try await servers.groups()
        XCTAssertTrue(groups.isEmpty, "без названия — «Мои серверы»")
        XCTAssertTrue(vault.all.keys.allSatisfy { !$0.hasPrefix("subscription.") }, "адрес без названия не хранится")

        try await ImportFlow.add(ImportParser.parse(try link("vmess/ws-tls")), name: "Работа", url: url, servers: servers, settings: settings)
        groups = try await servers.groups()
        XCTAssertEqual(groups.map(\.name), ["Работа"])
        XCTAssertTrue(groups[0].canUpdate, "подписка по адресу обновляется")
    }
}

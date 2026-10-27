import PrivateGatewayCore
import PrivateGatewayTheme
import SwiftUI
import UIKit
import XCTest
@testable import PrivateGateway

/// Подписка без сети: ответ, который задал тест.
private struct FixedSource: SubscriptionSource {
    let text: String

    func download(_ url: URLComponents) async throws -> SubscriptionContent {
        SubscriptionContent(text: text, title: "Мой VPN")
    }
}

/// Экраны «Серверы», «Добавить», карточка и островок на симуляторе; QR туда и обратно по всем эталонным ссылкам; добавление
/// через модель экрана — в общую папку.
final class ScreensTests: XCTestCase {
    private static let testData: URL = {
        var url = URL(fileURLWithPath: #filePath)
        url.deleteLastPathComponent()
        url.deleteLastPathComponent()
        url.deleteLastPathComponent()
        return url.appendingPathComponent("shared/testdata")
    }()

    private static func links() -> [String] {
        let folder = testData.appendingPathComponent("links")
        let files = (FileManager.default.enumerator(atPath: folder.path)?.compactMap { $0 as? String } ?? []).filter { $0.hasSuffix(".txt") }.sorted()
        return files.compactMap { try? String(contentsOf: folder.appendingPathComponent($0), encoding: .utf8) }
            .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
            .filter { !$0.isEmpty }
    }

    private var directory: URL!

    override func setUpWithError() throws {
        directory = FileManager.default.temporaryDirectory.appendingPathComponent("pg-app-\(UUID().uuidString)", isDirectory: true)
    }

    override func tearDownWithError() throws {
        if FileManager.default.fileExists(atPath: directory.path) {
            try FileManager.default.removeItem(at: directory)
        }
    }

    private let servers = [
        ServerItem(id: "a", linkName: "Нидерланды 01", countryCode: "NL", protocol: .vless, security: .reality, transport: .tcp, port: 443, subscriptionId: "s",
                   favorite: true, latency: .of(48)),
        ServerItem(id: "b", linkName: "Германия", countryCode: "DE", protocol: .trojan, security: .tls, transport: .webSocket, port: 443, latency: .of(180),
                   missingSince: Date()),
        ServerItem(id: "c", linkName: "Финляндия", countryCode: "", protocol: .hysteria2, security: .tls, transport: .quic, port: 8443,
                   latency: .failed(.noResponse)),
    ]

    @MainActor
    private func render(_ view: some View, height: CGFloat = 852) throws -> UIImage {
        try XCTUnwrap(ImageRenderer(content: view.frame(width: 393, height: height)).uiImage)
    }

    @MainActor
    func testQrCodeGoesThereAndBackForEveryFixtureLink() throws {
        let links = Self.links() + ["https://example.com/sub/0123456789abcdef0123456789abcdef?flag=1"]
        XCTAssertGreaterThanOrEqual(links.count, 15, "эталонов слишком мало")
        for link in links {
            let image = try XCTUnwrap(QrCode.image(link), link)
            let cg = try XCTUnwrap(image.cgImage)
            XCTAssertEqual(try QrCode.read(cg), link)
        }
    }

    @MainActor
    func testPictureWithoutACodeIsNil() throws {
        let blank = UIGraphicsImageRenderer(size: CGSize(width: 64, height: 64)).image { context in
            UIColor.white.setFill()
            context.fill(CGRect(x: 0, y: 0, width: 64, height: 64))
        }
        XCTAssertNil(try QrCode.read(try XCTUnwrap(blank.pngData())))
        XCTAssertThrowsError(try QrCode.read(Data("not an image".utf8)))
    }

    @MainActor
    func testServersDrawInEveryTabAndLanguage() throws {
        let subscriptions = [SubscriptionRef(id: "s", name: "Работа", updatedAt: Date())]
        for language in PgTexts.Language.allCases {
            for tab in [ServerListTab.all, .favorites, .recent] {
                let home = HomeState(status: TunnelStatus(state: .connected, serverId: "a"), servers: servers, selectedId: "b", subscriptions: subscriptions)
                let view = ServersView(texts: PgTexts(language), home: home, query: .constant(ServerListQuery(tab: tab)))
                XCTAssertEqual(try render(view).size.width, 393, accuracy: 1)
            }

            let empty = ServersView(texts: PgTexts(language), home: HomeState(), query: .constant(ServerListQuery()))
            XCTAssertNotNil(try render(empty))
        }
    }

    @MainActor
    func testCardAndIslandDraw() throws {
        let texts = PgTexts(.russian)
        let home = HomeState(status: TunnelStatus(state: .connected, serverId: "a"), servers: servers, selectedId: "a")
        for id in ["a", "b", "c"] {
            XCTAssertNotNil(try render(ServerCardView(texts: texts, home: home, id: id, actions: CardActions())))
        }

        for state in TunnelState.allCases {
            for tab in [AppTab.home, .servers] {
                let island = Island(texts: texts, tab: tab, state: state, hasServer: state != .off, onTab: { _ in }, onPower: {})
                XCTAssertNotNil(try render(island, height: 160))
            }
        }
    }

    @MainActor
    func testImportChecksAddsAndSelectsTheFirstServer() async throws {
        let links = Self.links()
        let container = SharedContainer(directory: directory, vault: MemoryVault())
        let model = ImportModel(container: container, source: FixedSource(text: links.joined(separator: "\n")))
        var added = 0
        model.onAdded = { added += 1 }

        await model.check("https://example.com/sub/0123456789abcdef")
        let batch = try XCTUnwrap(model.preview?.batch)
        XCTAssertEqual(batch.found, links.count)
        XCTAssertEqual(model.name, "Мой VPN", "название — из Profile-Title")
        XCTAssertNotNil(try render(ImportView(model: model, texts: PgTexts(.russian), onClose: {})))

        await model.add()
        let good = batch.found - batch.failed
        XCTAssertEqual((model.added ?? 0) + model.duplicates, good)
        XCTAssertEqual(added, 1)
        let stored = try await container.servers.servers()
        XCTAssertEqual(stored.count, model.added)
        let selected = try await container.settings.load().selectedServerId
        XCTAssertEqual(selected, stored.first?.id, "первый импорт выбирает первый новый сервер")
        let groups = try await container.servers.groups()
        XCTAssertEqual(groups.map(\.name), ["Мой VPN"])

        await model.add()
        XCTAssertEqual(added, 1, "второе нажатие не считается")
        XCTAssertNotNil(try render(ImportView(model: model, texts: PgTexts(.english), onClose: {})))
    }

    @MainActor
    func testQrFromAPictureIsCheckedLikeText() async throws {
        let link = try XCTUnwrap(Self.links().first)
        let model = ImportModel(container: SharedContainer(directory: directory, vault: MemoryVault()), source: FixedSource(text: ""))
        let png = try XCTUnwrap(QrCode.image(link)?.pngData())
        await model.readQr(png)
        XCTAssertEqual(model.preview?.batch?.found, 1)
        XCTAssertEqual(model.text, "", "в поле код не попадает")

        await model.readQr(Data("not an image".utf8))
        XCTAssertEqual(model.qrProblem, "android.import.qr.failed")
        XCTAssertNotNil(PgTexts(.russian).text(key: try XCTUnwrap(model.qrProblem)))
    }
}

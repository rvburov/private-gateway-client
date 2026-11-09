import PrivateGatewayCore
import PrivateGatewayTheme
import SwiftUI
import UIKit
import XCTest
@testable import PrivateGateway

/// Подписка без сети: пустой ответ.
private struct EmptySource: SubscriptionSource {
    func download(_ url: URLComponents) async throws -> SubscriptionContent {
        SubscriptionContent(text: "", title: nil)
    }
}

/// «Серверы» и карточка сервера по макету v1.1 (доски 02 и 11): метка строки — как у десктопа, «активен» только под
/// подключенной защитой; Hysteria2 и TUIC не мерятся и не подключаются; «Сбросить» не трогает поиск и вкладку; карточка —
/// откуда сервер, чип состояния, «нет ответа», сведения, «Сохранить», удаление только после «да», «Ссылка скопирована» и
/// «Сервер удален»; список с открытой панелью фильтра и каждая страница карточки рисуются на обоих языках.
final class ServersTests: XCTestCase {
    private let texts = PgTexts(.russian)

    private let servers = [
        ServerItem(id: "a", linkName: "Нидерланды 01", countryCode: "NL", protocol: .vless, security: .reality, transport: .tcp, port: 443, subscriptionId: "s",
                   favorite: true, latency: .of(42)),
        ServerItem(id: "b", linkName: "Германия 02", countryCode: "DE", protocol: .trojan, security: .tls, transport: .webSocket, port: 443, latency: .of(148),
                   missingSince: Date()),
        ServerItem(id: "h", linkName: "Финляндия 01", countryCode: "FI", protocol: .hysteria2, security: .tls, transport: .quic, port: 8443,
                   latency: .failed(.unsupported)),
        ServerItem(id: "t", linkName: "Турция 01", countryCode: "", protocol: .vless, security: .tls, transport: .grpc, port: 443, latency: .failed(.noResponse)),
    ]

    private let subscriptions = [SubscriptionRef(id: "s", name: "Основная подписка", updatedAt: Date())]

    /// Эталонная ссылка VLESS из shared/testdata — заведомо ненастоящая.
    private static let fixture: URL = {
        var url = URL(fileURLWithPath: #filePath)
        url.deleteLastPathComponent()
        url.deleteLastPathComponent()
        url.deleteLastPathComponent()
        return url.appendingPathComponent("shared/testdata/links/vless/private-gateway.txt")
    }()

    private var directory: URL!

    override func setUpWithError() throws {
        directory = FileManager.default.temporaryDirectory.appendingPathComponent("pg-servers-\(UUID().uuidString)", isDirectory: true)
    }

    override func tearDownWithError() throws {
        if FileManager.default.fileExists(atPath: directory.path) {
            try FileManager.default.removeItem(at: directory)
        }
    }

    @MainActor
    private func render(_ view: some View, height: CGFloat = 852) throws -> UIImage {
        try XCTUnwrap(ImageRenderer(content: view.frame(width: 393, height: height)).uiImage)
    }

    @MainActor
    func testRowTagIsActiveOnlyWhileConnected() {
        let connected = HomeState(status: TunnelStatus(state: .connected, serverId: "a"), servers: servers, selectedId: "a")
        XCTAssertEqual(ServersLook.tag(servers[0], home: connected, texts), texts.serversTagActive)
        XCTAssertNil(ServersLook.tag(servers[1], home: connected, texts), "у других строк метки нет")

        // «Подключение», «Переподключение» и выключение — еще или уже не «активен» (data-show-state доски 02).
        for state in [TunnelState.connecting, .reconnecting, .stopping] {
            let home = HomeState(status: TunnelStatus(state: state, serverId: "a"), servers: servers, selectedId: "a")
            XCTAssertEqual(ServersLook.tag(servers[0], home: home, texts), texts.serversTagSelected, "\(state)")
        }

        let off = HomeState(servers: servers, selectedId: "b")
        XCTAssertEqual(ServersLook.tag(servers[1], home: off, texts), texts.serversTagSelected)
        XCTAssertNil(ServersLook.tag(servers[0], home: off, texts))
        XCTAssertNil(ServersLook.tag(servers[0], home: HomeState(servers: servers), texts), "ничего не выбрано — меток нет")

        // Под защитой — сервер в работе, а не выбранный; расширение не назвало сервер — выбранный.
        let other = HomeState(status: TunnelStatus(state: .connected, serverId: "a"), servers: servers, selectedId: "b")
        XCTAssertEqual(ServersLook.tag(servers[0], home: other, texts), texts.serversTagActive)
        XCTAssertNil(ServersLook.tag(servers[1], home: other, texts))
        let unnamed = HomeState(status: TunnelStatus(state: .connected), servers: servers, selectedId: "b")
        XCTAssertEqual(ServersLook.tag(servers[1], home: unnamed, texts), texts.serversTagActive)
    }

    @MainActor
    func testHysteria2AndTuicAreNeitherMeasuredNorConnected() {
        XCTAssertTrue(ServersLook.supported(servers[0]))
        XCTAssertTrue(ServersLook.supported(servers[1]))
        XCTAssertFalse(ServersLook.supported(servers[2]), "Hysteria2 ядру телефона не по силам")
        let tuic = ServerItem(id: "u", linkName: "Польша 03", countryCode: "PL", protocol: .tuic, security: .tls, transport: .quic, port: 443)
        XCTAssertFalse(ServersLook.supported(tuic))

        let unsupported = LatencyLook.of(.failed(.unsupported), texts)
        XCTAssertEqual(unsupported.text, "—", "не мерили — не «нет ответа»")
        XCTAssertEqual(unsupported.color, PgColor.Text.muted)
        XCTAssertEqual(unsupported.hint, texts.androidCardCoreNone)
        XCTAssertEqual(CardLook.latency(.failed(.unsupported), texts).text, "—")

        let silent = LatencyLook.of(.failed(.noResponse), texts)
        XCTAssertEqual(silent.text, texts.serversLatencyNone, "в строке — «нет»")
        XCTAssertEqual(silent.color, PgColor.Status.Warning.text)
        XCTAssertEqual(CardLook.latency(.failed(.noResponse), texts).text, texts.cardLatencyNone, "в чипе карточки — «нет ответа»")
        XCTAssertEqual(CardLook.latency(.failed(.noResponse), texts).color, PgColor.Status.Warning.text)
        XCTAssertEqual(CardLook.latency(.of(42), texts).text, "42 \(texts.unitMs)")
        XCTAssertEqual(CardLook.latency(.of(42), texts).color, PgColor.Status.Success.text)
        XCTAssertEqual(CardLook.latency(.of(148), texts).color, PgColor.Text.muted, "медленный — серым")
        XCTAssertEqual(CardLook.latency(nil, texts).text, texts.latencyUnchecked, "не мерили — «Не проверен», а не отказ (решение 2026-11-08)")

        let hysteria = CardLook.details(servers[2], host: "203.0.113.57:8443", texts)
        XCTAssertEqual(hysteria.first { $0.identifier == "card.core" }?.value, texts.androidCardCoreNone, "ядра для Hysteria2 нет")
        XCTAssertEqual(hysteria.last.map { [$0.label, $0.value] }, [texts.cardLatencyReason, texts.latencyUnsupported], "почему задержки нет")
        XCTAssertEqual(CardLook.details(servers[3], host: "203.0.113.9:443", texts).last?.value, texts.latencyNoResponse)
    }

    @MainActor
    func testResetShowsEveryProtocolAndSortsByPingButKeepsSearchAndTab() {
        let query = ServerListQuery(search: "NL", tab: .favorites, hiddenProtocols: [.vless, .trojan], sort: .name)
        XCTAssertEqual(ServerList.filterCount(servers, query), 3, "два скрытых протокола и порядок — число на кнопке фильтра")
        let reset = ServersLook.reset(query)
        XCTAssertEqual(reset, ServerListQuery(search: "NL", tab: .favorites))
        XCTAssertEqual(ServerList.filterCount(servers, reset), 0)
    }

    @MainActor
    func testCardSaysWhereTheServerComesFromAndWhetherItIsInUse() {
        XCTAssertEqual(CardLook.subtitle(servers[0], subscriptions: subscriptions, texts), texts.cardFromSubscription("Основная подписка"))
        XCTAssertEqual(CardLook.subtitle(servers[3], subscriptions: subscriptions, texts), texts.cardFromLink)
        XCTAssertEqual(CardLook.subtitle(servers[1], subscriptions: subscriptions, texts), texts.cardMissing, "пропал из подписки — в карточке, не в строке")
        let orphan = ServerItem(id: "o", linkName: "Без группы", countryCode: "", protocol: .vless, security: .none, transport: .tcp, port: 80, subscriptionId: "gone")
        XCTAssertEqual(CardLook.subtitle(orphan, subscriptions: subscriptions, texts), texts.cardFromLink, "подписки уже нет")

        for state in [TunnelState.connected, .connecting, .reconnecting] {
            let home = HomeState(status: TunnelStatus(state: state, serverId: "a"), servers: servers, selectedId: "a")
            XCTAssertEqual(CardLook.status(servers[0], home: home, texts), CardStatus(text: texts.cardStatusActive, active: true), "\(state)")
            XCTAssertNil(CardLook.status(servers[1], home: home, texts), "\(state): у другого сервера чипа нет")
        }

        for state in [TunnelState.off, .error] {
            let home = HomeState(status: TunnelStatus(state: state), servers: servers, selectedId: "a")
            XCTAssertEqual(CardLook.status(servers[0], home: home, texts), CardStatus(text: texts.cardStatusSelected, active: false), "\(state)")
        }
    }

    @MainActor
    func testCardDetailsAndRenameRules() {
        XCTAssertEqual(
            Array(CardLook.details(servers[0], host: "203.0.113.24:443", exit: "203.0.113.90", texts).map(\.value).prefix(2)), ["203.0.113.24:443", "203.0.113.90"],
            "сервер «VPS + прокси»: куда подключается телефон и что видят сайты (решение 583)"
        )
        let rows = CardLook.details(servers[0], host: "203.0.113.24:443", texts)
        XCTAssertEqual(rows.map(\.label), [texts.cardAddress, texts.cardExitIp, texts.cardProtocol, texts.cardTransport, texts.cardSecurity, texts.cardCore])
        XCTAssertEqual(rows.map(\.value), ["203.0.113.24:443", texts.cardExitIpUnknown, "VLESS", "TCP", "REALITY", "Xray-core"])
        let open = ServerItem(id: "p", linkName: "Открытый", countryCode: "", protocol: .vless, security: .none, transport: .webSocket, port: 80)
        XCTAssertEqual(CardLook.details(open, host: "—", texts).map(\.value), ["—", texts.cardExitIpUnknown, "VLESS", "WS", texts.cardSecurityNone, "Xray-core"])

        XCTAssertFalse(CardLook.canSave("Нидерланды 01", server: servers[0]), "то же название — сохранять нечего")
        XCTAssertFalse(CardLook.canSave("   ", server: servers[0]))
        XCTAssertFalse(CardLook.canSave(String(repeating: "я", count: ServerNames.maxLength + 1), server: servers[0]))
        XCTAssertTrue(CardLook.canSave("  Дом  ", server: servers[0]))
    }

    @MainActor
    func testDeleteAsksFirstAndOnlyYesDeletes() {
        let inUse = CardLook.deleteDialog("Нидерланды 01", inUse: true, texts)
        XCTAssertEqual(inUse.kind, .danger, "красный значок и красная «Удалить»")
        XCTAssertEqual(inUse.icon, PgIcons.trash)
        XCTAssertEqual(inUse.title, texts.dialogDeleteServerTitle("Нидерланды 01"))
        XCTAssertEqual(inUse.text, texts.dialogDeleteServerTextInUse)
        XCTAssertEqual(inUse.primary, texts.dialogDeleteServerDelete)
        XCTAssertEqual(inUse.secondary, texts.dialogCancel)
        XCTAssertEqual(inUse.yes, .primary)
        XCTAssertEqual(CardLook.deleteDialog("Германия 02", inUse: false, texts).text, texts.dialogDeleteServerText)

        let layers = PgLayers()
        var deleted = 0
        for choice in [PgDialogChoice.secondary, .dismissed] {
            layers.confirm(inUse) { deleted += 1 }
            layers.answer(choice)
            XCTAssertEqual(deleted, 0, "\(choice): «Отмена» и нажатие мимо ничего не удаляют")
        }

        layers.confirm(inUse) { deleted += 1 }
        layers.answer(.primary)
        XCTAssertEqual(deleted, 1)
    }

    @MainActor
    func testCopyingTheLinkSaysSo() {
        let layers = PgLayers()
        var copied: [String] = []
        let link = "vless://11111111-2222-3333-4444-555555555555@203.0.113.24:443?security=tls#Example"
        CardShare.copy(link, layers: layers, texts: texts) { copied.append($0) }
        XCTAssertEqual(copied, [link])
        XCTAssertEqual(layers.toast, texts.cardShareCopied, "«Ссылка скопирована»")
        layers.hideToast()
    }

    /// Удаление через модель окна: сервера больше нет, и окно говорит «Сервер удален» (доска 11).
    @MainActor
    func testDeletingAServerSaysSo() async throws {
        let container = SharedContainer(directory: directory, vault: MemoryVault())
        let model = AppModel(container: container, texts: texts, source: EmptySource())
        let link = try String(contentsOf: Self.fixture, encoding: .utf8).trimmingCharacters(in: .whitespacesAndNewlines)
        let server = try XCTUnwrap(LinkParser.parse(link).server)
        _ = try await container.servers.add([server])
        await model.reload()
        let id = try XCTUnwrap(model.home.servers.first).id

        await model.delete(id)
        XCTAssertTrue(model.home.servers.isEmpty)
        XCTAssertEqual(model.message, texts.cardDeleted)
    }

    @MainActor
    func testListWithTheFilterOpenAndEveryCardPageDraw() throws {
        let link = "vless://11111111-2222-3333-4444-555555555555@203.0.113.24:443?security=tls#Example"
        for language in PgTexts.Language.allCases {
            let texts = PgTexts(language)
            for state in TunnelState.allCases {
                let home = HomeState(
                    status: TunnelStatus(state: state, serverId: "a"), servers: servers, selectedId: "a", subscriptions: subscriptions, measuring: state == .off
                )
                let query = ServerListQuery(hiddenProtocols: [.trojan], sort: .name)
                let list = ServersView(texts: texts, home: home, query: .constant(query), filterOpen: true)
                XCTAssertEqual(try render(list).size.width, 393, accuracy: 1, "\(language) \(state)")
                XCTAssertNotNil(try render(ServerCardView(texts: texts, home: home, id: "a", actions: CardActions())), "\(language) \(state)")
            }

            let home = HomeState(status: TunnelStatus(state: .connected, serverId: "a"), servers: servers, selectedId: "a", subscriptions: subscriptions)
            for id in ["a", "b", "h", "t"] {
                for page in [CardPage.details, .rename, .share] {
                    let card = ServerCardView(texts: texts, home: home, id: id, actions: CardActions(), page: page, link: page == .share ? link : nil)
                    XCTAssertNotNil(try render(PgSheet { card }), "\(language) \(id) \(page)")
                }
            }

            // Ничего не найдено — пояснение вместо групп, панель фильтра поверх него.
            let nothing = ServersView(texts: texts, home: home, query: .constant(ServerListQuery(search: "zz")), filterOpen: true)
            XCTAssertNotNil(try render(nothing))

            // Диалог удаления — поверх листа, в его слоях.
            let layers = PgLayers()
            layers.confirm(CardLook.deleteDialog("Нидерланды 01", inUse: true, texts)) {}
            XCTAssertNotNil(try render(ServerCardView(texts: texts, home: home, id: "a", actions: CardActions()).pgLayers(layers)))
            layers.answer(.dismissed)
        }
    }
}

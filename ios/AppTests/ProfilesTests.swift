import PrivateGatewayCore
import PrivateGatewayTheme
import SwiftUI
import UIKit
import XCTest
@testable import PrivateGateway

/// Подписка без сети: ответ или неудача, которые тест меняет по ходу.
private final class SwitchSource: SubscriptionSource, @unchecked Sendable {
    var text = ""
    var failure: SubscriptionDownloadError?

    func download(_ url: URLComponents) async throws -> SubscriptionContent {
        if let failure {
            throw failure
        }

        return SubscriptionContent(text: text, title: "Мой VPN")
    }
}

/// «Добавить профиль» и «Профили и подписки» по макету v1.1 (доски 03 и 12): выбранный источник и его предпросмотр, новые и
/// дубли — до добавления, «Обновить защиту», подписка, добавленная снова, — обновление в своей группе с итогом «новых N,
/// удалено M»; файл — текст UTF-8 с именем профиля по имени файла; рубрика считает «Мои серверы», «Сейчас защита выключена»
/// — третьей строкой тумблера, удаление — только после «Удалить» в диалоге; оба экрана и лист «Изменить» рисуются на обоих
/// языках.
final class ProfilesTests: XCTestCase {
    private let texts = PgTexts(.russian)

    private static let links: URL = {
        var url = URL(fileURLWithPath: #filePath)
        url.deleteLastPathComponent()
        url.deleteLastPathComponent()
        url.deleteLastPathComponent()
        return url.appendingPathComponent("shared/testdata/links")
    }()

    /// Эталонная ссылка из shared/testdata — заведомо ненастоящая.
    private func link(_ path: String) throws -> String {
        try String(contentsOf: Self.links.appendingPathComponent("\(path).txt"), encoding: .utf8).trimmingCharacters(in: .whitespacesAndNewlines)
    }

    private var directory: URL!

    override func setUpWithError() throws {
        directory = FileManager.default.temporaryDirectory.appendingPathComponent("pg-profiles-\(UUID().uuidString)", isDirectory: true)
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

    private let now = Date()

    private var groups: [GroupItem] {
        [
            GroupItem(id: "s", name: "Основная подписка", maskedUrl: "https://example.com/sub/****", servers: 7, updatedAt: now, lastAttemptAt: now, failure: nil, status: nil),
            GroupItem(id: "f", name: "Тестовый стенд", maskedUrl: "https://sub.example.org/test/****", servers: 1, updatedAt: now.addingTimeInterval(-3 * 86400),
                      lastAttemptAt: now.addingTimeInterval(-7200), failure: .status, status: 403),
            GroupItem(id: "p", name: "Профиль", maskedUrl: nil, servers: 2, updatedAt: nil, lastAttemptAt: nil, failure: nil, status: nil),
        ]
    }

    // MARK: «Добавить профиль»

    @MainActor
    func testPreviewCountsNewDuplicatesAndErrorsBeforeAdding() async throws {
        let container = SharedContainer(directory: directory, vault: MemoryVault())
        let saved = try link("vless/reality-vision")
        _ = try await container.servers.add([XCTUnwrap(LinkParser.parse(saved).server)])
        let model = ImportModel(container: container, source: SwitchSource())
        var results: [AddResult] = []
        model.onAdded = { result, _ in results.append(result) }

        model.chooseLink()
        XCTAssertEqual(model.source, .link)
        let text = try [saved, link("trojan/ws"), link("vmess/ws-tls"), link("vless/bad-port")].joined(separator: "\n")
        model.text = text
        await model.check(text)
        let batch = try XCTUnwrap(model.preview?.batch)
        XCTAssertEqual(batch.found, 4, "«найдено» — все строки, и с ошибкой тоже")
        XCTAssertEqual(batch.failed, 1)
        XCTAssertEqual(model.count, ImportCount(duplicates: 1, renewals: 0))
        let fresh = ImportLook.fresh(batch, count: model.count)
        XCTAssertEqual(fresh, 2, "новых — без дубля, до добавления")
        XCTAssertEqual(ImportLook.addLabel(fresh: fresh, renewals: 0, texts), "Добавить 2 сервера")
        XCTAssertEqual(ImportLook.format(batch, texts), "ссылки · VLESS / VMess / Trojan")
        XCTAssertEqual(ImportLook.errorLines(batch, texts), ["Строка 4: неверный порт — пропущено."])
        XCTAssertNotNil(try render(ImportView(model: model, texts: texts, onClose: {})))

        await model.add()
        XCTAssertEqual(results, [AddResult(added: 2, duplicates: 1)], "добавлено столько, сколько обещал предпросмотр")
        XCTAssertEqual(ImportLook.added(try XCTUnwrap(results.first), existing: nil, texts), "Добавлено 2 сервера")
    }

    @MainActor
    func testNothingNewButTheProtectionOfASavedServerIsRenewed() async throws {
        let container = SharedContainer(directory: directory, vault: MemoryVault())
        let insecure = try XCTUnwrap(LinkParser.parse("trojan://pw-a@198.51.100.1:443?sni=example.com&allowInsecure=1#Self-signed").server)
        _ = try await container.servers.add([insecure])
        let model = ImportModel(container: container, source: SwitchSource())
        var results: [AddResult] = []
        model.onAdded = { result, _ in results.append(result) }

        await model.paste("trojan://pw-a@198.51.100.1:443?sni=example.com&pcs=a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90#Pinned")
        XCTAssertEqual(model.source, .clipboard)
        XCTAssertEqual(model.text, "", "вставленное в поле ссылки не попадает")
        let batch = try XCTUnwrap(model.preview?.batch)
        XCTAssertEqual(model.count, ImportCount(duplicates: 1, renewals: 1))
        XCTAssertEqual(ImportLook.fresh(batch, count: model.count), 0)
        XCTAssertEqual(ImportLook.addLabel(fresh: 0, renewals: 1, texts), "Обновить защиту 1 сервера")
        XCTAssertTrue(ImportLook.canAdd(fresh: 0, renewals: 1))
        XCTAssertEqual(ImportLook.addLabel(fresh: 0, renewals: 0, texts), texts.importNothing)
        XCTAssertFalse(ImportLook.canAdd(fresh: 0, renewals: 0), "нечего добавить — кнопка недоступна")

        await model.add()
        XCTAssertEqual(results, [AddResult(added: 0, duplicates: 1, renewed: 1)])
        XCTAssertEqual(ImportLook.added(try XCTUnwrap(results.first), existing: nil, texts), "Защита обновлена у 1 сервера")

        await model.paste("   ")
        XCTAssertTrue(model.emptyClipboard)
        XCTAssertNil(model.preview)
        XCTAssertEqual(ImportLook.note(model, texts), ImportNote(text: texts.importEmptyClipboard, warning: true, identifier: "import.empty.clipboard"))
    }

    @MainActor
    func testTheSameSubscriptionAddedAgainIsUpdatedInItsGroup() async throws {
        let container = SharedContainer(directory: directory, vault: MemoryVault())
        let source = SwitchSource()
        source.text = try [link("vmess/ws-tls"), link("trojan/ws")].joined(separator: "\n")
        let model = ImportModel(container: container, source: source)
        var results: [(AddResult, String?)] = []
        model.onAdded = { result, existing in results.append((result, existing)) }
        let address = "https://example.com/sub/0123456789abcdef"

        await model.check(address)
        XCTAssertNil(model.existing)
        XCTAssertEqual(model.name, "Мой VPN", "название — из Profile-Title")
        await model.add()
        XCTAssertEqual(results.count, 1)
        model.reset()

        // В подписке теперь другой набор: vmess остался, trojan пропал, vless пришел.
        source.text = try [link("vmess/ws-tls"), link("vless/reality-vision")].joined(separator: "\n")
        await model.check(address)
        XCTAssertEqual(model.existing, "Мой VPN", "поле названия скрыто, «Добавить» обновит подписку")
        XCTAssertEqual(model.count?.duplicates, 1)
        XCTAssertNotNil(try render(ImportView(model: model, texts: texts, onClose: {})))
        await model.add()
        let (result, existing) = try XCTUnwrap(results.last)
        XCTAssertEqual(existing, "Мой VPN")
        XCTAssertEqual(result.added, 1)
        XCTAssertEqual(result.removed, 1)
        XCTAssertEqual(ImportLook.added(result, existing: existing, texts), "«Мой VPN» обновлена: новых 1, удалено 1")
        let stored = try await container.servers.groups()
        XCTAssertEqual(stored.map(\.servers), [2], "одна группа, а не вторая (решение 528)")

        source.failure = SubscriptionDownloadError(.status, status: 404)
        await model.check(address)
        XCTAssertNil(model.preview?.batch)
        XCTAssertEqual(ImportLook.note(model, texts)?.identifier, "import.failed")
        XCTAssertEqual(ImportLook.note(model, texts)?.warning, true)
    }

    @MainActor
    func testChoosingASourceDropsTheOtherPreviewAndEditingTheLinkDropsItsOwn() async throws {
        let model = ImportModel(container: SharedContainer(directory: directory, vault: MemoryVault()), source: SwitchSource())
        let text = try link("trojan/ws")
        await model.paste(text)
        XCTAssertNotNil(model.preview)

        model.chooseLink()
        XCTAssertEqual(model.source, .link)
        XCTAssertNil(model.preview, "предпросмотр буфера — не о ссылке")
        model.text = text
        await model.check(text)
        XCTAssertNotNil(model.preview)
        model.chooseLink()
        XCTAssertNotNil(model.preview, "нажатие на выбранную карточку ничего не сбрасывает")
        model.edited()
        XCTAssertNil(model.preview, "текст изменился — прежний предпросмотр не о нем")
        XCTAssertEqual(model.source, .link)

        model.chooseQr()
        XCTAssertEqual(model.source, .qr)
        model.cameraProblem()
        XCTAssertEqual(ImportLook.note(model, texts)?.identifier, "import.qr.problem")
        model.chooseFile()
        XCTAssertEqual(model.source, .file)
        XCTAssertNil(model.qrProblem, "подсказка другого источника уходит")
        XCTAssertNil(ImportLook.note(model, texts))

        await model.paste("# только комментарий")
        XCTAssertEqual(model.preview?.batch?.found, 0)
        XCTAssertEqual(ImportLook.note(model, texts)?.text, texts.importEmptyText, "серверов нет — сказать, а не рисовать пустой предпросмотр")

        model.reset()
        // Явно ImportSource.none: «.none» рядом с XCTAssertEqual легко принять за Optional.none.
        XCTAssertEqual(model.source, ImportSource.none)
        XCTAssertEqual(model.text, "")
        XCTAssertNil(model.preview)
    }

    @MainActor
    func testFileIsReadAsTextAndNamedByIt() async throws {
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let model = ImportModel(container: SharedContainer(directory: directory, vault: MemoryVault()), source: SwitchSource())
        let file = directory.appendingPathComponent("work-servers.txt")
        try [link("vless/reality-vision"), link("trojan/ws")].joined(separator: "\n").write(to: file, atomically: true, encoding: .utf8)

        await model.readFile(.success(file))
        XCTAssertEqual(model.source, .file)
        XCTAssertEqual(model.preview?.batch?.found, 2)
        XCTAssertEqual(model.name, "work-servers", "название профиля — имя файла без расширения")

        let binary = directory.appendingPathComponent("picture.bin")
        try Data([0x61, 0xC3, 0x28, 0x62]).write(to: binary)
        await model.readFile(.success(binary))
        XCTAssertEqual(model.fileProblem, .notText)
        XCTAssertNil(model.preview)
        XCTAssertEqual(ImportLook.note(model, texts)?.text, "Файл не прочитан: данные повреждены")

        let large = directory.appendingPathComponent("large.txt")
        try Data(repeating: 0x61, count: ImportModel.maxFileBytes + 1).write(to: large)
        XCTAssertEqual(ImportModel.read(large), .failed(.tooLarge))
        XCTAssertEqual(ImportLook.fileProblem(.tooLarge, texts), "Файл не прочитан: подписка больше 5 МБ")

        guard case .failed(.unreadable) = ImportModel.read(directory.appendingPathComponent("gone.txt")) else {
            return XCTFail("файла нет — причина от iOS")
        }

        await model.readFile(.failure(CocoaError(.fileReadNoPermission)))
        XCTAssertEqual(model.source, .file)
        XCTAssertNotNil(model.fileProblem)
    }

    @MainActor
    func testCountsFollowTheListWhilePreviewIsOpen() async throws {
        let container = SharedContainer(directory: directory, vault: MemoryVault())
        let model = ImportModel(container: container, source: SwitchSource())
        let text = try [link("vless/reality-vision"), link("trojan/ws")].joined(separator: "\n")
        await model.paste(text)
        XCTAssertEqual(model.count?.duplicates, 0)

        _ = try await container.servers.add(model.parsed)
        await model.recount()
        XCTAssertEqual(model.count?.duplicates, 2, "добавили в другом месте — теперь это дубли")
        let batch = try XCTUnwrap(model.preview?.batch)
        XCTAssertEqual(ImportLook.addLabel(fresh: ImportLook.fresh(batch, count: model.count), renewals: 0, texts), texts.importNothing)
    }

    @MainActor
    func testImportDrawsEverySourceAndPreviewInBothLanguages() async throws {
        let errors = ["garbage 1", "garbage 2", "garbage 3", "garbage 4", "garbage 5"]
        let text = try ([link("vless/reality-vision"), link("hysteria2/obfs-insecure")] + errors).joined(separator: "\n")
        for language in PgTexts.Language.allCases {
            let texts = PgTexts(language)
            let model = ImportModel(container: SharedContainer(directory: directory, vault: MemoryVault()), source: SwitchSource())
            XCTAssertNotNil(try render(ImportView(model: model, texts: texts, onClose: {})), "источник не выбран")
            model.chooseLink()
            XCTAssertNotNil(try render(ImportView(model: model, texts: texts, onClose: {})), "поле и «Проверить»")

            await model.paste(text)
            let batch = try XCTUnwrap(model.preview?.batch)
            let lines = ImportLook.errorLines(batch, texts)
            XCTAssertEqual(lines.count, ImportLook.shownErrors + 1, "три строки и «И еще…»")
            XCTAssertEqual(lines.last, texts.importMore(2))
            XCTAssertTrue(ImportLook.format(batch, texts).contains("Hy2"), "короткие имена протоколов, как в макете")
            XCTAssertNotNil(try render(ImportView(model: model, texts: texts, onClose: {})), "предпросмотр с ошибками")
        }
    }

    // MARK: «Профили и подписки»

    @MainActor
    func testSectionCountsEveryCardIncludingMine() {
        XCTAssertEqual(SubscriptionsLook.cards(groups: 3, manualCount: 1), 4, "как на доске: «Подписки · 4»")
        XCTAssertEqual(SubscriptionsLook.cards(groups: 3, manualCount: 0), 3)
        XCTAssertEqual(SubscriptionsLook.cards(groups: 0, manualCount: 0), 0)
        XCTAssertEqual(texts.subsSection(SubscriptionsLook.cards(groups: 3, manualCount: 2)), "Подписки · 4")
    }

    @MainActor
    func testBlockNoteOnlyWhileOnlyViaVpnAndNotConnected() {
        let only = SubscriptionSettings(onlyViaVpn: true)
        XCTAssertNil(SubscriptionsLook.blockNote(only, state: .connected, texts))
        for state in TunnelState.allCases where state != .connected {
            XCTAssertEqual(SubscriptionsLook.blockNote(only, state: state, texts), texts.subsVpnBlockedOff, "\(state)")
        }

        XCTAssertNil(SubscriptionsLook.blockNote(SubscriptionSettings(onlyViaVpn: false), state: .off, texts))
    }

    @MainActor
    func testGroupInUseIsTheOneProtectionGoesThroughInAnyStateButOff() {
        let servers = [
            ServerItem(id: "a", linkName: "Нидерланды 01", countryCode: "NL", protocol: .vless, security: .reality, transport: .tcp, port: 443, subscriptionId: "s"),
            ServerItem(id: "m", linkName: "Германия 02", countryCode: "DE", protocol: .trojan, security: .tls, transport: .webSocket, port: 443),
        ]
        XCTAssertEqual(SubscriptionsLook.inUse(HomeState(status: TunnelStatus(state: .connected, serverId: "a"), servers: servers, selectedId: "a")), "s")
        XCTAssertEqual(
            SubscriptionsLook.inUse(HomeState(status: TunnelStatus(state: .error, serverId: "a"), servers: servers, selectedId: "a")), "s",
            "в «Ошибке» туннель может стоять — удаление отключит VPN, как у десктопа"
        )
        XCTAssertEqual(SubscriptionsLook.inUse(HomeState(status: TunnelStatus(state: .connected, serverId: "m"), servers: servers)), SubscriptionsLook.mine)
        XCTAssertNil(SubscriptionsLook.inUse(HomeState(servers: servers, selectedId: "a")), "защита выключена — отключать нечего")
    }

    @MainActor
    func testRemovingAsksFirstAndSaysWhatGoes() {
        let plain = SubscriptionsLook.removeDialog(name: "Резервная", servers: 1, inUse: false, texts)
        XCTAssertEqual(plain.kind, .danger)
        XCTAssertEqual(plain.icon, PgIcons.trash)
        XCTAssertEqual(plain.title, "Удалить «Резервная»?")
        XCTAssertEqual(plain.text, "Вместе с группой удалятся 1 сервер и их ключи.")
        XCTAssertEqual(plain.primary, texts.dialogDeleteServerDelete)
        XCTAssertEqual(plain.secondary, texts.dialogCancel)
        XCTAssertEqual(plain.yes, .primary, "удаляет только красная «Удалить»")
        let inUse = SubscriptionsLook.removeDialog(name: "Основная подписка", servers: 7, inUse: true, texts)
        XCTAssertEqual(inUse.text, texts.subsRemoveTextInUse("7 серверов"))

        let layers = PgLayers()
        var removed = 0
        PgLayers.confirming(plain, in: layers) { removed += 1 }
        layers.answer(.secondary)
        XCTAssertEqual(removed, 0, "«Отмена» — ничего")
        PgLayers.confirming(plain, in: layers) { removed += 1 }
        layers.answer(.dismissed)
        XCTAssertEqual(removed, 0, "нажатие на затемнение — ничего")
        PgLayers.confirming(plain, in: layers) { removed += 1 }
        layers.answer(.primary)
        XCTAssertEqual(removed, 1)
    }

    @MainActor
    func testGroupStateFailureAndEditSheetHints() {
        let groups = self.groups
        XCTAssertEqual(SubscriptionsLook.chip(groups[0]), .ok)
        XCTAssertEqual(SubscriptionsLook.chip(groups[1]), .error)
        XCTAssertEqual(SubscriptionsLook.chip(groups[2]), .manual)
        XCTAssertNil(SubscriptionsLook.failure(groups[0], texts))
        XCTAssertEqual(
            SubscriptionsLook.failure(groups[1], texts),
            "Сервер ответил 403 — доступ запрещен, проверьте ссылку. Оставлена прошлая версия списка — серверы работают."
        )
        XCTAssertTrue(SubscriptionsLook.meta(groups[1], now: now, texts).hasPrefix("1 сервер · последняя попытка"))
        XCTAssertEqual(SubscriptionsLook.urlPlaceholder(groups[0]), "https://example.com/sub/****", "подсказка — маска текущего адреса")
        XCTAssertEqual(SubscriptionsLook.urlPlaceholder(groups[2]), "https://…")
        XCTAssertEqual(SubscriptionsLook.refreshOptions(texts).map(\.value), [.off, .every6Hours, .every12Hours, .every24Hours])
        XCTAssertEqual(SubscriptionsLook.refreshOptions(texts).map(\.title), [texts.subsRefreshOff, texts.subsRefresh6, texts.subsRefresh12, texts.subsRefresh24])
    }

    @MainActor
    func testSubscriptionsHeaderEditSheetAndSmallButtonsDraw() throws {
        for language in PgTexts.Language.allCases {
            let texts = PgTexts(language)
            for group in groups {
                XCTAssertNotNil(try render(SubscriptionEditView(texts: texts, group: group, onSave: { _, _ in true }, onClose: {})), group.name)
            }

            let header = PgPageHeader(
                title: texts.settingsAdvProfilesTitle, texts: texts, action: PgHeaderAction(icon: PgIcons.plus, label: texts.subsAdd, action: {}), fixed: true,
                onBack: {}
            )
            XCTAssertNotNil(try render(header, height: 60))
            for spinning in [false, true] {
                XCTAssertNotNil(try render(PgSmallButton(label: texts.subsUpdate, icon: PgIcons.refresh, soft: true, spinning: spinning, action: {}), height: 44))
            }

            let blocked = SubscriptionsView(
                texts: texts, settings: SubscriptionSettings(onlyViaVpn: true), groups: groups, manualCount: 1, home: HomeState(), actions: SubscriptionsActions(),
                onBack: {}
            )
            XCTAssertEqual(try render(blocked, height: 1600).size.width, 393, accuracy: 1)
        }
    }
}

import PrivateGatewayCore
import PrivateGatewayTheme
import SwiftUI
import UIKit
import XCTest
@testable import PrivateGateway

/// Блоки дизайн-системы телефона (доска 24): подсказка держится свой срок, опасный выбор — только после «да» в диалоге
/// (сегменты, меню, переключатель), меню отмечает текущий пункт, контур островка — по токенам, как у десктопа; каждый блок
/// рисуется на обоих языках.
final class ComponentsTests: XCTestCase {
    private let texts = PgTexts(.russian)

    /// Выключить блок при обрыве: основная кнопка — «Оставить включенным», опасная вторая — «Выключить».
    private func ksOff(_ texts: PgTexts) -> PgDialog {
        PgDialog(
            kind: .warning, icon: PgIcons.shield, detail: PgIcons.markAlert, title: texts.dialogKsOffTitle, text: texts.dialogKsOffText,
            primary: texts.dialogKsOffKeep, secondary: texts.dialogKsOffDrop, secondaryDanger: true
        )
    }

    /// Удалить сервер: основная красная — «Удалить», вторая — «Отмена».
    private func delete(_ texts: PgTexts) -> PgDialog {
        PgDialog(
            kind: .danger, icon: PgIcons.trash, title: texts.dialogDeleteServerTitle("Нидерланды 01"), text: texts.dialogDeleteServerTextInUse,
            primary: texts.dialogDeleteServerDelete, secondary: texts.dialogCancel
        )
    }

    @MainActor
    private func render(_ view: some View, height: CGFloat = 852) throws -> UIImage {
        try XCTUnwrap(ImageRenderer(content: view.frame(width: 393, height: height)).uiImage)
    }

    /// Ждать, пока условие станет верным, — не дольше `seconds`.
    @MainActor
    private func eventually(within seconds: Double, _ condition: () -> Bool) async throws {
        let deadline = Date().addingTimeInterval(seconds)
        while !condition(), Date() < deadline {
            try await Task.sleep(nanoseconds: 20_000_000)
        }
    }

    @MainActor
    func testToastHidesItselfAfterItsTime() async throws {
        XCTAssertEqual(PgToastDuration.action, 1.8, accuracy: 0.001, "итог действия — 1,8 с (доска 19)")
        XCTAssertEqual(PgToastDuration.event, 6, accuracy: 0.001, "событие — 6 с")

        let layers = PgLayers()
        layers.show("IP скопирован", for: 0.05)
        XCTAssertEqual(layers.toast, "IP скопирован")
        try await eventually(within: 3) { layers.toast == nil }
        XCTAssertNil(layers.toast, "подсказка ушла сама")
    }

    @MainActor
    func testNewToastReplacesTheOldOneAndKeepsItsOwnTime() async throws {
        let layers = PgLayers()
        layers.show("Серверы обновлены", for: 0.1)
        layers.show("Сервер удален", for: 30)
        XCTAssertEqual(layers.toast, "Сервер удален")
        try await Task.sleep(nanoseconds: 500_000_000)
        XCTAssertEqual(layers.toast, "Сервер удален", "срок прежней подсказки не убирает новую")

        layers.hideToast()
        XCTAssertNil(layers.toast)
    }

    @MainActor
    func testRiskyActionRunsOnlyOnYes() {
        let layers = PgLayers()
        let dialog = ksOff(texts)
        XCTAssertEqual(dialog.yes, .secondary, "«да» — опасная вторая кнопка")
        XCTAssertEqual(delete(texts).yes, .primary, "«да» удаления — основная красная")

        var runs = 0
        for choice in [PgDialogChoice.primary, .dismissed] {
            layers.confirm(dialog) { runs += 1 }
            XCTAssertEqual(layers.dialog?.title, texts.dialogKsOffTitle)
            layers.answer(choice)
            XCTAssertNil(layers.dialog)
            XCTAssertEqual(runs, 0, "\(choice): безопасный выбор ничего не делает")
        }

        layers.confirm(dialog) { runs += 1 }
        layers.answer(.secondary)
        XCTAssertEqual(runs, 1)
        layers.answer(.secondary)
        XCTAssertEqual(runs, 1, "ответ — один раз")

        var first: PgDialogChoice?
        layers.ask(dialog) { first = $0 }
        layers.confirm(delete(texts)) { runs += 1 }
        XCTAssertEqual(first, .dismissed, "новый вопрос закрывает прежний без ответа")
        XCTAssertEqual(layers.dialog?.kind, .danger)
        layers.answer(.primary)
        XCTAssertEqual(runs, 2)
    }

    @MainActor
    func testConfirmingWithoutAQuestionRunsAtOnceAndWithoutLayersNever() {
        var runs = 0
        PgLayers.confirming(nil, in: nil) { runs += 1 }
        XCTAssertEqual(runs, 1, "вопроса нет — сразу")
        PgLayers.confirming(ksOff(texts), in: nil) { runs += 1 }
        XCTAssertEqual(runs, 1, "спросить негде — опасный переход не делается")
    }

    func testNotificationsQuestionExplainsWhyAndAllowIsTheMainButton() {
        // DECISIONS.md, п. 7: вопрос — после первого подключения, с пояснением; «Разрешить» — основная, «Не сейчас» — вторая.
        let dialog = NotificationsLook.dialog(texts)
        XCTAssertEqual(dialog.title, texts.dialogNotificationsTitle)
        XCTAssertEqual(dialog.text, texts.dialogNotificationsText)
        XCTAssertEqual(dialog.primary, texts.dialogNotificationsAllow)
        XCTAssertEqual(dialog.secondary, texts.dialogNotificationsLater)
        XCTAssertEqual(dialog.yes, .primary)
    }

    @MainActor
    func testSegmentAsksBeforeTheRiskyChoice() {
        let layers = PgLayers()
        let proxy = PgDialog(
            kind: .warning, icon: PgIcons.shield, detail: PgIcons.markAlert, title: texts.dialogProxyModeTitle, text: texts.dialogProxyModeText,
            primary: texts.dialogProxyModeKeep, secondary: texts.dialogProxyModeSwitch, secondaryDanger: true
        )
        var selected: [String] = []

        PgSegments<String>.pick("vpn", selection: "vpn", dialog: nil, layers: layers) { selected.append($0) }
        XCTAssertEqual(selected, [], "выбранный сегмент — ничего")
        XCTAssertNil(layers.dialog)

        PgSegments<String>.pick("proxy", selection: "vpn", dialog: proxy, layers: layers) { selected.append($0) }
        XCTAssertEqual(selected, [], "сегмент сам не отмечается до ответа")
        XCTAssertEqual(layers.dialog?.title, texts.dialogProxyModeTitle)
        layers.answer(.primary)
        XCTAssertEqual(selected, [], "«Оставить VPN»")

        PgSegments<String>.pick("proxy", selection: "vpn", dialog: proxy, layers: layers) { selected.append($0) }
        layers.answer(.secondary)
        XCTAssertEqual(selected, ["proxy"], "«Перейти на прокси»")

        PgSegments<String>.pick("all", selection: "off", dialog: nil, layers: layers) { selected.append($0) }
        XCTAssertEqual(selected, ["proxy", "all"], "без вопроса — сразу")
    }

    @MainActor
    func testSelectMenuMarksTheCurrentOptionAndPicks() {
        let layers = PgLayers()
        let anchor = UUID()
        let options = [
            PgOption(value: "all", title: texts.settingsNotificationsAll), PgOption(value: "important", title: texts.settingsNotificationsImportant),
            PgOption(value: "off", title: texts.settingsNotificationsOff),
        ]
        var selected: [String] = []
        let open = {
            layers.openMenu(PgSelect<String>.menu(
                anchor: anchor, options: options, selection: "important", layers: layers, confirm: { $0 == "off" ? self.ksOff(self.texts) : nil },
                onSelect: { selected.append($0) }
            ))
        }

        open()
        XCTAssertEqual(layers.menu?.anchor, anchor)
        XCTAssertEqual(layers.menu?.items.map(\.title), options.map(\.title))
        XCTAssertEqual(layers.menu?.items.map(\.current), [false, true, false], "текущий пункт отмечен")
        layers.pick(1)
        XCTAssertNil(layers.menu, "выбор закрывает меню")
        XCTAssertEqual(selected, [], "выбран текущий — ничего")

        open()
        layers.pick(0)
        XCTAssertEqual(selected, ["all"])

        open()
        layers.pick(7)
        XCTAssertNotNil(layers.menu, "нет такого пункта — меню остается")
        layers.closeMenu()
        XCTAssertNil(layers.menu)

        open()
        layers.pick(2)
        XCTAssertEqual(selected, ["all"], "опасный пункт — сначала вопрос")
        XCTAssertNotNil(layers.dialog)
        layers.answer(.secondary)
        XCTAssertEqual(selected, ["all", "off"])

        open()
        layers.confirm(delete(texts)) {}
        XCTAssertNil(layers.menu, "диалог закрывает меню")
        layers.answer(.dismissed)
    }

    @MainActor
    func testIslandOutlineFollowsTheTokensLikeTheDesktop() {
        // Десктоп (IslandGeometry, ширина 440, поля 12, верх плашки 70): плашка 12–428 и 70–138, выступ — до 70 − 48 = 22.
        // Рамка — по самому контуру, без точек управления кривых.
        let desktop = Path(svg: IslandGeometry.data(width: 440, side: 12, top: 70)).cgPath.boundingBoxOfPath
        XCTAssertEqual(desktop.minX, 12, accuracy: 0.5)
        XCTAssertEqual(desktop.maxX, 428, accuracy: 0.5)
        XCTAssertEqual(desktop.minY, CGFloat(70 - PgLayout.Island.bumpRadius), accuracy: 0.5)
        XCTAssertEqual(desktop.maxY, CGFloat(70 + PgLayout.Island.height), accuracy: 0.5)
        XCTAssertTrue(IslandGeometry.data(width: 440, side: 12, top: 70).hasPrefix("M38.0 70.0 H156.50"), "начало — как у десктопа: M38 70 H156.5")

        // Телефон: поля 14, верх плашки — на радиусе выступа, выступ касается верха островка.
        let phone = IslandShape(side: CGFloat(PgLayout.Island.sideMarginMobile), top: CGFloat(PgLayout.Island.bumpRadius))
            .path(in: CGRect(x: 0, y: 0, width: 393, height: 116)).cgPath.boundingBoxOfPath
        XCTAssertEqual(phone.minX, CGFloat(PgLayout.Island.sideMarginMobile), accuracy: 0.5)
        XCTAssertEqual(phone.maxX, CGFloat(393 - PgLayout.Island.sideMarginMobile), accuracy: 0.5)
        XCTAssertEqual(phone.minY, 0, accuracy: 0.5)
        XCTAssertEqual(phone.maxY, CGFloat(PgLayout.Island.bumpRadius + PgLayout.Island.height), accuracy: 0.5)

        XCTAssertEqual(StateLook.of(.connected, texts).fill, PgState.Connected.fill, "нажатый щит — заливка состояния")
        XCTAssertEqual(StateLook.of(.reconnecting, texts).fill, PgState.Connecting.fill)
        XCTAssertEqual(StateLook.of(.off, texts).fill, PgState.Off.fill)
    }

    @MainActor
    func testComponentsDrawInBothLanguages() throws {
        for language in PgTexts.Language.allCases {
            let texts = PgTexts(language)
            let modes = [PgOption(value: 0, title: texts.settingsModeVpn), PgOption(value: 1, title: texts.settingsModeProxy)]
            let view = ScrollView {
                VStack(alignment: .leading, spacing: PgSpace.l) {
                    PgSegments(options: modes, selection: 0, onSelect: { _ in })
                    PgSegments(options: modes, selection: 1, enabled: false, onSelect: { _ in })
                    PgSelect(options: modes, selection: 1, label: texts.settingsModeTitle, onSelect: { _ in })
                    PgCheck(title: texts.protLanTitle, isOn: true, onChange: { _ in })
                    PgCheck(title: texts.protLanTitle, isOn: false, enabled: false, onChange: { _ in })
                    VStack(spacing: 0) {
                        PgToggleRow(title: texts.protLanTitle, text: texts.protLanText, isOn: true, padded: true, onChange: { _ in })
                        PgDivider()
                        PgToggleRow(title: texts.protLanDiscTitle, text: texts.protLanDiscText, isOn: false, enabled: false, padded: true, onChange: { _ in })
                        PgDivider()
                        PgSettingRow(title: texts.settingsModeTitle) {
                            PgSelect(options: modes, selection: 0, onSelect: { _ in })
                        }
                    }
                    .pgCard(padding: 0)
                    VStack(spacing: 0) {
                        PgLinkRow(icon: PgIcons.advProtection, title: texts.settingsAdvProtectionTitle, caption: texts.settingsAdvProtectionText) {}
                        PgDivider()
                        PgLinkRow(icon: PgIcons.advBackup, title: texts.settingsAdvBackupTitle, caption: texts.settingsAdvBackupText, tone: .violet) {}
                        PgDivider()
                        PgLinkRow(icon: PgIcons.advProfiles, title: texts.settingsAdvProfilesTitle, tone: .teal, enabled: false) {}
                    }
                    .pgCard(padding: 0)
                    // Не больше 10 блоков подряд в одном построителе: остальное — группой.
                    Group {
                        PgDialogView(dialog: ksOff(texts)) { _ in }
                        PgDialogView(dialog: delete(texts)) { _ in }
                        PgToastView(text: texts.dialogKsOffText)
                        PgActionButton(label: texts.dialogCancel, kind: .secondary, danger: true) {}
                        PgSheet {
                            Text(texts.settingsModeTitle)
                        }
                        .frame(height: 120)
                    }
                }
                .padding(.horizontal, PgSpace.windowSideMobile)
            }
            let screen = ZStack {
                PgBackground()
                view
            }
            XCTAssertEqual(try render(screen, height: 2400).size.width, 393, accuracy: 1)
        }
    }

    /// Слои поверх окна: меню у своей кнопки, диалог на затемнении и подсказка сверху — все сразу.
    @MainActor
    func testLayersDrawOverTheWindow() throws {
        let layers = PgLayers()
        let anchor = UUID()
        let window = ZStack(alignment: .bottom) {
            PgBackground()
            VStack {
                Text(texts.settingsModeTitle).pgMenuAnchor(anchor)
                Spacer()
            }
            Island(texts: texts, tab: .settings, state: .connected, hasServer: true, onTab: { _ in }, onPower: {})
        }
        .pgLayers(layers)

        XCTAssertNotNil(try render(window))
        layers.openMenu(PgMenu(anchor: anchor, items: [PgMenuItem(title: texts.settingsModeVpn, current: true), PgMenuItem(title: texts.settingsModeProxy)]) { _ in })
        layers.show(texts.dialogKsOffText, for: 30)
        XCTAssertNotNil(try render(window))
        layers.ask(ksOff(texts)) { _ in }
        XCTAssertNotNil(try render(window))
        layers.answer(.dismissed)
        layers.hideToast()
    }
}

import PrivateGatewayCore
import PrivateGatewayTheme
import SwiftUI
import UIKit
import XCTest
@testable import PrivateGateway

/// «Настройки» и «Защита и утечки» по доскам 04 и 05: выключить блок при обрыве — только после «Выключить», подпись
/// уведомлений по выбору, пункты меню по порядку макета, «Свой адрес…» открывает лист и сам выбранным не становится,
/// подключение при запуске — только к выбранному серверу при выключенной защите и хранится в общей папке.
final class SettingsTests: XCTestCase {
    private let texts = PgTexts(.russian)

    private var directory: URL!

    override func setUpWithError() throws {
        directory = FileManager.default.temporaryDirectory.appendingPathComponent("pg-settings-\(UUID().uuidString)", isDirectory: true)
    }

    override func tearDownWithError() throws {
        if FileManager.default.fileExists(atPath: directory.path) {
            try FileManager.default.removeItem(at: directory)
        }
    }

    private let servers = [
        ServerItem(id: "a", linkName: "Нидерланды 01", countryCode: "NL", protocol: .vless, security: .reality, transport: .tcp, port: 443),
        ServerItem(id: "b", linkName: "Германия", countryCode: "DE", protocol: .trojan, security: .tls, transport: .webSocket, port: 443),
    ]

    /// Тумблер блока при обрыве (`PgToggleRow`): выключение спрашивает `confirmOff`, включение — без вопроса.
    @MainActor
    func testKillSwitchTurnsOffOnlyAfterTheQuestion() {
        let dialog = SettingsDialogs.ksOff(texts)
        XCTAssertEqual(dialog.kind, .warning)
        XCTAssertEqual(dialog.title, texts.dialogKsOffTitle)
        XCTAssertEqual(dialog.text, texts.dialogKsOffText)
        XCTAssertEqual(dialog.primary, texts.dialogKsOffKeep, "основная — безопасная «Оставить включенным»")
        XCTAssertEqual(dialog.secondary, texts.dialogKsOffDrop)
        XCTAssertTrue(dialog.secondaryDanger)
        XCTAssertEqual(dialog.yes, .secondary, "«да» — красная «Выключить»")

        let layers = PgLayers()
        var changes: [Bool] = []
        for choice in [PgDialogChoice.primary, .dismissed] {
            PgLayers.confirming(dialog, in: layers) { changes.append(false) }
            XCTAssertEqual(layers.dialog?.title, texts.dialogKsOffTitle)
            layers.answer(choice)
            XCTAssertEqual(changes, [], "\(choice): тумблер остается включенным")
        }

        PgLayers.confirming(dialog, in: layers) { changes.append(false) }
        layers.answer(.secondary)
        XCTAssertEqual(changes, [false], "«Выключить» — выключено")

        PgLayers.confirming(nil, in: layers) { changes.append(true) }
        XCTAssertNil(layers.dialog, "включение — без вопроса")
        XCTAssertEqual(changes, [false, true])
    }

    func testNotificationsNoteFollowsTheLevel() {
        for language in PgTexts.Language.allCases {
            let texts = PgTexts(language)
            XCTAssertEqual(SettingsLook.notificationOptions(texts).map(\.value), [.all, .importantOnly, .off], "порядок сегментов — как в макете")
            XCTAssertEqual(
                SettingsLook.notificationOptions(texts).map(\.title),
                [texts.settingsNotificationsAll, texts.settingsNotificationsImportant, texts.settingsNotificationsOff]
            )
            XCTAssertEqual(SettingsLook.notificationsNote(.all, texts), texts.settingsNotificationsAllNote)
            XCTAssertEqual(SettingsLook.notificationsNote(.importantOnly, texts), texts.settingsNotificationsImportantNote)
            XCTAssertNil(SettingsLook.notificationsNote(.off, texts), "подпись десктопа — про трей, которого у телефона нет")
        }
    }

    func testReconnectMenuIsInTheOrderOfTheConnectionPage() {
        let options = CoreLook.reconnectOptions(texts)
        XCTAssertEqual(options.map(\.value), [.backoff60Seconds, .upTo5Minutes, .never])
        XCTAssertEqual(options.map(\.title), [texts.coreReconnectBackoff, texts.coreReconnect5min, texts.coreReconnectNever])
    }

    /// Меню «Подключения и ядра» — по порядку доски 06 и с подписями макета на обоих языках.
    func testCoreMenusAreInTheOrderOfTheBoard() {
        for language in PgTexts.Language.allCases {
            let texts = PgTexts(language)
            XCTAssertEqual(CoreLook.logOptions(texts).map(\.value), [.warning, .info, .debug])
            XCTAssertEqual(CoreLook.logOptions(texts).map(\.title), [texts.coreLogWarning, texts.coreLogInfo, texts.coreLogDebug])
            XCTAssertEqual(CoreLook.mtuOptions(texts).map(\.value), [nil, 1500, 1420, 1280])
            XCTAssertEqual(CoreLook.mtuOptions(texts).map(\.title), [texts.coreMtuAuto, "1500", "1420", "1280"])
            XCTAssertEqual(CoreLook.fingerprintOptions(texts).map(\.value), [.profile, .chrome, .firefox, .safari, .random])
            XCTAssertEqual(CoreLook.fingerprintOptions(texts).map(\.title), [texts.coreFpProfile, "chrome", "firefox", "safari", "random"], "имена — как в ссылках")
            XCTAssertEqual(CoreLook.timeoutOptions(texts).map(\.value), [10, 5, 20, 30])
            XCTAssertEqual(CoreLook.timeoutOptions(texts).map(\.title), [10, 5, 20, 30].map { texts.coreTimeoutValue($0) })
        }

        XCTAssertEqual(CoreLook.timeoutOptions(PgTexts(.russian)).first?.title, "10 с")
    }

    /// «MTU»: «Свое…» — ссылкой под чертой, открывает лист и само выбранным не становится; свой MTU не из меню — на кнопке
    /// число, и ни один пункт меню не текущий.
    @MainActor
    func testMtuMenuOffersTheChoicesAndOwnOpensTheSheet() {
        let layers = PgLayers()
        let anchor = UUID()
        var selected: [Int?] = []
        var sheets = 0
        let open = { (connection: ConnectionSettings) in
            layers.openMenu(PgSelect<Int?>.menu(
                anchor: anchor, options: CoreLook.mtuOptions(self.texts), selection: CoreLook.mtuSelection(connection), layers: layers,
                confirm: { _ in nil }, link: PgMenuLink(title: self.texts.coreMtuCustom) { sheets += 1 }, onSelect: { selected.append($0) }
            ))
        }

        open(ConnectionSettings())
        XCTAssertEqual(layers.menu?.items.map(\.title), [texts.coreMtuAuto, "1500", "1420", "1280", texts.coreMtuCustom])
        XCTAssertEqual(layers.menu?.items.map(\.current), [true, false, false, false, false], "по умолчанию — «Авто»")
        XCTAssertEqual(layers.menu?.items.map(\.link), [false, false, false, false, true])
        layers.pick(4)
        XCTAssertEqual(sheets, 1, "лист открыт")
        XCTAssertEqual(selected, [], "выбор — только после «Сохранить» в листе")

        open(ConnectionSettings(mtu: 1360))
        XCTAssertEqual(layers.menu?.items.map(\.current), [false, false, false, false, false], "свой MTU — ни один пункт не текущий")
        layers.pick(2)
        XCTAssertEqual(selected, [1420])
        open(ConnectionSettings(mtu: 1420))
        layers.pick(0)
        XCTAssertEqual(selected, [1420, nil], "«Авто» — nil")
    }

    func testOwnMtuAndTimeoutShowTheirValueOnTheButton() {
        XCTAssertEqual(CoreLook.customMtuTitle(ConnectionSettings(mtu: 1360)), "1360")
        XCTAssertNil(CoreLook.customMtuTitle(ConnectionSettings(mtu: 1420)), "MTU из меню — подпись пункта")
        XCTAssertNil(CoreLook.customMtuTitle(ConnectionSettings()))
        XCTAssertNil(CoreLook.customMtuTitle(ConnectionSettings(mtu: 9000)), "негодный MTU — «Авто»")
        XCTAssertNil(CoreLook.mtuSelection(ConnectionSettings(mtu: 9000)))
        XCTAssertEqual(CoreLook.mtuSelection(ConnectionSettings(mtu: 1280)), 1280)
        XCTAssertNil(CoreLook.customTimeoutTitle(ConnectionSettings(), texts))
        XCTAssertEqual(CoreLook.customTimeoutTitle(ConnectionSettings(connectTimeout: 15), texts), "15 с")
    }

    func testCoreRowsSayWhatThePhoneHas() {
        for language in PgTexts.Language.allCases {
            let texts = PgTexts(language)
            XCTAssertEqual(CoreLook.version(nil, texts), texts.coreVersionNone, "до первого подключения версии ядра нет")
            XCTAssertEqual(CoreLook.version("", texts), texts.coreVersionNone)
            XCTAssertEqual(CoreLook.version("26.3.27", texts), "26.3.27")
            let note = CoreLook.secondNote(texts)
            XCTAssertEqual(note.lowercased(), texts.androidCardCoreNone.lowercased())
            XCTAssertEqual(note.first.map { String($0) }, note.first.map { String($0).uppercased() }, "с заглавной буквы")
            XCTAssertFalse(note.contains("sing-box"), "sing-box на телефоне нет")
        }

        XCTAssertTrue(CoreLook.secondNote(PgTexts(.russian)).hasPrefix("Нет"), CoreLook.secondNote(PgTexts(.russian)))
    }

    /// «Маршрутизация»: сегменты и подпись по режиму с именем iOS, действия по порядку доски, новое правило — «Напрямую» в
    /// раздельном режиме и «Блокировать» в режиме «Весь трафик», «Напрямую» вне раздельного режима — бледнее.
    func testRoutingLookFollowsTheBoard() {
        for language in PgTexts.Language.allCases {
            let texts = PgTexts(language)
            XCTAssertEqual(RoutingLook.modeOptions(texts).map(\.value), [.allTraffic, .split])
            XCTAssertEqual(RoutingLook.modeOptions(texts).map(\.title), [texts.routingModeAll, texts.routingModeSplit])
            let all = RoutingLook.modeNote(.allTraffic, texts)
            XCTAssertTrue(all.contains("iOS"), all)
            XCTAssertFalse(all.contains("{"), all)
            XCTAssertEqual(RoutingLook.modeNote(.split, texts), texts.routingModeSplitNote)
            XCTAssertEqual(RoutingLook.actionOptions(texts).map(\.value), [.direct, .proxy, .block])
            XCTAssertEqual(RoutingLook.actionOptions(texts).map(\.title), [texts.routingActionDirect, texts.routingActionProxy, texts.routingActionBlock])
            XCTAssertEqual(RoutingLook.lanText(false, texts), texts.routingLanDenied)
            XCTAssertEqual(RoutingLook.lanText(true, texts), texts.routingLanAllowed)
            // geosite телефон не принимает — подсказка и ошибка его не предлагают, остальное перечисляют как у десктопа.
            for text in [RoutingLook.patternHint(texts), RoutingLook.patternBad(texts)] {
                XCTAssertFalse(text.contains("geosite"), text)
                XCTAssertFalse(text.contains(", ,"), text)
            }

            XCTAssertTrue(RoutingLook.patternHint(texts).contains("example.com"))
            XCTAssertTrue(RoutingLook.patternHint(texts).contains("*.local"))
            XCTAssertTrue(RoutingLook.patternHint(texts).contains("203.0.113.0/24"))
            XCTAssertLessThan(RoutingLook.patternBad(texts).count, texts.routingRuleBad.count)
        }

        XCTAssertEqual(
            RoutingLook.patternHint(PgTexts(.russian)),
            "example.com — сайт и его поддомены, *.local — все имена в зоне, 203.0.113.0/24 — адреса подсети."
        )
        XCTAssertEqual(RoutingLook.patternBad(PgTexts(.russian)), "Нужен домен, *.домен, адрес или подсеть")
        XCTAssertEqual(RoutingLook.patternBad(PgTexts(.english)), "Enter a domain, *.domain, an address or a subnet")

        XCTAssertEqual(RoutingLook.newAction(split: true), .direct)
        XCTAssertEqual(RoutingLook.newAction(split: false), .block, "«Напрямую» в режиме «Весь трафик» не действует")
        XCTAssertTrue(RoutingLook.isIdle(.direct, split: false))
        XCTAssertFalse(RoutingLook.isIdle(.direct, split: true))
        XCTAssertFalse(RoutingLook.isIdle(.block, split: false))
        XCTAssertFalse(RoutingLook.isIdle(.proxy, split: false))
        XCTAssertEqual(RoutingLook.pillColors(.direct).tint, PgColor.Status.Warning.tint)
        XCTAssertEqual(RoutingLook.pillColors(.proxy).tint, PgColor.Status.Success.tint)
        XCTAssertEqual(RoutingLook.pillColors(.block).tint, PgColor.Status.Danger.tint)
    }

    /// «Подключение и ядро» и «Маршрутизация» через модель окна — в общую папку, их читает расширение туннеля.
    @MainActor
    func testConnectionAndRoutingAreKeptInTheSharedFolder() async throws {
        let container = SharedContainer(directory: directory, vault: MemoryVault())
        let model = AppModel(container: container, texts: texts)
        await model.reload()
        XCTAssertEqual(model.settings.connection, ConnectionSettings())
        XCTAssertEqual(model.settings.routing, RoutingSettings())

        let connection = ConnectionSettings(coreLog: .info, mtu: 1360, fingerprint: .chrome, mux: true, tlsFragment: true, connectTimeout: 20)
        await model.setConnection(connection)
        XCTAssertEqual(model.settings.connection, connection)
        let routing = RoutingSettings(mode: .split, rules: [SiteRule(pattern: "example.com", action: .direct)]).saving(SiteRule(pattern: "203.0.113.0/24", action: .block))
        await model.setRouting(routing)
        XCTAssertEqual(model.settings.routing, routing)

        let saved = try await container.settings.load()
        XCTAssertEqual(saved.connection, connection)
        XCTAssertEqual(saved.routing, routing)
        XCTAssertEqual(saved.core().mtu, 1360, "MTU — в план туннеля и ядро")
        XCTAssertTrue(saved.killSwitch, "остальное не тронуто")
    }

    func testLanguageRowNamesTheAppLanguage() {
        XCTAssertEqual(SettingsLook.languageName(PgTexts(.russian)), "Русский")
        XCTAssertEqual(SettingsLook.languageName(PgTexts(.english)), "English")
        XCTAssertTrue(SettingsLook.languageNote(PgTexts(.russian)).hasPrefix("Как в системе"), SettingsLook.languageNote(PgTexts(.russian)))
        XCTAssertTrue(SettingsLook.languageNote(PgTexts(.english)).hasPrefix("As in the system"), SettingsLook.languageNote(PgTexts(.english)))
    }

    func testFooterHasTheAppAndCoreVersionsWithoutTheService() {
        XCTAssertEqual(SettingsLook.footer(AboutInfo(app: "0.1.0", core: "26.3.27"), texts), "Private Gateway Client 0.1.0 · Ядро: Xray-core 26.3.27")
        XCTAssertEqual(SettingsLook.footer(AboutInfo(app: "0.1.0"), texts), "Private Gateway Client 0.1.0 · Ядро: Xray-core", "до первого подключения версии ядра нет")
        let english = SettingsLook.footer(AboutInfo(app: "0.1.0", core: "26.3.27"), PgTexts(.english))
        XCTAssertEqual(english, "Private Gateway Client 0.1.0 · Core: Xray-core 26.3.27")
        XCTAssertFalse(english.lowercased().contains("service"), "службы у телефона нет")
    }

    /// «DNS в туннеле»: меню из трех вариантов и «Свой адрес…» под чертой; он открывает лист и сам выбранным не становится.
    @MainActor
    func testDnsMenuOffersThreeChoicesAndTheCustomAddressOpensTheSheet() {
        let layers = PgLayers()
        let anchor = UUID()
        var selected: [DnsChoice] = []
        var sheets = 0
        let open = { (selection: DnsChoice) in
            layers.openMenu(PgSelect<DnsChoice>.menu(
                anchor: anchor, options: ProtectionLook.dnsOptions(self.texts), selection: selection, layers: layers, confirm: { _ in nil },
                link: PgMenuLink(title: self.texts.protDnsCustom) { sheets += 1 }, onSelect: { selected.append($0) }
            ))
        }

        open(.profile)
        XCTAssertEqual(layers.menu?.items.map(\.title), [texts.protDnsProfile, texts.protDnsCloudflare, texts.protDnsQuad9, texts.protDnsCustom])
        XCTAssertEqual(layers.menu?.items.map(\.current), [true, false, false, false])
        XCTAssertEqual(layers.menu?.items.map(\.link), [false, false, false, true], "«Свой адрес…» — ссылкой под чертой")
        layers.pick(3)
        XCTAssertNil(layers.menu)
        XCTAssertEqual(sheets, 1, "лист открыт")
        XCTAssertEqual(selected, [], "выбор — только после «Сохранить» в листе")

        open(.custom)
        XCTAssertEqual(layers.menu?.items.map(\.current), [false, false, false, false], "свой адрес — ни один пункт меню не текущий")
        layers.pick(2)
        XCTAssertEqual(selected, [.quad9Doh])
        XCTAssertEqual(sheets, 1)
    }

    func testCustomDnsTitleIsTheAddress() {
        XCTAssertNil(ProtectionLook.customDnsTitle(TunnelSettings()), "не свой адрес — подпись пункта")
        XCTAssertNil(ProtectionLook.customDnsTitle(TunnelSettings(dns: .quad9Doh, customDns: "192.0.2.53")))
        XCTAssertNil(ProtectionLook.customDnsTitle(TunnelSettings(dns: .custom, customDns: "  ")))
        XCTAssertEqual(ProtectionLook.customDnsTitle(TunnelSettings(dns: .custom, customDns: " 192.0.2.53 ")), "192.0.2.53")
        XCTAssertEqual(ProtectionLook.customDnsTitle(TunnelSettings(dns: .custom, customDns: "https://192.0.2.53/dns-query")), "DoH: 192.0.2.53")
        XCTAssertEqual(ProtectionLook.customDnsTitle(TunnelSettings(dns: .custom, customDns: "https://[2001:db8::1]:8443/dns-query")), "DoH: [2001:db8::1]:8443")
    }

    func testAddressPriorityIsInTheOrderOfTheBoard() {
        let options = ProtectionLook.addressOptions(texts)
        XCTAssertEqual(options.map(\.title), [texts.protAddrIpv4, texts.protAddrOnly4], "«Предпочитать IPv4» первым, как в макете")
        XCTAssertEqual(options.map(\.value), [true, false], "«Предпочитать IPv4» — IPv6 через сервер, «Только IPv4» — IPv6 в блок")
    }

    func testConnectOnLaunchOnlyToTheSelectedServerWhenProtectionIsOff() {
        let on = AppSettings(connectOnLaunch: true)
        let off = HomeState(servers: servers, selectedId: "a")
        XCTAssertTrue(AppModel.connectsOnLaunch(on, home: off, blocked: false))
        XCTAssertFalse(AppModel.connectsOnLaunch(AppSettings(), home: off, blocked: false), "настройка выключена")
        XCTAssertFalse(AppModel.connectsOnLaunch(on, home: HomeState(servers: servers), blocked: false), "сервер не выбран — выбирает только пользователь")
        XCTAssertFalse(AppModel.connectsOnLaunch(on, home: HomeState(), blocked: false), "серверов нет")
        for state in [TunnelState.connecting, .connected, .reconnecting] {
            let running = HomeState(status: TunnelStatus(state: state, serverId: "a"), servers: servers, selectedId: "a")
            XCTAssertFalse(AppModel.connectsOnLaunch(on, home: running, blocked: false), "\(state): защита уже идет")
        }

        let error = HomeState(status: TunnelStatus(state: .error, error: TunnelErrors.coreFailed), servers: servers, selectedId: "a")
        XCTAssertTrue(AppModel.connectsOnLaunch(on, home: error, blocked: false), "«Ошибка» без туннеля — защиты нет")
        XCTAssertFalse(AppModel.connectsOnLaunch(on, home: error, blocked: true), "«Блокировка» — кнопка сняла бы защиту")
    }

    @MainActor
    func testConnectOnLaunchIsKeptInTheSharedFolder() async throws {
        let container = SharedContainer(directory: directory, vault: MemoryVault())
        let model = AppModel(container: container, texts: texts)
        await model.reload()
        XCTAssertFalse(model.settings.connectOnLaunch)

        await model.setConnectOnLaunch(true)
        XCTAssertTrue(model.settings.connectOnLaunch)
        let saved = try await container.settings.load()
        XCTAssertTrue(saved.connectOnLaunch)
        XCTAssertTrue(saved.killSwitch, "остальное не тронуто")

        // Серверов нет — подключаться не к чему: защита остается выключенной.
        await model.connectOnLaunch()
        XCTAssertEqual(model.home.status.state, .off)

        await model.setConnectOnLaunch(false)
        XCTAssertFalse(model.settings.connectOnLaunch)
    }

    /// Строки «Для опытных» крупные (плитка 36), лист своего DNS и меню у кнопки с адресом, значение в рамке — на обоих языках.
    @MainActor
    func testBlocksOfTheBoardsDrawInBothLanguages() throws {
        for language in PgTexts.Language.allCases {
            let texts = PgTexts(language)
            let view = VStack(spacing: PgSpace.card) {
                SettingsGroup(title: texts.settingsSectionAdvanced, inset: PgSpace.xxs) {
                    PgLinkRow(icon: PgIcons.advCore, title: texts.settingsAdvCoreTitle, caption: texts.settingsAdvCoreText, large: true) {}
                    PgDivider().padding(.horizontal, PgSpace.card)
                    PgLinkRow(icon: PgIcons.advRouting, title: texts.settingsAdvRoutingTitle, caption: texts.settingsAdvRoutingText, large: true) {}
                    PgDivider().padding(.horizontal, PgSpace.card)
                    PgLinkRow(icon: PgIcons.advCheck, title: texts.settingsAdvCheckTitle, caption: texts.settingsAdvCheckText, large: true) {}
                    PgDivider().padding(.horizontal, PgSpace.card)
                    PgLinkRow(icon: PgIcons.file, title: texts.aboutLicenses, caption: texts.aboutLicensesText, large: true) {}
                }
                SettingsGroup(title: texts.coreGroupCore) {
                    PgSettingRow(title: texts.coreXrayTitle, caption: texts.coreXrayText) {
                        PgValueBox(text: "26.3.27")
                    }
                }
                SettingsGroup(title: texts.protGroupDns) {
                    PgSettingRow(title: texts.protDnsTitle, caption: texts.protDnsText) {
                        PgSelect(
                            options: ProtectionLook.dnsOptions(texts), selection: DnsChoice.custom, label: texts.protDnsTitle, valueTitle: "DoH: 192.0.2.53",
                            link: PgMenuLink(title: texts.protDnsCustom) {}, onSelect: { _ in }
                        )
                    }
                }
            }
            .padding(.horizontal, PgSpace.windowSideMobile)
            let image = try XCTUnwrap(ImageRenderer(content: view.frame(width: 393, height: 600)).uiImage)
            XCTAssertEqual(image.size.width, 393, accuracy: 1)
        }
    }
}

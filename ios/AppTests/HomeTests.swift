import PrivateGatewayCore
import PrivateGatewayTheme
import SwiftUI
import UIKit
import XCTest
@testable import PrivateGateway

/// Тема SwiftUI и главная на симуляторе: цвет токена не теряет каналов, насыщенность шрифта — ближайшая, иконка ложится в
/// свой прямоугольник; плитки, вид состояния, строка проверки, карточка IP, быстрая смена и скорость — по состоянию
/// туннеля, как у десктопа; главная рисуется в каждом состоянии.
final class HomeTests: XCTestCase {
    private let texts = PgTexts(.russian)

    private let servers = [
        ServerItem(id: "a", linkName: "Нидерланды 01", countryCode: "NL", protocol: .vless, security: .reality, transport: .tcp, port: 443, latency: .of(48)),
        ServerItem(id: "b", linkName: "Германия", countryCode: "DE", protocol: .trojan, security: .tls, transport: .webSocket, port: 443, latency: .of(30)),
        ServerItem(id: "h", linkName: "Финляндия", countryCode: "FI", protocol: .hysteria2, security: .tls, transport: .quic, port: 8443, latency: .of(5)),
        ServerItem(id: "c", linkName: "Швеция", countryCode: "", protocol: .vmess, security: .none, transport: .tcp, port: 80, latency: .failed(.noResponse)),
        ServerItem(id: "d", linkName: "Польша", countryCode: "PL", protocol: .vless, security: .tls, transport: .xhttp, port: 443, latency: .of(90)),
    ]

    @MainActor
    func testTokenColorKeepsItsChannels() {
        var red: CGFloat = 0
        var green: CGFloat = 0
        var blue: CGFloat = 0
        var alpha: CGFloat = 0
        XCTAssertTrue(UIColor(Color(PgRGBA(argb: 0x8020_40FF))).getRed(&red, green: &green, blue: &blue, alpha: &alpha))
        XCTAssertEqual(alpha, 128.0 / 255, accuracy: 0.002)
        XCTAssertEqual(red, 32.0 / 255, accuracy: 0.002)
        XCTAssertEqual(green, 64.0 / 255, accuracy: 0.002)
        XCTAssertEqual(blue, 1, accuracy: 0.002)
    }

    @MainActor
    func testFontWeightIsTheNearestOne() {
        XCTAssertEqual(PgType.body.fontWeight, .regular)
        XCTAssertEqual(PgType.nav.fontWeight, .medium)
        XCTAssertEqual(PgType.title.fontWeight, .semibold)
        XCTAssertEqual(PgType.display.fontWeight, .bold)
        XCTAssertEqual(PgType.body.extraLineSpacing, (1.45 - 1.2) * 14, accuracy: 0.001)
        XCTAssertEqual(PgType.title.extraLineSpacing, 0)
    }

    @MainActor
    func testIconFitsItsRectangle() {
        let rect = CGRect(x: 10, y: 20, width: 48, height: 48)
        for (name, data) in PgIcons.all {
            let bounds = PgIconShape(data: data).path(in: rect).boundingRect
            XCTAssertGreaterThan(bounds.width + bounds.height, 0, name)
            XCTAssertTrue(rect.insetBy(dx: -1, dy: -1).contains(bounds), "\(name): \(bounds)")
        }

        // Щит: x 4,5–19,5 и y 2,8–21,2 из 24 → вдвое крупнее и со сдвигом прямоугольника.
        let shield = PgIconShape(data: PgIcons.shield).path(in: rect).boundingRect
        XCTAssertEqual(shield.minX, 10 + 4.5 * 2, accuracy: 0.01)
        XCTAssertEqual(shield.minY, 20 + 2.8 * 2, accuracy: 0.01)
        XCTAssertEqual(shield.maxX, 10 + 19.5 * 2, accuracy: 0.01)
    }

    @MainActor
    func testChipsFollowTheTunnel() {
        let connected = ProtectionChips.of(.connected, killSwitch: true, texts)
        XCTAssertEqual(connected.map(\.value), [texts.checkAllOk, texts.checkDnsOk, texts.checkIpv6Ok, texts.checkKsOk])
        XCTAssertEqual(connected.map(\.look), [.ok, .ok, .ok, .ok])
        XCTAssertEqual(connected.map(\.text), [texts.checkAllText, texts.checkDnsText, texts.checkIpv6Text, texts.checkKsText])

        // «Подключение» и «Переподключение» (доска 09): весь трафик поднимается, DNS и IPv6 уже в туннеле, блок — «Kill Switch вкл».
        for state in [TunnelState.connecting, .reconnecting] {
            let waiting = ProtectionChips.of(state, killSwitch: true, texts)
            XCTAssertEqual(waiting.map(\.look), [.waiting, .ok, .ok, .ok], "\(state)")
            XCTAssertEqual(waiting.map(\.value), [texts.checkAllWaiting, texts.checkDnsOk, texts.checkIpv6Ok, texts.checkKsOk], "\(state)")
        }

        XCTAssertEqual(ProtectionChips.of(.reconnecting, killSwitch: false, texts)[3].look, .off, "без блока при обрыве — «выключен»")
        XCTAssertEqual(ProtectionChips.of(.reconnecting, killSwitch: false, texts)[3].value, texts.checkKsOff)

        let off = ProtectionChips.of(.off, killSwitch: true, texts)
        XCTAssertEqual(off.map(\.value), [texts.checkAllOff, texts.checkDnsOff, texts.checkIpv6Off, texts.checkKsOff])
        XCTAssertEqual(off.map(\.look), [.off, .off, .off, .off], "без туннеля блока нет")
        XCTAssertEqual(ProtectionChips.of(.error, killSwitch: true, texts).map(\.look), [.off, .off, .off, .off], "«Ошибка» без туннеля — трафик напрямую")

        // «Блокировка»: туннель оставлен без ядра — весь трафик «остановлен» (оранжевый), остальное держится.
        let blocked = ProtectionChips.of(.error, killSwitch: true, texts, blocked: true)
        XCTAssertEqual(blocked.map(\.look), [.stopped, .ok, .ok, .ok])
        XCTAssertEqual(blocked.map(\.value), [texts.checkAllStopped, texts.checkDnsOk, texts.checkIpv6Ok, texts.checkKsOk])

        // «Утечка IP»: весь трафик — «реальный IP открыт» янтарным со своим пояснением.
        let leak = ProtectionChips.of(.connected, killSwitch: true, texts, leak: true)
        XCTAssertEqual(leak.map(\.look), [.warning, .ok, .ok, .ok])
        XCTAssertEqual(leak[0].value, texts.checkAllSameIp)
        XCTAssertEqual(leak[0].text, texts.checkAllSameIpText)
        XCTAssertEqual(ProtectionChips.of(.off, killSwitch: true, texts, leak: true).map(\.look), [.off, .off, .off, .off], "утечка — только под защитой")

        XCTAssertEqual(ProtectionChips.of(.connected, killSwitch: false, texts)[3].look, .off)
        XCTAssertEqual(ProtectionChips.of(.error, killSwitch: true, texts).map(\.title), [texts.checkAllTitle, texts.checkDnsTitle, texts.checkIpv6Title, texts.checkKsTitle])
    }

    @MainActor
    func testChipValueIsTheColorOfItsTone() {
        // CheckBrushConverter десктопа: подпись значения — текст тона, «остановлен» — оранжевый status.warning, а не янтарный.
        XCTAssertEqual(ChipLook.ok.text, PgColor.Status.Success.text)
        XCTAssertEqual(ChipLook.waiting.text, PgColor.Accent.text)
        XCTAssertEqual(ChipLook.off.text, PgColor.Status.Danger.text)
        XCTAssertEqual(ChipLook.warning.text, PgColor.Status.Caution.text)
        XCTAssertEqual(ChipLook.stopped.text, PgColor.Status.Warning.text)
        XCTAssertEqual(ChipLook.stopped.base, PgColor.Status.Warning.base)
        XCTAssertEqual(ChipLook.stopped.tint, PgColor.Status.Warning.tint)
        XCTAssertEqual(ChipLook.stopped.mark, PgIcons.chipDash)
        XCTAssertEqual(ChipLook.ok.hover, PgColor.Status.Success.hover, "выбранная плитка — подложка темнее")
        XCTAssertNotEqual(ChipLook.ok.hover, ChipLook.ok.tint)
    }

    @MainActor
    func testChipOpensItsExplanation() throws {
        XCTAssertEqual(StatusCard.toggle(nil, 2), 2, "нажатие открывает пояснение плитки")
        XCTAssertNil(StatusCard.toggle(2, 2), "повторное — закрывает")
        XCTAssertEqual(StatusCard.toggle(2, 0), 0, "другая плитка — ее пояснение")

        let chips = ProtectionChips.of(.connected, killSwitch: true, texts)
        XCTAssertNil(StatusCard.info(chips, selected: nil), "ничего не выбрано — пояснения нет")
        XCTAssertEqual(StatusCard.info(chips, selected: 1)?.title, texts.checkDnsTitle)
        XCTAssertEqual(StatusCard.info(chips, selected: 1)?.text, texts.checkDnsText)
        XCTAssertEqual(StatusCard.info(chips, selected: 3)?.text, texts.checkKsText)
        XCTAssertNil(StatusCard.info(chips, selected: 9))

        let home = HomeState(status: TunnelStatus(state: .connected, serverId: "a", since: Date()), servers: servers, selectedId: "a")
        for selected in [nil, 0, 3] as [Int?] {
            let card = StatusCard(texts: texts, home: home, killSwitch: true, selected: selected).frame(width: 393)
            XCTAssertNotNil(ImageRenderer(content: card).uiImage)
        }

        // С открытым пояснением карточка выше: вставка под плитками действительно рисуется.
        let closed = try XCTUnwrap(ImageRenderer(content: StatusCard(texts: texts, home: home, killSwitch: true).frame(width: 393)).uiImage)
        let open = try XCTUnwrap(ImageRenderer(content: StatusCard(texts: texts, home: home, killSwitch: true, selected: 0).frame(width: 393)).uiImage)
        XCTAssertGreaterThan(open.size.height, closed.size.height)
    }

    @MainActor
    func testStateLookIsColorAndText() {
        XCTAssertEqual(StateLook.of(.connected, texts).title, "IP скрыт")
        XCTAssertEqual(StateLook.of(.connected, texts).ring, PgState.Connected.ring)
        XCTAssertEqual(StateLook.of(.off, texts).power, texts.powerLabelOff)
        XCTAssertEqual(StateLook.of(.reconnecting, texts).ring, StateLook.of(.connecting, texts).ring, "переподключение — вид подключения")
        XCTAssertNotEqual(StateLook.of(.error, texts).mark, StateLook.of(.connected, texts).mark, "состояние видно не только цветом")
        XCTAssertEqual(StateLook.of(.error, texts).title, texts.statusErrorTitle)
        XCTAssertEqual(StatusCard.online(Date(timeIntervalSince1970: 0), now: Date(timeIntervalSince1970: 8_049)), "02:14:09")

        let blocked = StateLook.of(.error, texts, blocked: true)
        XCTAssertEqual(blocked.title, texts.statusBlockedTitle)
        XCTAssertEqual(blocked.power, texts.powerLabelBlocked)
        XCTAssertEqual(blocked.ring, PgState.Blocked.ring, "«Блокировка» — оранжевая")
        XCTAssertEqual(blocked.mark, PgIcons.markAlert)
        XCTAssertEqual(StateLook.of(.off, texts, blocked: true), StateLook.of(.off, texts), "«Блокировка» бывает только у «Ошибки»")

        let leak = StateLook.of(.connected, texts, leak: true)
        XCTAssertEqual(leak.title, texts.statusLeakTitle)
        XCTAssertEqual(leak.power, texts.powerLabelLeak, "своя подпись под щитом")
        XCTAssertEqual(leak.ring, PgState.Error.ring, "тон «Ошибки»")
        XCTAssertEqual(leak.mark, PgIcons.markAlert)
        XCTAssertEqual(StateLook.of(.connecting, texts, leak: true), StateLook.of(.connecting, texts))
    }

    @MainActor
    func testSubtitleFollowsTheState() {
        let server = servers[0]
        let status = { (state: TunnelState, error: String?) in TunnelStatus(state: state, serverId: "a", error: error, attempt: 2) }
        XCTAssertEqual(StatusText.subtitle(status(.connecting, nil), blocked: false, leak: false, server: server, texts), texts.statusConnectingSub,
                       "«Интернет на паузе, утечки нет», а не имя сервера")
        XCTAssertEqual(StatusText.subtitle(status(.connected, nil), blocked: false, leak: false, server: server, texts), texts.statusConnectedSub)
        XCTAssertEqual(StatusText.subtitle(status(.connected, nil), blocked: false, leak: true, server: server, texts), texts.statusLeakSub)
        let plain = ServerItem(id: "p", linkName: "Без шифрования", countryCode: "", protocol: .vless, security: .none, transport: .tcp, port: 80)
        XCTAssertEqual(StatusText.subtitle(status(.connected, nil), blocked: false, leak: false, server: plain, texts), texts.statusConnectedSubPlain)
        XCTAssertEqual(StatusText.subtitle(status(.reconnecting, nil), blocked: false, leak: false, server: server, texts), texts.statusReconnectingSub(2))
        XCTAssertEqual(StatusText.subtitle(status(.off, nil), blocked: false, leak: false, server: server, texts), texts.statusOffSub)

        let lost = status(.error, TunnelErrors.serverNotResponding)
        XCTAssertEqual(StatusText.subtitle(lost, blocked: true, leak: false, server: server, texts), texts.statusBlockedSub)
        XCTAssertEqual(StatusText.subtitle(status(.error, TunnelErrors.coreFailed), blocked: true, leak: false, server: server, texts),
                       texts.statusErrorSub(TunnelErrors.coreFailed), "«код · трафик заблокирован»")
        XCTAssertEqual(StatusText.subtitle(lost, blocked: false, leak: false, server: server, texts), texts.statusErrorSubDirect(TunnelErrors.serverNotResponding),
                       "туннеля нет — трафик идет напрямую")
    }

    @MainActor
    func testCheckLineFollowsTheDesktopRules() {
        let now = Date(timeIntervalSince1970: 1_800_000_000)
        let checked = WebIp(exit: "203.0.113.24", checkedAt: now.addingTimeInterval(-3))
        XCTAssertEqual(CheckLine.of(.connected, blocked: false, ip: checked, now: now, texts), CheckLine(text: texts.checkedNow, action: .check))
        let earlier = CheckLine.of(.connected, blocked: false, ip: checked, now: now.addingTimeInterval(120), texts)
        XCTAssertTrue(earlier.text.hasPrefix(texts.checkedAt("").trimmingCharacters(in: .whitespaces)), earlier.text)
        XCTAssertNotEqual(earlier.text, texts.checkedNow)
        XCTAssertEqual(earlier.action, .check)

        var running = checked
        running.checking = true
        XCTAssertEqual(CheckLine.of(.connected, blocked: false, ip: running, now: now, texts), CheckLine(text: texts.checkedRunning, action: nil),
                       "идет проверка — кнопки нет")
        XCTAssertEqual(CheckLine.of(.connected, blocked: false, ip: WebIp(), now: now, texts), CheckLine(text: texts.checkedConnecting, action: .check))

        XCTAssertEqual(CheckLine.of(.off, blocked: false, ip: checked, now: now, texts), CheckLine(text: texts.checkedOff, action: nil))
        XCTAssertEqual(CheckLine.of(.connecting, blocked: false, ip: checked, now: now, texts), CheckLine(text: texts.checkedConnecting, action: nil))
        XCTAssertEqual(CheckLine.of(.reconnecting, blocked: false, ip: checked, now: now, texts), CheckLine(text: texts.checkedReconnecting, action: nil))
        XCTAssertEqual(CheckLine.of(.error, blocked: false, ip: checked, now: now, texts), CheckLine(text: texts.checkedError, action: .retry))
        XCTAssertEqual(CheckLine.of(.error, blocked: true, ip: checked, now: now, texts), CheckLine(text: texts.checkedBlocked, action: .retry))

        XCTAssertEqual(CheckLine.tone(.connected, blocked: false), PgState.Connecting.icon, "«Проверить» — синим")
        XCTAssertEqual(CheckLine.tone(.error, blocked: true), PgState.Blocked.icon)
        XCTAssertEqual(CheckLine.tone(.error, blocked: false), PgState.Error.icon)
    }

    @MainActor
    func testWebIpCardFollowsTheState() {
        let ip = WebIp(exit: "203.0.113.24", direct: "198.51.100.7", info: AddressInfo(
            address: "203.0.113.24", asn: 64500, holder: "EXAMPLE-AS", prefix: "203.0.113.0/24", country: "NL", city: "Amsterdam", failure: nil
        ))

        let connected = WebIpLook.of(.connected, blocked: false, ip: ip, masked: false, texts)
        XCTAssertEqual(connected.ip, "203.0.113.24", "под защитой сайты видят адрес выхода")
        XCTAssertEqual(connected.meta, "Amsterdam · AS64500")
        XCTAssertEqual(connected.real, "198.51.100.7")
        XCTAssertFalse(connected.realVisible, "реальный — скрыт")
        XCTAssertTrue(connected.canCopy)

        var unknown = ip
        unknown.info = nil
        XCTAssertNil(WebIpLook.of(.connected, blocked: false, ip: unknown, masked: false, texts).meta, "о сети адреса не спрашивали — ничего не выдумываем")

        let off = WebIpLook.of(.off, blocked: false, ip: ip, masked: false, texts)
        XCTAssertEqual(off.ip, "198.51.100.7", "без защиты — реальный")
        XCTAssertEqual(off.meta, texts.ipMetaOff)
        XCTAssertTrue(off.realVisible)

        for state in [TunnelState.connecting, .reconnecting] {
            let waiting = WebIpLook.of(state, blocked: false, ip: ip, masked: false, texts)
            XCTAssertEqual(waiting.ip, "—", "\(state)")
            XCTAssertFalse(waiting.canCopy, "\(state): «—» не копируется")
            XCTAssertEqual(waiting.meta, texts.ipMetaWaiting)
            XCTAssertFalse(waiting.realVisible)
        }

        let blocked = WebIpLook.of(.error, blocked: true, ip: ip, masked: false, texts)
        XCTAssertEqual(blocked.ip, "—")
        XCTAssertEqual(blocked.meta, texts.ipMetaStopped, "«Трафик остановлен — утечки нет»")
        XCTAssertFalse(blocked.realVisible)

        let error = WebIpLook.of(.error, blocked: false, ip: ip, masked: false, texts)
        XCTAssertEqual(error.ip, "198.51.100.7", "«Ошибка» без туннеля — трафик напрямую")
        XCTAssertTrue(error.realVisible)

        let nothing = WebIpLook.of(.off, blocked: false, ip: WebIp(), masked: false, texts)
        XCTAssertEqual(nothing.ip, "—", "не мерили — прочерк")
        XCTAssertNil(nothing.real, "реального нет — строки нет")
        XCTAssertNil(nothing.plain)

        let leakIp = WebIp(exit: "198.51.100.7", direct: "198.51.100.7")
        XCTAssertTrue(HomeFacts(ip: leakIp).leak(.connected))
        XCTAssertFalse(HomeFacts(ip: leakIp).leak(.off))
        XCTAssertFalse(HomeFacts(ip: ip).leak(.connected))
        XCTAssertFalse(HomeFacts(ip: WebIp(direct: "198.51.100.7")).leak(.connected), "адрес выхода не измерен — утечки не видно")
        let leak = WebIpLook.of(.connected, blocked: false, ip: leakIp, masked: false, texts)
        XCTAssertEqual(leak.ip, "198.51.100.7")
        XCTAssertTrue(leak.realVisible, "утечка — реальный «виден сайтам»")
    }

    @MainActor
    func testEyeHidesTheIpWithStars() {
        let ip = WebIp(exit: "203.0.113.24", direct: "198.51.100.7")
        let masked = WebIpLook.of(.connected, blocked: false, ip: ip, masked: true, texts)
        XCTAssertEqual(masked.ip, "***.*.***.**", "цифры — звездочки, точки остаются (как десктоп)")
        XCTAssertEqual(masked.real, "***.**.***.*")
        XCTAssertFalse(masked.canCopy, "скрытый не копируется")
        XCTAssertEqual(masked.plain, "203.0.113.24")
        XCTAssertEqual(WebIpLook.mask("2001:db8::a1"), "****:***::**")
        XCTAssertEqual(WebIpLook.of(.connecting, blocked: false, ip: ip, masked: true, texts).ip, "—", "прочерк не прячется")
    }

    @MainActor
    func testCopyShowsTheToast() {
        let layers = PgLayers()
        var copied: [String] = []
        let ip = WebIp(exit: "203.0.113.24", direct: "198.51.100.7")

        IpCopy.copy(WebIpLook.of(.connected, blocked: false, ip: ip, masked: false, texts), layers: layers, texts: texts) { copied.append($0) }
        XCTAssertEqual(copied, ["203.0.113.24"])
        XCTAssertEqual(layers.toast, texts.ipCopied, "«IP скопирован»")
        layers.hideToast()

        IpCopy.copy(WebIpLook.of(.connected, blocked: false, ip: ip, masked: true, texts), layers: layers, texts: texts) { copied.append($0) }
        IpCopy.copy(WebIpLook.of(.connecting, blocked: false, ip: ip, masked: false, texts), layers: layers, texts: texts) { copied.append($0) }
        XCTAssertEqual(copied, ["203.0.113.24"], "скрытый и «—» не копируются")
        XCTAssertNil(layers.toast)
    }

    @MainActor
    func testSwitchOpensTheMenuAndPicksAServer() {
        let home = HomeState(status: TunnelStatus(state: .connected, serverId: "a"), servers: servers, selectedId: "a")
        XCTAssertEqual(home.inUse?.id, "a")
        XCTAssertEqual(QuickMenu.servers(home).map(\.id), ["a", "b", "d"], "сервер в работе и два самых быстрых; Hysteria2 телефон не умеет")
        XCTAssertEqual(QuickMenu.title(servers[0]), "NL · Нидерланды 01")
        XCTAssertEqual(QuickMenu.title(servers[3]), "Швеция")

        let layers = PgLayers()
        let anchor = UUID()
        var chosen: [String] = []
        var all = 0
        let open = {
            HomeServerRow.open(in: layers, anchor: anchor, home: home, texts: self.texts, onSelect: { chosen.append($0) }, onAll: { all += 1 })
        }

        open()
        let menu = layers.menu
        XCTAssertEqual(menu?.anchor, anchor, "меню — у кнопки «Сменить»")
        XCTAssertEqual(menu?.title, texts.serverQuick)
        XCTAssertEqual(menu?.items.map(\.title), ["Нидерланды 01", "Германия", "Польша", texts.serverAll])
        XCTAssertEqual(menu?.items.map(\.current), [true, false, false, false], "сервер в работе отмечен")
        XCTAssertEqual(menu?.items.map(\.badge), ["NL", "DE", "PL", nil])
        XCTAssertEqual(menu?.items.map(\.caption), ["VLESS · REALITY", "Trojan · WS", "VLESS · XHTTP", nil], "протокол второй строкой")
        XCTAssertEqual(menu?.items.map(\.trailing), ["48 \(texts.unitMs)", "30 \(texts.unitMs)", "90 \(texts.unitMs)", nil])
        XCTAssertEqual(menu?.items.map(\.link), [false, false, false, true], "«Все серверы ›» — под чертой")

        layers.pick(1)
        XCTAssertNil(layers.menu, "выбор закрывает меню")
        XCTAssertEqual(chosen, ["b"])

        open()
        layers.pick(0)
        XCTAssertEqual(chosen, ["b"], "сервер в работе — ничего")

        open()
        layers.pick(3)
        XCTAssertEqual(all, 1, "«Все серверы ›»")
        XCTAssertEqual(chosen, ["b"])

        HomeServerRow.open(in: nil, anchor: anchor, home: home, texts: texts, onSelect: { chosen.append($0) }, onAll: { all += 1 })
        XCTAssertNil(layers.menu, "без слоев окна меню не открыть")

        let nothing = QuickMenu.make(anchor: anchor, home: HomeState(), texts: texts, onSelect: { _ in }, onAll: {})
        XCTAssertEqual(nothing.items.map(\.title), [texts.serverAll], "серверов нет — только «Все серверы ›»")
    }

    @MainActor
    func testServerRowShowsTheServerInUseAndTheQuickSwitch() throws {
        let servers = [
            ServerItem(id: "a", linkName: "Нидерланды 01", countryCode: "NL", protocol: .vless, security: .reality, transport: .tcp, port: 443, latency: .of(48)),
            ServerItem(id: "b", linkName: "Германия", countryCode: "DE", protocol: .trojan, security: .tls, transport: .webSocket, port: 443, latency: .of(30)),
        ]
        let home = HomeState(status: TunnelStatus(state: .connected, serverId: "b"), servers: servers, selectedId: "a")
        XCTAssertEqual(home.inUse?.id, "b", "под защитой — сервер в работе, а не выбранный")
        let row = HomeServerRow(texts: texts, home: home, onAdd: {}, onServers: {}, onSelect: { _ in }, onRefresh: {}).frame(width: 393)
        XCTAssertNotNil(ImageRenderer(content: row).uiImage)
        XCTAssertEqual(QuickSwitch.pick(servers, inUse: home.inUse).map(\.id), ["b", "a"])

        let menu = QuickMenu.make(anchor: UUID(), home: home, texts: texts, onSelect: { _ in }, onAll: {})
        XCTAssertEqual(menu.items.first?.title, "Германия", "первая строка — сервер в работе")
        XCTAssertEqual(menu.items.first?.current, true)

        // Серверов нет и сервер не выбран — строка все равно рисуется (ведет к добавлению или к списку).
        for empty in [HomeState(), HomeState(servers: servers)] {
            let view = HomeServerRow(texts: texts, home: empty, onAdd: {}, onServers: {}, onSelect: { _ in }, onRefresh: {}).frame(width: 393)
            XCTAssertNotNil(ImageRenderer(content: view).uiImage)
        }
    }

    @MainActor
    func testNetworkLineIsTheKindOnly() {
        XCTAssertEqual(NetworkLine.of(.wifi, texts)?.text, texts.networkWifiNoName, "имени Wi-Fi iOS без отдельного права не дает")
        XCTAssertEqual(NetworkLine.of(.wifi, texts)?.icon, PgIcons.netWiFi)
        XCTAssertEqual(NetworkLine.of(.cellular, texts)?.text, texts.networkMobile)
        XCTAssertEqual(NetworkLine.of(.ethernet, texts)?.text, texts.networkEthernet)
        XCTAssertEqual(NetworkLine.of(.offline, texts)?.text, texts.networkNone)
        XCTAssertEqual(NetworkLine.of(.offline, texts)?.icon, PgIcons.netNone)
        XCTAssertNil(NetworkLine.of(nil, texts), "iOS еще не сказала — строки нет")
        XCTAssertNil(NetworkLine.of(.other, texts))
    }

    @MainActor
    func testRealIpIsForgottenInAnotherNetwork() {
        XCTAssertEqual(NetworkKey.next(nil, kind: .wifi, interface: "en0").key, "wifi-en0")
        XCTAssertFalse(NetworkKey.next(nil, kind: .wifi, interface: "en0").changed, "первая новость — не смена сети")
        XCTAssertFalse(NetworkKey.next("wifi-en0", kind: .wifi, interface: "en0").changed)
        XCTAssertEqual(NetworkKey.next("wifi-en0", kind: .wifi, interface: nil).key, "wifi-en0", "под VPN интерфейс не назван — та же сеть")
        XCTAssertFalse(NetworkKey.next("wifi-en0", kind: .wifi, interface: nil).changed)
        XCTAssertEqual(NetworkKey.next("wifi-en0", kind: .offline, interface: nil).key, "wifi-en0", "сеть пропала на время — не смена")
        XCTAssertFalse(NetworkKey.next("wifi-en0", kind: .offline, interface: nil).changed)
        let cellular = NetworkKey.next("wifi-en0", kind: .cellular, interface: "pdp_ip0")
        XCTAssertEqual(cellular.key, "cellular-pdp_ip0")
        XCTAssertTrue(cellular.changed, "Wi-Fi сменился сотовой — реальный IP другой")
        XCTAssertTrue(NetworkKey.next("wifi-en0", kind: .wifi, interface: "en1").changed)
    }

    @MainActor
    func testSpeedComesFromTheTunnelCounters() throws {
        let first = TrafficSample(received: 1_000_000, sent: 200_000, at: 10)
        let second = TrafficSample(received: 3_362_500, sent: 700_000, at: 11)
        let speed = try XCTUnwrap(TunnelSpeed.between(first, second))
        XCTAssertEqual(speed.down, 18.9, accuracy: 0.001, "2 362 500 байт за секунду — 18,9 Мбит/с")
        XCTAssertEqual(speed.up, 4.0, accuracy: 0.001)

        let wrapped = try XCTUnwrap(TunnelSpeed.between(TrafficSample(received: UInt32.max - 99, sent: 0, at: 0), TrafficSample(received: 900, sent: 0, at: 2)))
        XCTAssertEqual(wrapped.down, 1000.0 * 8 / 2 / 1_000_000, accuracy: 1e-9, "счетчик перешел через ноль — не отрицательная скорость")
        XCTAssertNil(TunnelSpeed.between(first, TrafficSample(received: 2_000_000, sent: 0, at: 10.1)), "слишком близко — скорости нет")
        XCTAssertNil(TunnelCounters.read(address: "203.0.113.250"), "интерфейса с чужим адресом нет — счетчиков нет")

        let home = HomeState(status: TunnelStatus(state: .connected, serverId: "a"), servers: servers, selectedId: "a")
        let shown = SpeedLook.of(.connected, home: home, speed: speed, texts)
        XCTAssertEqual(shown, SpeedLook(ping: "48", down: "18,9", up: "4,0"), "пинг — замер через ядро, скорость — с запятой по-русски")
        XCTAssertEqual(SpeedLook.of(.connected, home: home, speed: speed, PgTexts(.english)).down, "18.9")
        XCTAssertEqual(SpeedLook.of(.connected, home: home, speed: nil, texts), SpeedLook(ping: "48", down: "—", up: "—"),
                       "счетчиков нет — не рисуем выдуманных чисел")
        for state in TunnelState.allCases where state != .connected {
            XCTAssertEqual(SpeedLook.of(state, home: home, speed: speed, texts), SpeedLook(ping: "—", down: "—", up: "—"), "\(state)")
        }
    }

    @MainActor
    func testUnprotectAsksFirst() {
        let dialog = HomeDialogs.unprotect(texts)
        XCTAssertEqual(dialog.title, texts.dialogUnprotectTitle)
        XCTAssertEqual(dialog.yes, .secondary, "«да» — опасная «Снять защиту»")

        let layers = PgLayers()
        var dropped = 0
        layers.confirm(dialog) { dropped += 1 }
        layers.answer(.primary)
        XCTAssertEqual(dropped, 0, "«Оставить защиту»")
        layers.confirm(dialog) { dropped += 1 }
        layers.answer(.secondary)
        XCTAssertEqual(dropped, 1)
    }

    @MainActor
    func testHomeDrawsInEveryStateAndLanguage() throws {
        // Без серверов, серверы без выбора, выбранный сервер.
        let lists: [(servers: [ServerItem], selected: String?)] = [([], nil), (servers, nil), (servers, "a")]
        let ips = [WebIp(), WebIp(exit: "203.0.113.24", direct: "198.51.100.7", checkedAt: Date()), WebIp(exit: "198.51.100.7", direct: "198.51.100.7")]
        for language in PgTexts.Language.allCases {
            for state in TunnelState.allCases {
                for list in lists {
                    for (index, ip) in ips.enumerated() {
                        let status = TunnelStatus(
                            state: state, serverId: list.selected, since: Date(), error: state == .error ? TunnelErrors.serverNotResponding : nil, attempt: 2
                        )
                        let home = HomeState(status: status, servers: list.servers, selectedId: list.selected, measuring: index == 1)
                        let facts = HomeFacts(
                            blocked: state == .error && index == 1, uplink: [Uplink.wifi, .cellular, .offline][index], ip: ip,
                            speed: index == 1 ? TunnelSpeed(down: 18.9, up: 4) : nil
                        )
                        let view = HomeView(texts: PgTexts(language), home: home, facts: facts, version: "0.1.0").frame(width: 393, height: 852)
                        let image = try XCTUnwrap(ImageRenderer(content: view).uiImage, "\(language) \(state) \(list.servers.count) \(index)")
                        XCTAssertEqual(image.size.width, 393, accuracy: 1)
                    }
                }
            }
        }
    }

    @MainActor
    func testIpCardAndQuickMenuDrawOverTheWindow() throws {
        let home = HomeState(status: TunnelStatus(state: .connected, serverId: "a"), servers: servers, selectedId: "a")
        let ip = WebIp(exit: "203.0.113.24", direct: "198.51.100.7")
        for masked in [false, true] {
            let card = WebIpCard(texts: texts, home: home, facts: HomeFacts(ip: ip), masked: masked).frame(width: 393)
            XCTAssertNotNil(ImageRenderer(content: card).uiImage)
        }

        // Меню быстрой смены у своей кнопки поверх карточки и подсказка «IP скопирован» сверху.
        let layers = PgLayers()
        let anchor = UUID()
        let window = ZStack(alignment: .top) {
            PgBackground()
            VStack(alignment: .trailing, spacing: 0) {
                WebIpCard(texts: texts, home: home, facts: HomeFacts(ip: ip))
                Text(texts.serverSwitch).pgMenuAnchor(anchor)
            }
            .padding(.top, 120)
        }
        .pgLayers(layers)
        .frame(width: 393, height: 852)
        layers.openMenu(QuickMenu.make(anchor: anchor, home: home, texts: texts, onSelect: { _ in }, onAll: {}))
        layers.show(texts.ipCopied, for: 30)
        XCTAssertNotNil(ImageRenderer(content: window).uiImage)
        let menu = try XCTUnwrap(layers.menu)
        XCTAssertNotNil(ImageRenderer(content: PgMenuView(menu: menu) { _ in }.fixedSize()).uiImage)
        layers.hideToast()
    }
}

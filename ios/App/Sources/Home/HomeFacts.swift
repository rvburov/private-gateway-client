import Foundation
import PrivateGatewayCore
import PrivateGatewayTheme

/// Внешний IP для главной — из замера инспектора (Cloudflare trace, тот же, что «Измерить» на «Проверке»): адрес выхода,
/// измеренный под защитой, и реальный — замер без защиты в этой же сети (другая сеть — замер сбрасывается). Сведения об
/// адресе (город, автономная система) — только если о нем спросили кнопкой «Кто провайдер»: RIPEstat сам не спрашиваем.
struct WebIp: Equatable {
    /// Адрес, который видят сайты под защитой; nil — еще не измерен или источник не ответил.
    var exit: String?
    /// Реальный адрес: замер без защиты в этой сети; nil — не мерили.
    var direct: String?
    /// Что RIPEstat знает об адресе — его спросили на «Проверке».
    var info: AddressInfo?
    /// Идет замер.
    var checking = false
    /// Когда последний раз мерили адрес выхода под защитой.
    var checkedAt: Date?
}

/// Через какую сеть iPhone выходит в интернет (под VPN — та, поверх которой идет туннель).
enum Uplink: Equatable, Sendable {
    /// Выхода в интернет нет.
    case offline
    case wifi
    case cellular
    case ethernet
    /// Другое: iOS не назвала вид сети.
    case other
}

/// Сеть, в которой мерили реальный IP, — вид и интерфейс («wifi-en0»): в другой сети и реальный IP другой, прежний замер не
/// годится ни для карточки IP, ни для проверки утечки (как у службы десктопа). Имя Wi-Fi iOS не дает, поэтому смену одной
/// сети Wi-Fi на другую на том же интерфейсе так не видно.
enum NetworkKey {
    /// Ключ после новости от iOS и сменилась ли сеть. Без выхода в интернет сеть не сменилась — пропала на время; под VPN
    /// iOS может не назвать интерфейс под туннелем — тот же вид без имени считается той же сетью.
    static func next(_ previous: String?, kind: Uplink, interface: String?) -> (key: String?, changed: Bool) {
        guard kind != .offline else {
            return (previous, false)
        }

        let prefix = "\(kind)-"
        if interface == nil, let previous, previous.hasPrefix(prefix) {
            return (previous, false)
        }

        let key = prefix + (interface ?? "")
        return (key, previous != nil && previous != key)
    }
}

/// Что главная знает сверх состояния туннеля: «Блокировка», сеть, внешний IP и скорость туннеля.
struct HomeFacts: Equatable {
    /// Расширение сдалось (сервер не отвечает), а туннель оставлен: блок при обрыве держит трафик закрытым — «Блокировка».
    var blocked = false
    /// Сеть телефона; nil — iOS еще не сказала.
    var uplink: Uplink?
    var ip = WebIp()
    /// Скорость по счетчикам интерфейса туннеля; nil — счетчиков нет.
    var speed: TunnelSpeed?
    /// Пауза для входа в Wi-Fi: до какого времени (решение 557); nil — паузы нет.
    var paused: Date?

    /// «Утечка IP»: защита включена, а замер через туннель увидел реальный адрес — тот же, что без защиты в этой сети (правило
    /// `HasExitIpLeak` десктопа).
    func leak(_ state: TunnelState) -> Bool {
        state == .connected && ip.exit != nil && ip.exit == ip.direct
    }
}

/// Строка сети под состоянием (network.* десктопа): значок и вид сети. Имя Wi-Fi iOS отдает только с отдельным правом
/// приложения (Access Wi-Fi Information) — его нет, поэтому только вид.
enum NetworkLine {
    static func of(_ uplink: Uplink?, _ texts: PgTexts) -> (icon: String, text: String)? {
        switch uplink {
        case .offline?: return (PgIcons.netNone, texts.networkNone)
        case .wifi?: return (PgIcons.netWiFi, texts.networkWifiNoName)
        case .cellular?: return (PgIcons.netMobile, texts.networkMobile)
        case .ethernet?: return (PgIcons.netEthernet, texts.networkEthernet)
        case .other?, nil: return nil
        }
    }
}

/// Строка под плитками: текст и кнопка справа — правила `CheckedText` и `HasCheckAction` десктопа. «Проверить» — замер
/// внешнего IP на месте (под защитой он и есть проверка утечки), «Повторить» — подключиться снова, «Вход в Wi-Fi» в
/// «Подключении» и «Переподключении» — вопрос о паузе для входа в сеть, на паузе — отсчет и «Вернуть защиту» (решение 557).
struct CheckLine: Equatable {
    enum Action: Equatable {
        case check
        case retry
        case captive
        case resume
    }

    let text: String
    let action: Action?

    /// «Проверено только что» держится 10 с, дальше — «Проверено в 14:05».
    static let nowFor: TimeInterval = 10

    /// - Parameters:
    ///   - captive: предлагать «Вход в Wi-Fi» в «Подключении» и «Переподключении» (главная — да).
    ///   - paused: пауза для входа в Wi-Fi до этого времени — в «Выключено» вместо «Защита выключена».
    static func of(
        _ state: TunnelState, blocked: Bool, ip: WebIp, now: Date, _ texts: PgTexts, captive: Bool = false, paused: Date? = nil
    ) -> CheckLine {
        if state == .off, let paused {
            return CheckLine(text: texts.pauseBanner + " · " + CaptivePause.clock(CaptivePause.left(until: paused, now: now)), action: .resume)
        }

        if captive, state == .connecting || state == .reconnecting {
            return CheckLine(text: state == .connecting ? texts.checkedConnecting : texts.checkedReconnecting, action: .captive)
        }

        switch state {
        case .connected:
            if ip.checking {
                return CheckLine(text: texts.checkedRunning, action: nil)
            }

            guard let at = ip.checkedAt else {
                return CheckLine(text: texts.checkedConnecting, action: .check)
            }

            if now.timeIntervalSince(at) < nowFor {
                return CheckLine(text: texts.checkedNow, action: .check)
            }

            let format = DateFormatter()
            format.locale = Locale(identifier: "en_US_POSIX")
            format.dateFormat = "HH:mm"
            return CheckLine(text: texts.checkedAt(format.string(from: at)), action: .check)
        case .connecting, .stopping: return CheckLine(text: texts.checkedConnecting, action: nil)
        case .reconnecting: return CheckLine(text: texts.checkedReconnecting, action: nil)
        case .error: return CheckLine(text: blocked ? texts.checkedBlocked : texts.checkedError, action: .retry)
        case .off: return CheckLine(text: texts.checkedOff, action: nil)
        }
    }

    /// Цвет кнопки — тон состояния: «Повторить» в «Блокировке» — оранжевый, в «Ошибке» — красный, «Проверить» — синий.
    static func tone(_ state: TunnelState, blocked: Bool) -> PgRGBA {
        if state == .off {
            // «Вернуть защиту» на паузе — янтарный тон паузы.
            return PgState.Blocked.icon
        }

        guard state == .error else {
            return PgState.Connecting.icon
        }

        return blocked ? PgState.Blocked.icon : PgState.Error.icon
    }
}

/// Карточка «Сайты видят ваш IP» по состоянию — как `WebIp`, `WebIpMeta` и `RealIp` десктопа. Отличие телефона: «Ошибка» без
/// туннеля — трафик идет напрямую, поэтому там, как в «Выключено», реальный адрес и «виден сайтам», а не «трафик
/// остановлен».
struct WebIpLook: Equatable {
    static let placeholder = "—"

    /// Адрес на экране (звездочками, если скрыт) или «—».
    let ip: String
    /// Адрес для «Копировать»; nil — копировать нечего.
    let plain: String?
    let canCopy: Bool
    /// «город · AS…», «Ваш провайдер», «Нет доступа, пока туннель не готов»; nil — строки нет.
    let meta: String?
    /// Реальный адрес (звездочками, если скрыт); nil — не мерили, строки нет.
    let real: String?
    /// Реальный адрес виден сайтам (защиты нет или утечка), иначе — скрыт.
    let realVisible: Bool
    /// Защита включена, а реальный адрес не измерен — «Ваш реальный IP: не определен», а не пропавшая строка (как у десктопа).
    let realUnknown: Bool

    static func of(_ state: TunnelState, blocked: Bool, ip: WebIp, masked: Bool, _ texts: PgTexts, paused: Bool = false) -> WebIpLook {
        let leak = HomeFacts(blocked: blocked, ip: ip).leak(state)
        let shown: String?
        let meta: String?
        let visible: Bool
        switch state {
        case .connected:
            shown = ip.exit
            meta = ip.exit.flatMap { Self.info(ip.info, address: $0) }
            visible = leak
        case .off:
            shown = ip.direct
            meta = paused ? texts.ipMetaPaused : texts.ipMetaOff
            visible = true
        case .error where !blocked:
            shown = ip.direct
            meta = texts.ipMetaOff
            visible = true
        case .error:
            shown = nil
            meta = texts.ipMetaStopped
            visible = false
        case .connecting, .reconnecting, .stopping:
            shown = nil
            meta = texts.ipMetaWaiting
            visible = false
        }

        return WebIpLook(
            ip: shown.map { masked ? mask($0) : $0 } ?? placeholder, plain: shown, canCopy: shown != nil && !masked, meta: meta,
            real: ip.direct.map { masked ? mask($0) : $0 }, realVisible: visible, realUnknown: ip.direct == nil && state != .off
        )
    }

    /// Скрытый адрес — как у десктопа: цифры (и шестнадцатеричные знаки IPv6) — звездочки, точки и двоеточия остаются.
    static func mask(_ address: String) -> String {
        String(address.map { $0.isASCII && $0.isHexDigit ? "*" : $0 })
    }

    /// «Amsterdam · AS64500» — только о том же адресе и только то, что RIPEstat знает.
    private static func info(_ info: AddressInfo?, address: String) -> String? {
        guard let info, info.address == address, info.failure == nil else {
            return nil
        }

        let parts = [info.city, info.asn.map { "AS\($0)" }].compactMap { $0 }
        return parts.isEmpty ? nil : parts.joined(separator: " · ")
    }
}

/// Задержка сервера: быстрый — зеленым, медленный — серым, нет ответа — «нет» оранжевым, не мерили — «—». Сервер, за который
/// ядро телефона не берется (Hysteria2, TUIC), не мерили вовсе — тоже «—», а не «нет ответа»: он не молчал, его не спрашивали.
enum LatencyLook {
    static func of(_ latency: Latency?, _ texts: PgTexts) -> (text: String, color: PgRGBA, hint: String) {
        if latency?.failure == .unsupported {
            return ("—", PgColor.Text.muted, texts.androidCardCoreNone)
        }

        let ms = latency?.ms.map { "\($0) \(texts.unitMs)" } ?? ""
        switch LatencyKind.of(latency) {
        case .good: return (ms, PgColor.Status.Success.text, ms)
        case .slow: return (ms, PgColor.Text.muted, ms)
        case .noAnswer: return (texts.serversLatencyNone, PgColor.Status.Warning.text, texts.serversLatencyNoneHint)
        case .never: return ("—", PgColor.Text.muted, texts.androidServersLatencyNever)
        }
    }
}

/// Быстрая смена («Сменить ▾», MainPageViewModel.QuickServers десктопа): меню у кнопки — надзаголовок, сервер в работе
/// (отмечен) и два самых быстрых измеренных, у каждого протокол и задержка, под чертой — «Все серверы ›». Hysteria2 и TUIC
/// телефон не умеет — их не предлагаем.
enum QuickMenu {
    static func servers(_ home: HomeState) -> [ServerItem] {
        let inUse = home.inUse
        let usable = home.servers.filter { $0.id == inUse?.id || ServerFacts.coreName($0.protocol) != nil }
        return QuickSwitch.pick(usable, inUse: inUse)
    }

    /// Выбор сервера в работе — ничего, другого — `onSelect` (под защитой — сразу переподключение), последняя строка — `onAll`.
    static func make(
        anchor: UUID, home: HomeState, texts: PgTexts, onSelect: @escaping (String) -> Void, onAll: @escaping () -> Void
    ) -> PgMenu {
        let inUse = home.inUse?.id
        let picked = Self.servers(home)
        let items = picked.map { server -> PgMenuItem in
            let latency = LatencyLook.of(server.latency, texts)
            return PgMenuItem(
                title: server.displayName, current: server.id == inUse, badge: server.countryCode, caption: server.summary, trailing: latency.text,
                trailingColor: latency.color
            )
        }
        let ids = picked.map(\.id)
        return PgMenu(anchor: anchor, items: items + [PgMenuItem(title: texts.serverAll, link: true)], onPick: { index in
            guard index < ids.count else {
                onAll()
                return
            }

            if ids[index] != inUse {
                onSelect(ids[index])
            }
        }, title: texts.serverQuick)
    }

    /// Строка сервера в карточке IP: «NL · Нидерланды 01»; кода страны нет — одно название.
    static func title(_ server: ServerItem) -> String {
        server.countryCode.isEmpty ? server.displayName : server.countryCode + " · " + server.displayName
    }
}

/// «IP скопирован»: адрес — в буфер, подсказка сверху на 1,8 с. Скрытый или не измеренный адрес не копируется.
enum IpCopy {
    @MainActor
    static func copy(_ look: WebIpLook, layers: PgLayers?, texts: PgTexts, pasteboard: (String) -> Void) {
        guard look.canCopy, let plain = look.plain else {
            return
        }

        pasteboard(plain)
        layers?.show(texts.ipCopied, for: PgToastDuration.action)
    }
}

/// Карточка «Пинг · Загрузка · Отдача» — как `PingText`, `DownText` и `UpText` десктопа: только в «Защищено». Пинг —
/// последний замер задержки сервера через ядро (без защиты его и меряют, решения 430 и 468), скорость — по счетчикам
/// интерфейса туннеля; чего нет — «—».
struct SpeedLook: Equatable {
    let ping: String
    let down: String
    let up: String

    static func of(_ state: TunnelState, home: HomeState, speed: TunnelSpeed?, _ texts: PgTexts) -> SpeedLook {
        guard state == .connected else {
            return SpeedLook(ping: WebIpLook.placeholder, down: WebIpLook.placeholder, up: WebIpLook.placeholder)
        }

        let ping = home.inUse?.latency?.ms.map(String.init) ?? WebIpLook.placeholder
        guard let speed else {
            return SpeedLook(ping: ping, down: WebIpLook.placeholder, up: WebIpLook.placeholder)
        }

        let format = NumberFormatter()
        format.locale = Locale(identifier: texts.language.rawValue)
        format.numberStyle = .decimal
        format.minimumFractionDigits = 1
        format.maximumFractionDigits = 1
        let text = { (value: Double) in format.string(from: NSNumber(value: value)) ?? WebIpLook.placeholder }
        return SpeedLook(ping: ping, down: text(speed.down), up: text(speed.up))
    }
}

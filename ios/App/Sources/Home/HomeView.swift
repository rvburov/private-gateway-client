import PrivateGatewayCore
import PrivateGatewayTheme
import SwiftUI
import UIKit

/// Главная (доски 01, 09 и 25): шапка, карточка состояния с четырьмя плитками проверок, карточка «Сайты видят ваш IP» со
/// строкой сервера, карточка «Пинг · Загрузка · Отдача»; внизу — кнопка-щит островка. Показывается только измеренное:
/// чего нет — «—».
struct HomeView: View {
    let texts: PgTexts
    var home = HomeState()
    var killSwitch = true
    /// «Блокировка», сеть, внешний IP и скорость туннеля.
    var facts = HomeFacts()
    var version = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? ""
    var onAdd: () -> Void = {}
    var onServers: () -> Void = {}
    var onSelect: (String) -> Void = { _ in }
    var onRefresh: () -> Void = {}
    /// «Пинг» — подробности на «Проверке».
    var onCheck: () -> Void = {}
    /// «Проверить» — замер внешнего IP на месте.
    var onCheckNow: () -> Void = {}
    /// «Повторить» в «Ошибке» и «Блокировке» — подключиться снова.
    var onRetry: () -> Void = {}

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: PgSpace.m) {
                header
                StatusCard(texts: texts, home: home, killSwitch: killSwitch, facts: facts, onCheckNow: onCheckNow, onRetry: onRetry)
                WebIpCard(texts: texts, home: home, facts: facts, onAdd: onAdd, onServers: onServers, onSelect: onSelect, onRefresh: onRefresh)
                SpeedCard(texts: texts, look: SpeedLook.of(home.status.state, home: home, speed: facts.speed, texts), onPing: onCheck)
            }
            .padding(.horizontal, PgSpace.windowSideMobile)
            .padding(.bottom, PgLayout.scrollBottomPadding)
        }
        .accessibilityIdentifier("home")
    }

    /// Шапка (MainPage десктопа): знак — щит с отметкой, название, версия полностью, «+».
    private var header: some View {
        HStack(spacing: 0) {
            ZStack {
                PgIcon(data: PgIcons.shield, color: Color(PgColor.Accent.base), size: 28)
                PgIcon(data: PgIcons.markOk, color: Color(PgColor.Accent.base), size: 28)
            }
            .accessibilityHidden(true)

            Text(texts.appTitle)
                .pgText(PgType.brand)
                .foregroundStyle(Color(PgColor.Text.primary))
                .lineLimit(1)
                .padding(.leading, PgSpace.m)
                .padding(.trailing, PgSpace.s)
            if !version.isEmpty {
                Text(version)
                    .pgText(PgTextStyle(size: PgType.overline.size, weight: 600))
                    .foregroundStyle(Color(PgColor.Accent.text))
                    .lineLimit(1)
                    .padding(.horizontal, PgSpace.xs)
                    .padding(.vertical, 2)
                    .background(RoundedRectangle(cornerRadius: PgRadius.chip, style: .continuous).fill(Color(PgColor.Accent.tint)))
                    .accessibilityIdentifier("version")
            }

            Spacer(minLength: 0)
            Button(action: onAdd) {
                PgIcon(data: PgIcons.plus, size: 22, lineWidth: 2)
                    .frame(width: PgLayout.touchTarget, height: PgLayout.touchTarget)
                    .contentShape(Rectangle())
            }
            .buttonStyle(PgPressStyle())
            .accessibilityLabel(texts.headerAdd)
            .accessibilityIdentifier("add")
        }
        .frame(minHeight: 60)
    }
}

/// Карточка состояния (MainPage десктопа): плитка щита с отметкой, заголовок, подзаголовок и сеть, время в сети; четыре
/// плитки проверок — нажатие выделяет плитку и открывает под ними пояснение; строка «Проверено …» и кнопка по состоянию.
struct StatusCard: View {
    let texts: PgTexts
    let home: HomeState
    let killSwitch: Bool
    let facts: HomeFacts
    let onCheckNow: () -> Void
    let onRetry: () -> Void

    /// Выбранная плитка — ее пояснение под плитками; повторное нажатие закрывает.
    @State private var selected: Int?

    init(
        texts: PgTexts, home: HomeState, killSwitch: Bool, facts: HomeFacts = HomeFacts(), selected: Int? = nil,
        onCheckNow: @escaping () -> Void = {}, onRetry: @escaping () -> Void = {}
    ) {
        self.texts = texts
        self.home = home
        self.killSwitch = killSwitch
        self.facts = facts
        self.onCheckNow = onCheckNow
        self.onRetry = onRetry
        _selected = State(initialValue: selected)
    }

    private var status: TunnelStatus { home.status }

    var body: some View {
        let leak = facts.leak(status.state)
        let look = StateLook.of(status.state, texts, blocked: facts.blocked, leak: leak)
        let chips = ProtectionChips.of(status.state, killSwitch: killSwitch, texts, blocked: facts.blocked, leak: leak)
        VStack(alignment: .leading, spacing: PgSpace.l) {
            HStack(alignment: .top, spacing: 0) {
                ZStack {
                    RoundedRectangle(cornerRadius: PgRadius.button, style: .continuous).fill(Color(look.tint))
                    PgIcon(data: PgIcons.shield, color: Color(look.icon), size: 26, lineWidth: 1.9)
                    PgIcon(data: look.mark, color: Color(look.icon), size: 26, lineWidth: 1.9)
                }
                .frame(width: 46, height: 46)
                .accessibilityHidden(true)

                VStack(alignment: .leading, spacing: 2) {
                    Text(look.title).pgText(PgType.status).foregroundStyle(Color(look.icon)).accessibilityIdentifier("status.title")
                    Text(StatusText.subtitle(status, blocked: facts.blocked, leak: leak, server: home.inUse, texts))
                        .pgText(PgType.small)
                        .foregroundStyle(Color(PgColor.Text.secondary))
                        .lineLimit(1)
                        .accessibilityIdentifier("status.sub")
                    if let network = NetworkLine.of(facts.uplink, texts) {
                        HStack(spacing: 5) {
                            PgIcon(data: network.icon, color: Color(PgColor.Text.muted), size: 13, lineWidth: 2)
                            Text(network.text).pgText(PgType.caption).foregroundStyle(Color(PgColor.Text.muted)).lineLimit(1)
                        }
                        .padding(.top, 1)
                        .accessibilityElement(children: .combine)
                        .accessibilityIdentifier("status.network")
                    }
                }
                .padding(.leading, PgSpace.l)
                .padding(.trailing, PgSpace.s)

                Spacer(minLength: 0)
                if status.state == .connected, let since = status.since {
                    VStack(alignment: .trailing, spacing: 2) {
                        Text(texts.statusOnline).pgText(PgType.caption).foregroundStyle(Color(PgColor.Text.muted))
                        TimelineView(.periodic(from: since, by: 1)) { context in
                            Text(Self.online(since, now: context.date))
                                .pgText(PgType.mono.with(weight: 600))
                                .foregroundStyle(Color(PgColor.Text.primary))
                        }
                    }
                }
            }

            LazyVGrid(columns: [GridItem(.flexible(), spacing: PgSpace.xs), GridItem(.flexible(), spacing: PgSpace.xs)], spacing: PgSpace.xs) {
                ForEach(chips.indices, id: \.self) { index in
                    ChipView(chip: chips[index], selected: selected == index) {
                        selected = Self.toggle(selected, index)
                    }
                    .accessibilityIdentifier("chip.\(index)")
                }
            }

            if let chip = Self.info(chips, selected: selected) {
                VStack(alignment: .leading, spacing: 2) {
                    Text(chip.title).pgText(PgType.small.with(weight: PgType.bodyStrong.weight)).foregroundStyle(Color(PgColor.Text.primary))
                    Text(chip.text)
                        .pgText(PgType.caption)
                        .foregroundStyle(Color(PgColor.Text.secondary))
                        .fixedSize(horizontal: false, vertical: true)
                }
                .padding(.horizontal, PgSpace.l)
                .padding(.vertical, PgSpace.m)
                .frame(maxWidth: .infinity, alignment: .leading)
                .background(RoundedRectangle(cornerRadius: PgRadius.button, style: .continuous).fill(Color(PgColor.Accent.tint)))
                .accessibilityElement(children: .combine)
                .accessibilityIdentifier("chip.info")
            }

            TimelineView(.periodic(from: Date(), by: 5)) { context in
                checkRow(CheckLine.of(status.state, blocked: facts.blocked, ip: facts.ip, now: context.date, texts))
            }
        }
        .padding(.top, 14)
        .padding(.leading, PgSpace.card)
        .padding(.trailing, 14)
        .padding(.bottom, PgSpace.l)
        .pgCard(padding: 0)
    }

    /// «Проверено только что» и «Проверить», «Подробности — в «Проверке»» и «Повторить»; без разделителя над строкой.
    private func checkRow(_ line: CheckLine) -> some View {
        HStack(spacing: PgSpace.s) {
            Text(line.text)
                .pgText(PgType.caption)
                .foregroundStyle(Color(PgColor.Text.muted))
                .frame(maxWidth: .infinity, alignment: .leading)
                .accessibilityIdentifier("checked")
            if let action = line.action {
                Button(action: action == .check ? onCheckNow : onRetry) {
                    Text(action == .check ? texts.actionCheck : texts.actionRetry)
                        .pgText(PgType.small.with(weight: PgType.bodyStrong.weight))
                        .foregroundStyle(Color(CheckLine.tone(status.state, blocked: facts.blocked)))
                        .padding(.vertical, PgSpace.xxs)
                        .contentShape(Rectangle())
                }
                .buttonStyle(PgPressStyle())
                .accessibilityIdentifier(action == .check ? "check" : "retry")
            }
        }
    }

    /// Нажатие на плитку: другая — выбрать ее, та же — закрыть пояснение.
    nonisolated static func toggle(_ selected: Int?, _ index: Int) -> Int? {
        selected == index ? nil : index
    }

    /// Пояснение под плитками: выбранная плитка, если она есть в этом состоянии.
    nonisolated static func info(_ chips: [ProtectionChip], selected: Int?) -> ProtectionChip? {
        guard let selected, chips.indices.contains(selected) else {
            return nil
        }

        return chips[selected]
    }

    /// «02:14:09» — сколько защита в сети.
    nonisolated static func online(_ since: Date, now: Date) -> String {
        let seconds = max(0, Int(now.timeIntervalSince(since)))
        return String(format: "%02d:%02d:%02d", seconds / 3600, seconds / 60 % 60, seconds % 60)
    }
}

/// Подзаголовок состояния — `StatusSubtitle` десктопа.
enum StatusText {
    static func subtitle(_ status: TunnelStatus, blocked: Bool, leak: Bool, server: ServerItem?, _ texts: PgTexts) -> String {
        switch status.state {
        case .connected:
            if leak {
                return texts.statusLeakSub
            }

            return server?.encrypted == false ? texts.statusConnectedSubPlain : texts.statusConnectedSub
        case .connecting, .stopping: return texts.statusConnectingSub
        case .reconnecting: return texts.statusReconnectingSub(status.attempt)
        case .error:
            let code = status.error ?? TunnelErrors.coreFailed
            if blocked {
                // Сервер не отвечает — «Сервер не отвечает», иначе код: трафик заблокирован, утечки нет.
                return code == TunnelErrors.serverNotResponding ? texts.statusBlockedSub : texts.statusErrorSub(code)
            }

            return texts.statusErrorSubDirect(code)
        case .off: return texts.statusOffSub
        }
    }
}

/// Плитка проверки (.btn-chip, Button.chip десктопа): кружок с отметкой цвета тона, заголовок small полужирным, значение
/// overline цветом тона; выбранная — подложка темнее и рамка цвета тона.
struct ChipView: View {
    let chip: ProtectionChip
    var selected = false
    var action: () -> Void = {}

    var body: some View {
        Button(action: action) {
            HStack(spacing: PgSpace.s) {
                ZStack {
                    Circle().fill(Color(chip.look.base))
                    PgIcon(data: chip.look.mark, color: Color(PgColor.Surface.solid), size: 14, lineWidth: 2.6)
                }
                .frame(width: 22, height: 22)
                .accessibilityHidden(true)

                VStack(alignment: .leading, spacing: 0) {
                    Text(chip.title)
                        .pgText(PgType.small.with(weight: PgType.bodyStrong.weight))
                        .foregroundStyle(Color(PgColor.Text.primary))
                        .lineLimit(1)
                    Text(chip.value)
                        .pgText(PgTextStyle(size: PgType.overline.size, weight: PgType.caption.weight))
                        .foregroundStyle(Color(chip.look.text))
                        .lineLimit(1)
                }

                Spacer(minLength: 0)
            }
            .padding(.horizontal, PgSpace.m)
            .frame(maxWidth: .infinity, minHeight: 46, alignment: .leading)
            .background(RoundedRectangle(cornerRadius: PgRadius.button, style: .continuous).fill(Color(selected ? chip.look.hover : chip.look.tint)))
            .overlay(
                RoundedRectangle(cornerRadius: PgRadius.button, style: .continuous)
                    .strokeBorder(selected ? Color(chip.look.base) : Color.clear, lineWidth: 1.5)
            )
            .contentShape(Rectangle())
        }
        .buttonStyle(PgPressStyle())
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(selected ? [.isButton, .isSelected] : .isButton)
    }
}

/// «Сайты видят ваш IP» (MainPage десктопа): адрес моноширинным крупно, «Копировать» и глаз (скрытый — звездочками), строка
/// о сети адреса, «Ваш реальный IP: … · скрыт», под чертой — строка сервера.
struct WebIpCard: View {
    let texts: PgTexts
    let home: HomeState
    let facts: HomeFacts
    let onAdd: () -> Void
    let onServers: () -> Void
    let onSelect: (String) -> Void
    let onRefresh: () -> Void

    @State private var masked: Bool
    @Environment(\.pgLayers) private var layers

    init(
        texts: PgTexts, home: HomeState, facts: HomeFacts, masked: Bool = false, onAdd: @escaping () -> Void = {},
        onServers: @escaping () -> Void = {}, onSelect: @escaping (String) -> Void = { _ in }, onRefresh: @escaping () -> Void = {}
    ) {
        self.texts = texts
        self.home = home
        self.facts = facts
        self.onAdd = onAdd
        self.onServers = onServers
        self.onSelect = onSelect
        self.onRefresh = onRefresh
        _masked = State(initialValue: masked)
    }

    var body: some View {
        let look = WebIpLook.of(home.status.state, blocked: facts.blocked, ip: facts.ip, masked: masked, texts)
        VStack(alignment: .leading, spacing: 0) {
            VStack(alignment: .leading, spacing: 3) {
                Overline(text: texts.ipTitle)
                HStack(spacing: 0) {
                    Text(look.ip)
                        .pgText(PgType.monoLarge)
                        .foregroundStyle(Color(PgColor.Text.primary))
                        .lineLimit(1)
                        .minimumScaleFactor(0.6)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .accessibilityIdentifier("ip.address")
                    iconButton(PgIcons.copy, label: texts.ipCopy, enabled: look.canCopy, identifier: "ip.copy") {
                        IpCopy.copy(look, layers: layers, texts: texts) { UIPasteboard.general.string = $0 }
                    }
                    iconButton(masked ? PgIcons.eyeOff : PgIcons.eye, label: masked ? texts.ipShow : texts.ipHide, identifier: "ip.mask") {
                        masked.toggle()
                    }
                }

                if let meta = look.meta {
                    Text(meta).pgText(PgType.small).foregroundStyle(Color(PgColor.Text.secondary)).lineLimit(1).accessibilityIdentifier("ip.meta")
                }

                if let real = look.real {
                    HStack(spacing: PgSpace.xxs) {
                        Text(texts.ipReal)
                        Text(real).pgText(PgType.mono.with(weight: PgType.small.weight))
                        Text("· " + (look.realVisible ? texts.ipRealVisible : texts.ipRealHidden))
                    }
                    .pgText(PgType.small)
                    .foregroundStyle(Color(PgColor.Text.secondary))
                    .lineLimit(1)
                    .minimumScaleFactor(0.8)
                    .accessibilityElement(children: .combine)
                    .accessibilityIdentifier("ip.real")
                }
            }
            .padding(.top, 14)
            .padding(.leading, PgSpace.card)
            .padding(.trailing, PgSpace.s)
            .padding(.bottom, PgSpace.l)

            PgDivider().padding(.horizontal, PgSpace.card)
            HomeServerRow(texts: texts, home: home, onAdd: onAdd, onServers: onServers, onSelect: onSelect, onRefresh: onRefresh)
        }
        .pgCard(padding: 0)
        .accessibilityIdentifier("ip")
    }

    /// Кнопка-значок (.btn-icon): 44 × 44, значок 18 цветом текста; недоступная — прозрачность 0,35.
    private func iconButton(_ icon: String, label: String, enabled: Bool = true, identifier: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            PgIcon(data: icon, color: Color(PgColor.Text.primary), size: 18, lineWidth: 2)
                .frame(width: PgLayout.touchTarget, height: PgLayout.touchTarget)
                .contentShape(Rectangle())
        }
        .buttonStyle(PgPressStyle())
        .disabled(!enabled)
        .opacity(enabled ? 1 : 0.35)
        .accessibilityLabel(label)
        .accessibilityIdentifier(identifier)
    }
}

/// Строка сервера в карточке IP (MainPage десктопа): слева «Обновить серверы» — подложка accent.tint, при обновлении крутится
/// только стрелка, кнопка не гаснет; посередине «NL · Нидерланды 01» и «VLESS · REALITY · порт 443»; справа «Сменить ▾» —
/// меню быстрой смены у кнопки. Серверов нет — строка ведет к добавлению.
struct HomeServerRow: View {
    let texts: PgTexts
    let home: HomeState
    let onAdd: () -> Void
    let onServers: () -> Void
    let onSelect: (String) -> Void
    let onRefresh: () -> Void

    @Environment(\.pgLayers) private var layers
    @State private var anchor = UUID()

    var body: some View {
        HStack(spacing: 0) {
            Button(action: onRefresh) {
                refreshIcon
                    .frame(width: PgLayout.touchTarget, height: PgLayout.touchTarget)
                    .background(RoundedRectangle(cornerRadius: PgRadius.button, style: .continuous).fill(Color(PgColor.Accent.tint)))
                    .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .accessibilityLabel(texts.serverRefresh)
            .accessibilityIdentifier("server.refresh")

            if home.servers.isEmpty {
                Button(action: onAdd) {
                    labels.contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .accessibilityIdentifier("server.add")
            } else {
                labels
                Button {
                    Self.open(in: layers, anchor: anchor, home: home, texts: texts, onSelect: onSelect, onAll: onServers)
                } label: {
                    HStack(spacing: PgSpace.xs) {
                        Text(texts.serverSwitch).pgText(PgType.small.with(weight: 500)).foregroundStyle(Color(PgColor.Text.primary)).lineLimit(1)
                        PgIcon(data: PgIcons.chevronDown, color: Color(PgColor.Accent.text), size: 14, lineWidth: 2.6)
                    }
                    .padding(.leading, PgSpace.l)
                    .padding(.trailing, PgSpace.m)
                    .frame(height: 36)
                    .contentShape(Rectangle())
                }
                .buttonStyle(PgSelectStyle())
                .pgMenuAnchor(anchor)
                .accessibilityLabel(texts.serverSwitch)
                .accessibilityHint(texts.serverQuick)
                .accessibilityIdentifier("server.switch")
            }
        }
        .padding(.top, PgSpace.s)
        .padding(.horizontal, PgSpace.m)
        .padding(.bottom, PgSpace.m)
    }

    /// «Сменить ▾»: меню быстрой смены у кнопки (`QuickMenu`); слоев окна нет (блок нарисован вне корня) — ничего. Слои — на
    /// главном потоке, как и тест, который зовет это напрямую.
    @MainActor
    static func open(
        in layers: PgLayers?, anchor: UUID, home: HomeState, texts: PgTexts, onSelect: @escaping (String) -> Void, onAll: @escaping () -> Void
    ) {
        layers?.openMenu(QuickMenu.make(anchor: anchor, home: home, texts: texts, onSelect: onSelect, onAll: onAll))
    }

    /// Название сервера и протокол с портом; сервера нет — «Сервер не выбран» и что сделать.
    private var labels: some View {
        VStack(alignment: .leading, spacing: 2) {
            if let server = home.inUse {
                Text(QuickMenu.title(server)).pgText(PgType.title).foregroundStyle(Color(PgColor.Text.primary)).lineLimit(1)
                Text(server.summary + " · " + texts.serverPort(server.port)).pgText(PgType.small).foregroundStyle(Color(PgColor.Text.secondary)).lineLimit(1)
            } else {
                Text(texts.serverNone).pgText(PgType.title).foregroundStyle(Color(PgColor.Text.primary)).lineLimit(1)
                Text(home.servers.isEmpty ? texts.serverAddFirst : texts.androidServerChoose)
                    .pgText(PgType.small)
                    .foregroundStyle(Color(PgColor.Text.secondary))
                    .lineLimit(1)
            }
        }
        .padding(.leading, PgSpace.l)
        .padding(.trailing, PgSpace.s)
        .frame(maxWidth: .infinity, alignment: .leading)
        .accessibilityElement(children: .combine)
        .accessibilityIdentifier("server.name")
    }

    /// Стрелка «Обновить»: идет замер — крутится (оборот за 0,9 с), сама кнопка не меняется.
    @ViewBuilder
    private var refreshIcon: some View {
        if home.measuring {
            TimelineView(.animation) { context in
                let turn = context.date.timeIntervalSinceReferenceDate.truncatingRemainder(dividingBy: 0.9) / 0.9
                PgIcon(data: PgIcons.refresh, color: Color(PgColor.Accent.text), size: 20, lineWidth: 2)
                    .rotationEffect(.degrees(turn * 360))
            }
        } else {
            PgIcon(data: PgIcons.refresh, color: Color(PgColor.Accent.text), size: 20, lineWidth: 2)
        }
    }
}

/// «Пинг · ↓ Загрузка · ↑ Отдача» (MainPage десктопа): три колонки через черту; «Пинг» зеленым и ведет на «Проверку».
struct SpeedCard: View {
    let texts: PgTexts
    let look: SpeedLook
    let onPing: () -> Void

    var body: some View {
        HStack(spacing: 0) {
            Button(action: onPing) {
                column(texts.speedPing, look.ping, texts.unitMs, color: PgColor.Status.Success.text, identifier: "speed.ping")
            }
            .buttonStyle(PgPressStyle())
            .accessibilityLabel(texts.speedPingOpen)
            .accessibilityValue(look.ping)
            divider
            column(texts.speedDown, look.down, texts.unitMbps, color: PgColor.Text.primary, identifier: "speed.down")
            divider
            column(texts.speedUp, look.up, texts.unitMbps, color: PgColor.Text.primary, identifier: "speed.up")
        }
        .fixedSize(horizontal: false, vertical: true)
        .padding(.vertical, PgSpace.m)
        .padding(.horizontal, PgSpace.xs)
        .pgCard(padding: 0)
        .accessibilityIdentifier("speed")
    }

    private var divider: some View {
        Rectangle()
            .fill(Color(PgColor.Line.divider))
            .frame(width: 1)
            .frame(maxHeight: .infinity)
            .accessibilityHidden(true)
    }

    private func column(_ title: String, _ value: String, _ unit: String, color: PgRGBA, identifier: String) -> some View {
        VStack(spacing: 2) {
            Text(title).pgText(PgType.caption).foregroundStyle(Color(PgColor.Text.muted)).lineLimit(1)
            Text(value)
                .pgText(PgTextStyle(size: PgType.title.size, weight: PgType.title.weight, mono: true))
                .foregroundStyle(Color(color))
                .lineLimit(1)
                .accessibilityIdentifier(identifier)
            Text(unit).pgText(PgType.caption).foregroundStyle(Color(PgColor.Text.muted)).lineLimit(1)
        }
        .frame(maxWidth: .infinity)
        .contentShape(Rectangle())
        .accessibilityElement(children: .combine)
    }
}

/// Значок страны: две буквы кода на подложке (цветные флаги шрифт рисует не везде); кода нет — щит.
struct CountryBadge: View {
    let code: String

    var body: some View {
        ZStack {
            RoundedRectangle(cornerRadius: PgRadius.input, style: .continuous).fill(Color(PgColor.Accent.tint))
            if code.isEmpty {
                PgIcon(data: PgIcons.shield, color: Color(PgColor.Accent.text), size: 18)
            } else {
                Text(code).pgText(PgType.bodyStrong).foregroundStyle(Color(PgColor.Accent.text))
            }
        }
        .frame(width: PgLayout.touchTarget - 4, height: PgLayout.touchTarget - 4)
        .accessibilityHidden(true)
    }
}

/// Задержка: быстрый — зеленым, медленный — серым, нет ответа — «нет» оранжевым, не мерили — «—» (`LatencyLook`).
struct LatencyText: View {
    let texts: PgTexts
    let latency: Latency?

    var body: some View {
        let look = LatencyLook.of(latency, texts)
        Text(look.text).pgText(PgType.mono).foregroundStyle(Color(look.color)).lineLimit(1).accessibilityLabel(look.hint)
    }
}

/// Кнопка-щит (layout.powerButton, Button.power десктопа): белый круг с кольцом цвета состояния внутри 80 пунктов, щит с
/// отметкой линией 1,9; при подключении по кольцу бежит дуга (оборот за 0,9 с). Нажатие — заливка state.*.fill и сжатие
/// (`PowerPressStyle`). Подпись состояния — под кнопкой в островке. В «Блокировке» нажатие снимает защиту (после вопроса).
struct PowerButton: View {
    let look: StateLook
    let texts: PgTexts
    let state: TunnelState
    var blocked = false
    let action: () -> Void

    private var on: Bool { state == .connected || state == .connecting || state == .reconnecting || (state == .error && blocked) }

    var body: some View {
        Button(action: action) {
            ZStack {
                Circle().strokeBorder(Color(look.ring), lineWidth: PgLayout.PowerButton.ringWidth)
                if state == .connecting || state == .reconnecting {
                    TimelineView(.animation) { context in
                        let turn = context.date.timeIntervalSinceReferenceDate.truncatingRemainder(dividingBy: 0.9) / 0.9
                        Circle()
                            .inset(by: PgLayout.PowerButton.ringWidth / 2)
                            .trim(from: 0, to: 100.0 / 360)
                            .stroke(Color(PgState.Connecting.spinner), style: StrokeStyle(lineWidth: PgLayout.PowerButton.ringWidth, lineCap: .round))
                            .rotationEffect(.degrees(turn * 360))
                    }
                }

                PgIcon(data: PgIcons.shield, color: Color(look.icon), size: PgLayout.PowerButton.iconSize, lineWidth: 1.9)
                PgIcon(data: look.mark, color: Color(look.icon), size: PgLayout.PowerButton.iconSize, lineWidth: 1.9)
            }
            .frame(width: PgLayout.PowerButton.size, height: PgLayout.PowerButton.size)
            .contentShape(Circle())
        }
        .buttonStyle(PowerPressStyle(look: look))
        .disabled(state == .stopping)
        .accessibilityLabel(look.power)
        .accessibilityHint(on ? texts.powerActionDisconnect : texts.powerActionConnect)
        .accessibilityIdentifier("power")
    }
}

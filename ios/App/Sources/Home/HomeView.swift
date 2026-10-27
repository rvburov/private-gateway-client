import PrivateGatewayCore
import PrivateGatewayTheme
import SwiftUI

/// Главная (доска 25): шапка, карточка состояния с четырьмя чипами защиты, сервер в работе, внизу — кнопка включения под
/// большой палец. Внешний IP и скорость — после замера: показывается только измеренное, без предполагаемых значений.
struct HomeView: View {
    let texts: PgTexts
    var home = HomeState()
    var killSwitch = true
    var version = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? ""
    var onAdd: () -> Void = {}
    var onServers: () -> Void = {}
    var onSelect: (String) -> Void = { _ in }
    var onRefresh: () -> Void = {}
    var onCheck: () -> Void = {}

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: PgSpace.l) {
                header
                StatusCard(texts: texts, home: home, killSwitch: killSwitch, onCheck: onCheck)
                ServerCard(texts: texts, home: home, onAdd: onAdd, onServers: onServers, onSelect: onSelect, onRefresh: onRefresh)
            }
            .padding(.horizontal, PgSpace.windowSideMobile)
            .padding(.bottom, PgLayout.scrollBottomPadding)
        }
        .accessibilityIdentifier("home")
    }

    private var header: some View {
        HStack(spacing: PgSpace.s) {
            PgIcon(data: PgIcons.shield, color: Color(PgColor.Accent.base), size: 26)
            Text(texts.appTitle).pgText(PgType.brand).foregroundStyle(Color(PgColor.Text.primary))
            if !version.isEmpty {
                // Версия без последней цифры, как у Android: «0.1».
                Text(version.split(separator: ".").prefix(2).joined(separator: "."))
                    .pgText(PgType.caption)
                    .foregroundStyle(Color(PgColor.Accent.text))
                    .padding(.horizontal, PgSpace.xs)
                    .padding(.vertical, 2)
                    .background(RoundedRectangle(cornerRadius: PgRadius.chip).fill(Color(PgColor.Accent.tint)))
            }

            Spacer()
            Button(action: onAdd) {
                PgIcon(data: PgIcons.plus, size: 24, lineWidth: 2)
                    .frame(width: PgLayout.touchTarget, height: PgLayout.touchTarget)
                    .contentShape(Circle())
            }
            .accessibilityLabel(texts.headerAdd)
            .accessibilityIdentifier("add")
        }
        .padding(.top, PgSpace.l)
    }
}

/// Сервер в работе (или выбранный): значок страны, название, протокол и порт. «Сменить ▾» раскрывает быструю смену (доска 25,
/// решение 431): сервер в работе и два самых быстрых измеренных, «Обновить» — замер задержки, «Все серверы ›».
struct ServerCard: View {
    let texts: PgTexts
    let home: HomeState
    let onAdd: () -> Void
    let onServers: () -> Void
    let onSelect: (String) -> Void
    let onRefresh: () -> Void

    @State private var quick = false

    var body: some View {
        VStack(alignment: .leading, spacing: PgSpace.s) {
            if let server = home.inUse {
                Button {
                    quick.toggle()
                } label: {
                    HStack(spacing: PgSpace.l) {
                        CountryBadge(code: server.countryCode)
                        VStack(alignment: .leading, spacing: 2) {
                            Text(server.displayName).pgText(PgType.bodyStrong).foregroundStyle(Color(PgColor.Text.primary)).lineLimit(1)
                            Text(server.summary + " · " + texts.serverPort(server.port))
                                .pgText(PgType.caption)
                                .foregroundStyle(Color(PgColor.Text.secondary))
                        }

                        Spacer(minLength: 0)
                        Text(texts.serverSwitch).pgText(PgType.bodyStrong).foregroundStyle(Color(PgColor.Accent.text))
                        PgIcon(data: PgIcons.chevronDown, color: Color(PgColor.Accent.text), size: 16)
                    }
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .accessibilityHint(texts.serverQuick)
                .accessibilityIdentifier("server.switch")

                if quick {
                    quickList(server)
                }
            } else {
                Overline(text: texts.serverNone)
                // Серверы есть, но ни один не выбран (выбранный удален): сам сервер не выбирается — ведем к списку.
                Button(action: home.servers.isEmpty ? onAdd : onServers) {
                    HStack {
                        Text(home.servers.isEmpty ? texts.serversNone : texts.androidServerChoose)
                            .pgText(PgType.small)
                            .foregroundStyle(Color(PgColor.Text.secondary))
                            .multilineTextAlignment(.leading)
                        Spacer()
                        if home.servers.isEmpty {
                            PgIcon(data: PgIcons.plus, color: Color(PgColor.Accent.base))
                        } else {
                            Text(texts.serverAll).pgText(PgType.bodyStrong).foregroundStyle(Color(PgColor.Accent.text))
                        }
                    }
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .accessibilityIdentifier(home.servers.isEmpty ? "server.add" : "server.choose")
            }
        }
        .pgCard()
    }

    /// Быстрая смена: сервер в работе и самые быстрые измеренные; нажатие — выбор (под защитой — сразу переподключение).
    @ViewBuilder
    private func quickList(_ inUse: ServerItem) -> some View {
        Divider().overlay(Color(PgColor.Line.divider))
        HStack {
            Overline(text: texts.serverQuick)
            Spacer()
            Button(action: onRefresh) {
                PgIcon(data: PgIcons.refresh, color: Color(home.measuring ? PgColor.Text.muted : PgColor.Accent.text), size: 20)
                    .frame(width: PgLayout.touchTarget, height: PgLayout.touchTarget)
                    .contentShape(Circle())
            }
            .disabled(home.measuring)
            .accessibilityLabel(texts.serverRefresh)
            .accessibilityIdentifier("quick.refresh")
        }

        ForEach(QuickSwitch.pick(home.servers, inUse: inUse), id: \.id) { server in
            Button {
                quick = false
                onSelect(server.id)
            } label: {
                HStack(spacing: PgSpace.m) {
                    CountryBadge(code: server.countryCode)
                    Text(server.displayName).pgText(PgType.body).foregroundStyle(Color(PgColor.Text.primary)).lineLimit(1)
                    Spacer(minLength: 0)
                    LatencyText(texts: texts, latency: server.latency)
                }
                .padding(.vertical, PgSpace.xs)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .accessibilityIdentifier("quick.\(server.id)")
        }

        Button(action: onServers) {
            Text(texts.serverAll).pgText(PgType.bodyStrong).foregroundStyle(Color(PgColor.Accent.text))
        }
        .buttonStyle(.plain)
        .accessibilityIdentifier("quick.all")
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

/// Задержка: быстрый — зеленым, медленный — серым, нет ответа — «нет» оранжевым, не мерили — «—».
struct LatencyText: View {
    let texts: PgTexts
    let latency: Latency?

    var body: some View {
        let look = self.look
        Text(look.text).pgText(PgType.mono).foregroundStyle(Color(look.color)).lineLimit(1).accessibilityLabel(look.hint)
    }

    private var look: (text: String, color: PgRGBA, hint: String) {
        let ms = latency?.ms.map { "\($0) \(texts.unitMs)" } ?? ""
        switch LatencyKind.of(latency) {
        case .good: return (ms, PgColor.Status.Success.text, ms)
        case .slow: return (ms, PgColor.Text.muted, ms)
        case .noAnswer: return (texts.serversLatencyNone, PgColor.Status.Warning.text, texts.serversLatencyNoneHint)
        case .never: return ("—", PgColor.Text.muted, texts.androidServersLatencyNever)
        }
    }
}

/// Карточка состояния: значок щита с отметкой, заголовок и подзаголовок, время в сети, четыре чипа и строка проверки.
struct StatusCard: View {
    let texts: PgTexts
    let home: HomeState
    let killSwitch: Bool
    var onCheck: () -> Void = {}

    private var status: TunnelStatus { home.status }

    var body: some View {
        let look = StateLook.of(status.state, texts)
        VStack(alignment: .leading, spacing: PgSpace.l) {
            HStack(spacing: PgSpace.l) {
                ZStack {
                    RoundedRectangle(cornerRadius: PgRadius.button, style: .continuous).fill(Color(look.tint))
                    PgIcon(data: PgIcons.shield, color: Color(look.icon), size: 26, lineWidth: 2)
                    PgIcon(data: look.mark, color: Color(look.icon), size: 26, lineWidth: 2.2)
                }
                .frame(width: 48, height: 48)
                .accessibilityHidden(true)

                VStack(alignment: .leading, spacing: 2) {
                    Text(look.title).pgText(PgType.status).foregroundStyle(Color(look.icon)).accessibilityIdentifier("status.title")
                    Text(subtitle).pgText(PgType.small).foregroundStyle(Color(PgColor.Text.secondary)).lineLimit(2)
                }

                Spacer(minLength: 0)
                if status.state == .connected, let since = status.since {
                    VStack(alignment: .trailing, spacing: 2) {
                        Text(texts.statusOnline).pgText(PgType.caption).foregroundStyle(Color(PgColor.Text.muted))
                        TimelineView(.periodic(from: since, by: 1)) { context in
                            Text(Self.online(since, now: context.date)).pgText(PgType.mono).foregroundStyle(Color(PgColor.Text.primary))
                        }
                    }
                }
            }

            let chips = ProtectionChips.of(status.state, killSwitch: killSwitch, texts)
            LazyVGrid(columns: [GridItem(.flexible(), spacing: PgSpace.s), GridItem(.flexible(), spacing: PgSpace.s)], spacing: PgSpace.s) {
                ForEach(chips.indices, id: \.self) { index in
                    ChipView(chip: chips[index])
                }
            }

            Divider().overlay(Color(PgColor.Line.divider))
            HStack {
                Text(note).pgText(PgType.caption).foregroundStyle(Color(PgColor.Text.muted))
                Spacer()
                Button(action: onCheck) {
                    Text(texts.actionCheck).pgText(PgType.bodyStrong).foregroundStyle(Color(PgColor.Accent.text))
                }
                .buttonStyle(.plain)
                .accessibilityIdentifier("check")
            }
        }
        .pgCard()
    }

    /// Под заголовком: шифруется ли трафик, к какому серверу подключаемся, код ошибки.
    private var subtitle: String {
        switch status.state {
        case .connected: return home.inUse?.encrypted == false ? texts.statusConnectedSubPlain : texts.statusConnectedSub
        case .connecting, .stopping: return home.inUse?.displayName ?? ""
        case .reconnecting: return texts.statusReconnectingSub(status.attempt)
        case .error: return texts.statusErrorSubDirect(status.error ?? "")
        case .off: return texts.statusOffSub
        }
    }

    /// Строка под чипами: что сделать, чтобы проверить защиту.
    private var note: String {
        switch status.state {
        case .off:
            if home.inUse != nil {
                return texts.checkedOff
            }

            return home.servers.isEmpty ? texts.serverAddFirst : texts.androidServerChoose
        case .connecting, .stopping: return texts.checkedConnecting
        case .reconnecting: return texts.checkedReconnecting
        case .error: return status.error.flatMap { texts.text(key: "error.\($0)") } ?? texts.checkedError
        case .connected: return ""
        }
    }

    /// «02:14:09» — сколько защита в сети.
    nonisolated static func online(_ since: Date, now: Date) -> String {
        let seconds = max(0, Int(now.timeIntervalSince(since)))
        return String(format: "%02d:%02d:%02d", seconds / 3600, seconds / 60 % 60, seconds % 60)
    }
}

/// Чип защиты: кружок с отметкой цвета вида, заголовок и значение.
struct ChipView: View {
    let chip: ProtectionChip

    var body: some View {
        HStack(spacing: PgSpace.s) {
            ZStack {
                Circle().fill(Color(chip.look.base))
                PgIcon(data: chip.look.mark, color: Color(PgColor.Surface.solid), size: 14, lineWidth: 3)
            }
            .frame(width: 20, height: 20)
            .accessibilityHidden(true)

            VStack(alignment: .leading, spacing: 0) {
                Text(chip.title).pgText(PgType.bodyStrong).foregroundStyle(Color(PgColor.Text.primary)).lineLimit(1)
                Text(chip.value).pgText(PgType.caption).foregroundStyle(Color(PgColor.Text.secondary)).lineLimit(1)
            }

            Spacer(minLength: 0)
        }
        .padding(PgSpace.m)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: PgRadius.button, style: .continuous).fill(Color(chip.look.tint)))
        .accessibilityElement(children: .combine)
    }
}

/// Кнопка-щит (layout.powerButton): кольцо цвета состояния, щит с отметкой; при подключении по кольцу бежит дуга. Подпись
/// состояния — под кнопкой в островке.
struct PowerButton: View {
    let look: StateLook
    let texts: PgTexts
    let state: TunnelState
    let action: () -> Void

    private var on: Bool { state == .connected || state == .connecting || state == .reconnecting }

    var body: some View {
        Button(action: action) {
            ZStack {
                Circle().fill(Color(PgColor.Surface.solid))
                Circle().stroke(Color(look.ring), lineWidth: PgLayout.PowerButton.ringWidth)
                if state == .connecting || state == .reconnecting {
                    TimelineView(.animation) { context in
                        let turn = context.date.timeIntervalSinceReferenceDate.truncatingRemainder(dividingBy: 1.1) / 1.1
                        Circle()
                            .trim(from: 0, to: 100.0 / 360)
                            .stroke(Color(PgState.Connecting.spinner), style: StrokeStyle(lineWidth: PgLayout.PowerButton.ringWidth, lineCap: .round))
                            .rotationEffect(.degrees(turn * 360))
                    }
                }

                PgIcon(data: PgIcons.shield, color: Color(look.icon), size: PgLayout.PowerButton.iconSize, lineWidth: 2)
                PgIcon(data: look.mark, color: Color(look.icon), size: PgLayout.PowerButton.iconSize, lineWidth: 2.2)
            }
            .frame(width: PgLayout.PowerButton.size, height: PgLayout.PowerButton.size)
            .shadow(color: Color(look.glow), radius: 14)
        }
        .buttonStyle(.plain)
        .disabled(state == .stopping)
        .accessibilityLabel(look.power)
        .accessibilityHint(on ? texts.powerActionDisconnect : texts.powerActionConnect)
        .accessibilityIdentifier("power")
    }
}

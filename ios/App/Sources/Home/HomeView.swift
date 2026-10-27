import PrivateGatewayCore
import PrivateGatewayTheme
import SwiftUI

/// Главная (доска 25): шапка, карточка состояния с четырьмя чипами защиты, сервер в работе, внизу — кнопка включения под
/// большой палец. Внешний IP и скорость — после замера: показывается только измеренное, без предполагаемых значений.
struct HomeView: View {
    let texts: PgTexts
    var status = TunnelStatus()
    var killSwitch = true
    var hasServers = false
    var version = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? ""
    var onPower: () -> Void = {}
    var onAdd: () -> Void = {}

    var body: some View {
        ZStack(alignment: .bottom) {
            PgBackground()
            ScrollView {
                VStack(alignment: .leading, spacing: PgSpace.l) {
                    header
                    StatusCard(texts: texts, status: status, killSwitch: killSwitch, hasServers: hasServers)
                    serverCard
                }
                .padding(.horizontal, PgSpace.windowSideMobile)
                .padding(.bottom, PgLayout.scrollBottomPadding)
            }

            PowerButton(look: StateLook.of(status.state, texts), texts: texts, on: status.state != .off && status.state != .error, action: onPower)
                .padding(.bottom, PgLayout.Island.bottomMargin)
        }
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
        }
        .padding(.top, PgSpace.l)
    }

    private var serverCard: some View {
        VStack(alignment: .leading, spacing: PgSpace.s) {
            Overline(text: texts.serverNone)
            Button(action: onAdd) {
                HStack {
                    Text(texts.serversNone).pgText(PgType.small).foregroundStyle(Color(PgColor.Text.secondary)).multilineTextAlignment(.leading)
                    Spacer()
                    PgIcon(data: PgIcons.plus, color: Color(PgColor.Accent.base))
                }
            }
            .accessibilityLabel(texts.headerAdd)
        }
        .pgCard()
    }
}

/// Карточка состояния: значок щита с отметкой, заголовок и подзаголовок, время в сети, четыре чипа и строка проверки.
struct StatusCard: View {
    let texts: PgTexts
    let status: TunnelStatus
    let killSwitch: Bool
    let hasServers: Bool

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
                Text(texts.actionCheck).pgText(PgType.bodyStrong).foregroundStyle(Color(PgColor.Accent.text))
            }
        }
        .pgCard()
    }

    /// Под заголовком: шифруется ли трафик, к какому серверу подключаемся, код ошибки.
    private var subtitle: String {
        switch status.state {
        case .connected: return texts.statusConnectedSub
        case .connecting, .stopping: return ""
        case .reconnecting: return texts.statusReconnectingSub(status.attempt)
        case .error: return texts.statusErrorSubDirect(status.error ?? "")
        case .off: return texts.statusOffSub
        }
    }

    /// Строка под чипами: что сделать, чтобы проверить защиту.
    private var note: String {
        switch status.state {
        case .off: return hasServers ? texts.checkedOff : texts.serverAddFirst
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

/// Кнопка включения внизу экрана (доска 25): кольцо цвета состояния, щит, подпись состояния.
struct PowerButton: View {
    let look: StateLook
    let texts: PgTexts
    let on: Bool
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            VStack(spacing: PgSpace.xxs) {
                ZStack {
                    Circle().fill(Color(PgColor.Surface.solid))
                    Circle().stroke(Color(look.ring), lineWidth: PgLayout.PowerButton.ringWidth)
                    PgIcon(data: PgIcons.shield, color: Color(look.icon), size: PgLayout.PowerButton.iconSize, lineWidth: 2)
                    PgIcon(data: look.mark, color: Color(look.icon), size: PgLayout.PowerButton.iconSize, lineWidth: 2.2)
                }
                .frame(width: PgLayout.PowerButton.size, height: PgLayout.PowerButton.size)
                .shadow(color: Color(look.glow), radius: PgSpace.l)

                Text(look.power).pgText(PgType.navActive).foregroundStyle(Color(look.icon))
            }
        }
        .buttonStyle(.plain)
        .accessibilityLabel(look.power)
        .accessibilityHint(on ? texts.powerActionDisconnect : texts.powerActionConnect)
        .accessibilityIdentifier("power")
    }
}

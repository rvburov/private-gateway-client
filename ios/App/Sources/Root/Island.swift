import PrivateGatewayCore
import PrivateGatewayTheme
import SwiftUI

/// Разделы окна.
enum AppTab: Hashable {
    case home
    case servers
    case check
    case settings
}

/// Островок внизу (layout.island, layout.powerButton) — как `Island` Android: разделы и кнопка-щит в выемке посередине, под
/// ней — состояние словом, не только цветом.
struct Island: View {
    let texts: PgTexts
    let tab: AppTab
    let state: TunnelState
    let hasServer: Bool
    let onTab: (AppTab) -> Void
    let onPower: () -> Void

    var body: some View {
        let lift = PgLayout.PowerButton.size / 2
        let shape = RoundedRectangle(cornerRadius: PgLayout.Island.cornerRadius, style: .continuous)
        ZStack(alignment: .top) {
            // Половины по обе стороны кнопки — равной ширины: подпись под кнопкой остается посередине.
            HStack(spacing: 0) {
                HStack(spacing: 0) {
                    NavItem(texts: texts, icon: PgIcons.navHome, detail: nil, label: texts.navHome, active: tab == .home) { onTab(.home) }
                        .accessibilityIdentifier("nav.home")
                    NavItem(texts: texts, icon: PgIcons.navServers, detail: PgIcons.navServersDetail, label: texts.navServers, active: tab == .servers) {
                        onTab(.servers)
                    }
                    .accessibilityIdentifier("nav.servers")
                }
                .frame(maxWidth: .infinity)
                powerLabel.frame(width: PgLayout.PowerButton.size)
                HStack(spacing: 0) {
                    NavItem(texts: texts, icon: PgIcons.navCheck, detail: PgIcons.navCheckDetail, label: texts.navCheck, active: tab == .check) {
                        onTab(.check)
                    }
                    .accessibilityIdentifier("nav.check")
                    NavItem(texts: texts, icon: PgIcons.navSettings, detail: PgIcons.navSettingsDetail, label: texts.navSettings, active: tab == .settings) {
                        onTab(.settings)
                    }
                    .accessibilityIdentifier("nav.settings")
                }
                .frame(maxWidth: .infinity)
            }
            .frame(height: PgLayout.Island.height)
            .background(shape.fill(Color(PgColor.Surface.card)))
            .overlay(shape.stroke(Color(PgColor.Line.border), lineWidth: 1))
            .pgShadow(PgShadow.island)
            .padding(.top, lift)

            // Выемка: круг цвета островка за кнопкой (layout.island.bumpRadius).
            Circle()
                .fill(Color(PgColor.Surface.card))
                .frame(width: PgLayout.Island.bumpRadius * 2, height: PgLayout.Island.bumpRadius * 2)
                .offset(y: lift - PgLayout.Island.bumpRadius + 4)
            PowerButton(look: StateLook.of(state, texts), texts: texts, state: state, action: onPower)
                .offset(y: 4)
        }
        .frame(height: PgLayout.Island.height + lift, alignment: .top)
        .padding(.horizontal, PgLayout.Island.sideMarginMobile)
        .padding(.bottom, PgLayout.Island.bottomMargin)
    }

    /// Подпись под кнопкой-щитом — состояние словом («Защищено», «Выключено»); подключаться не к чему — «Нет сервера».
    private var powerLabel: some View {
        let look = StateLook.of(state, texts)
        let label = !hasServer && state == .off ? texts.powerLabelNoServer : look.power
        return Text(label)
            .pgText(PgType.navMobileActive)
            .foregroundStyle(Color(look.icon))
            .lineLimit(1)
            .frame(maxHeight: .infinity, alignment: .bottom)
            .padding(.bottom, 8)
            .accessibilityHidden(true)
    }
}

/// Раздел островка: иконка (с деталью) и подпись; выбранный — акцентным цветом.
struct NavItem: View {
    let texts: PgTexts
    let icon: String
    let detail: String?
    let label: String
    let active: Bool
    let action: () -> Void

    var body: some View {
        let color = Color(active ? PgColor.Accent.text : PgColor.Text.muted)
        Button(action: action) {
            VStack(spacing: 2) {
                ZStack {
                    PgIcon(data: icon, color: color, size: 22)
                    if let detail {
                        PgIcon(data: detail, color: color, size: 22)
                    }
                }

                Text(label).pgText(active ? PgType.navMobileActive : PgType.navMobile).foregroundStyle(color).lineLimit(1)
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(active ? [.isButton, .isSelected] : .isButton)
    }
}

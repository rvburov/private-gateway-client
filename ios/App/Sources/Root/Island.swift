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

/// Островок внизу (IslandNav десктопа, layout.island, layout.powerButton) — белая плашка surface.solid с обводкой
/// line.divider и тенью shadow.island, выступ под кнопку-щит одним контуром с плашкой (`IslandShape`). Разделы — по центру
/// полосы, под щитом — состояние словом, не только цветом.
struct Island: View {
    /// Колонка под щитом (layout.island.navColumnsMobile, «1fr 1fr 76px 1fr 1fr»): колонки CSS в Swift не генерируются.
    static let shieldColumn: CGFloat = 76

    let texts: PgTexts
    let tab: AppTab
    let state: TunnelState
    let hasServer: Bool
    /// «Блокировка» и «Утечка IP» — свои подпись и цвет под щитом (`StateLook`).
    var blocked = false
    var leak = false
    let onTab: (AppTab) -> Void
    let onPower: () -> Void

    var body: some View {
        let look = StateLook.of(state, texts, blocked: blocked, leak: leak)
        // Верх плашки — на уровне центра щита: выступ поднимается над плашкой ровно на свой радиус.
        let top = CGFloat(PgLayout.Island.bumpRadius)
        let shape = IslandShape(side: CGFloat(PgLayout.Island.sideMarginMobile), top: top)
        ZStack(alignment: .top) {
            shape
                .fill(Color(PgColor.Surface.solid))
                .pgShadow(PgShadow.island)
                .overlay(shape.stroke(Color(PgColor.Line.divider), lineWidth: 1))

            HStack(spacing: 0) {
                NavItem(texts: texts, icon: PgIcons.navHome, detail: nil, label: texts.navHome, active: tab == .home) { onTab(.home) }
                    .accessibilityIdentifier("nav.home")
                NavItem(texts: texts, icon: PgIcons.navServers, detail: PgIcons.navServersDetail, label: texts.navServers, active: tab == .servers) {
                    onTab(.servers)
                }
                .accessibilityIdentifier("nav.servers")
                powerLabel(look).frame(width: Self.shieldColumn)
                NavItem(texts: texts, icon: PgIcons.navCheck, detail: PgIcons.navCheckDetail, label: texts.navCheck, active: tab == .check) {
                    onTab(.check)
                }
                .accessibilityIdentifier("nav.check")
                NavItem(texts: texts, icon: PgIcons.navSettings, detail: PgIcons.navSettingsDetail, label: texts.navSettings, active: tab == .settings) {
                    onTab(.settings)
                }
                .accessibilityIdentifier("nav.settings")
            }
            .padding(.horizontal, PgSpace.island)
            .frame(height: PgLayout.Island.height)
            .padding(.top, top)

            PowerButton(look: look, texts: texts, state: state, blocked: blocked, action: onPower)
                .padding(.top, top - CGFloat(PgLayout.PowerButton.size) / 2)
        }
        .frame(height: top + CGFloat(PgLayout.Island.height), alignment: .top)
        .padding(.bottom, PgLayout.Island.bottomMargin)
    }

    /// Подпись под кнопкой-щитом — состояние словом («Защищено», «Выключено») у низа полосы; подключаться не к чему —
    /// «Нет сервера».
    private func powerLabel(_ look: StateLook) -> some View {
        let label = !hasServer && state == .off ? texts.powerLabelNoServer : look.power
        return Text(label)
            .pgText(PgType.navMobileActive)
            .foregroundStyle(Color(look.icon))
            .lineLimit(1)
            .minimumScaleFactor(0.7)
            .frame(maxHeight: .infinity, alignment: .bottom)
            .padding(.bottom, 9)
            .accessibilityHidden(true)
    }
}

/// Раздел островка (Button.nav десктопа, .btn-nav макета): значок 24 линией 1,7 и подпись через 4 по центру полосы, отступ
/// 9 снизу; выбранный — accent.base, первый контур значка залит accent.navActiveFill.
struct NavItem: View {
    let texts: PgTexts
    let icon: String
    let detail: String?
    let label: String
    let active: Bool
    let action: () -> Void

    var body: some View {
        let color = Color(active ? PgColor.Accent.base : PgColor.Text.muted)
        Button(action: action) {
            VStack(spacing: PgSpace.xxs) {
                ZStack {
                    if active {
                        PgIconShape(data: icon).fill(Color(PgColor.Accent.navActiveFill)).frame(width: 24, height: 24)
                    }

                    PgIcon(data: icon, color: color, size: 24, lineWidth: 1.7)
                    if let detail {
                        PgIcon(data: detail, color: color, size: 24, lineWidth: 1.7)
                    }
                }
                .accessibilityHidden(true)

                Text(label).pgText(active ? PgType.navMobileActive : PgType.navMobile).foregroundStyle(color).lineLimit(1)
            }
            .padding(.bottom, 9)
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(active ? [.isButton, .isSelected] : .isButton)
    }
}

/// Контур островка (IslandGeometry десктопа, islandPath макета) данными пути SVG: плашка высотой layout.island.height от
/// `side` до `width − side` со скруглениями cornerRadius, выступ под щит — дуга bumpRadius вокруг центра щита с плавными
/// скруглениями bumpFilletRadius; верх плашки — на `top`. У десктопа `top` = 70: «M38 70 H… A18 18 0 0 0 … 56.91 …».
enum IslandGeometry {
    static func data(width: Double, side: Double, top: Double) -> String {
        let corner = PgLayout.Island.cornerRadius
        let bottom = top + PgLayout.Island.height
        let bump = PgLayout.Island.bumpRadius
        let fillet = PgLayout.Island.bumpFilletRadius
        // Скругление касается верха плашки и дуги выступа: центры дуг — на расстоянии bump + fillet друг от друга.
        let reach = ((bump + fillet) * (bump + fillet) - fillet * fillet).squareRoot()
        let touchX = reach * bump / (bump + fillet)
        let touchY = top - fillet * bump / (bump + fillet)
        let center = width / 2
        let right = width - side
        let start = side + corner
        var outline = "M\(start) \(top) H\(center - reach)"
        outline += " A\(fillet) \(fillet) 0 0 0 \(center - touchX) \(touchY)"
        outline += " A\(bump) \(bump) 0 0 1 \(center + touchX) \(touchY)"
        outline += " A\(fillet) \(fillet) 0 0 0 \(center + reach) \(top)"
        outline += " H\(right - corner) A\(corner) \(corner) 0 0 1 \(right) \(top + corner)"
        outline += " V\(bottom - corner) A\(corner) \(corner) 0 0 1 \(right - corner) \(bottom)"
        outline += " H\(start) A\(corner) \(corner) 0 0 1 \(side) \(bottom - corner)"
        outline += " V\(top + corner) A\(corner) \(corner) 0 0 1 \(start) \(top) Z"
        return outline
    }
}

/// Плашка островка с выступом под щит — одна фигура: заливка, обводка и тень идут по всему контуру вместе с выступом.
struct IslandShape: Shape {
    var side: CGFloat
    var top: CGFloat

    func path(in rect: CGRect) -> Path {
        Path(svg: IslandGeometry.data(width: Double(rect.width), side: Double(side), top: Double(top)), origin: rect.origin)
    }
}

/// Нажатие на щит (app.css .power:active, Button.power:pressed десктопа): белый круг под кольцом заливается цветом состояния
/// (state.*.fill), кнопка сжимается до 0,96. Свечение glow (0 4 12 цветом состояния) — у белой подложки, а не у кольца и
/// знака.
struct PowerPressStyle: ButtonStyle {
    let look: StateLook

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .background(
                ZStack {
                    Circle().fill(Color(PgColor.Surface.solid)).shadow(color: Color(look.glow), radius: 6, x: 0, y: 4)
                    Circle().fill(Color(look.fill)).opacity(configuration.isPressed ? 1 : 0)
                }
            )
            .scaleEffect(configuration.isPressed ? 0.96 : 1)
    }
}

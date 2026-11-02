import PrivateGatewayTheme
import SwiftUI
import UIKit

/// Тема SwiftUI из значений `PrivateGatewayTheme` (сгенерированы из design-tokens.json при сборке): цвет, шрифт, иконка,
/// карточка, тень и фон — как `Theme.kt` Android. Только светлая тема, как решено в макете; значения вручную не задаются.
extension Color {
    /// Цвет токена: ARGB → sRGB с прозрачностью.
    init(_ color: PgRGBA) {
        self.init(.sRGB, red: color.red, green: color.green, blue: color.blue, opacity: color.alpha)
    }
}

extension PgTextStyle {
    /// Системный шрифт iOS (SF Pro и SF Mono — font.ios) размера и насыщенности токена.
    var font: Font {
        .system(size: size, weight: fontWeight, design: mono ? .monospaced : .default)
    }

    /// Насыщенность 100–900 → ближайшая из Font.Weight.
    var fontWeight: Font.Weight {
        switch weight {
        case ..<150: return .ultraLight
        case ..<250: return .thin
        case ..<350: return .light
        case ..<450: return .regular
        case ..<550: return .medium
        case ..<650: return .semibold
        case ..<750: return .bold
        case ..<850: return .heavy
        default: return .black
        }
    }

    /// Межстрочный интервал сверх обычного (~1,2 размера) — из lineHeight токена.
    var extraLineSpacing: CGFloat {
        guard let lineHeight else {
            return 0
        }

        return max(0, (lineHeight - 1.2) * size)
    }
}

extension PgTextStyle {
    /// То же начертание другой насыщенности: полужирный выбранного сегмента и текущего пункта меню (600 — как bodyStrong),
    /// Medium подсказки и кнопки выбора (500 — как в app.css).
    func with(weight: Int) -> PgTextStyle {
        PgTextStyle(size: size, weight: weight, tracking: tracking, lineHeight: lineHeight, mono: mono, uppercase: uppercase)
    }

    /// То же начертание с другой высотой строки: текст диалога — размером small и высотой строки тела, как в app.css.
    func with(lineHeight: Double?) -> PgTextStyle {
        PgTextStyle(size: size, weight: weight, tracking: tracking, lineHeight: lineHeight, mono: mono, uppercase: uppercase)
    }
}

extension PgTextStyle {
    /// Самый крупный «Размер текста», под который рассчитаны экраны: дальше текст не растет, иначе островок и карточки
    /// перестают помещаться.
    static let largestSize = DynamicTypeSize.accessibility2

    /// Во сколько раз «Размер текста» iOS увеличивает текст против обычного — как sp у Android: токен — размер при обычном
    /// размере текста. Кривая — системная, текста тела (`UIFontMetrics`).
    @MainActor
    static func scale(_ size: DynamicTypeSize) -> CGFloat {
        let traits = UITraitCollection(preferredContentSizeCategory: category(min(size, largestSize)))
        return UIFontMetrics(forTextStyle: .body).scaledValue(for: 100, compatibleWith: traits) / 100
    }

    @MainActor
    private static func category(_ size: DynamicTypeSize) -> UIContentSizeCategory {
        switch size {
        case .xSmall: return .extraSmall
        case .small: return .small
        case .medium: return .medium
        case .large: return .large
        case .xLarge: return .extraLarge
        case .xxLarge: return .extraExtraLarge
        case .xxxLarge: return .extraExtraExtraLarge
        case .accessibility1: return .accessibilityMedium
        case .accessibility2: return .accessibilityLarge
        case .accessibility3: return .accessibilityExtraLarge
        case .accessibility4: return .accessibilityExtraExtraLarge
        case .accessibility5: return .accessibilityExtraExtraExtraLarge
        @unknown default: return .large
        }
    }
}

extension View {
    /// Текст начертанием токена: шрифт, трекинг (доля размера), межстрочный интервал и заглавные. Размер растет с «Размером
    /// текста» iOS (доступность), как у Android.
    func pgText(_ style: PgTextStyle) -> some View {
        modifier(PgTextModifier(style: style))
    }

    /// Тень макета (shadow.*): все слои по порядку.
    func pgShadow(_ layers: [PgShadowLayer]) -> some View {
        modifier(PgShadowModifier(layers: layers))
    }

    /// Стеклянная карточка: заливка surface.card, рамка line.border, скругление radius.card, тень shadow.card.
    func pgCard(padding: CGFloat = PgSpace.card) -> some View {
        let shape = RoundedRectangle(cornerRadius: PgRadius.card, style: .continuous)
        return self
            .padding(padding)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(shape.fill(Color(PgColor.Surface.card)))
            .overlay(shape.stroke(Color(PgColor.Line.border), lineWidth: 1))
            .pgShadow(PgShadow.card)
    }
}

/// Начертание токена в размере, выбранном пользователем в «Размере текста».
struct PgTextModifier: ViewModifier {
    let style: PgTextStyle
    @Environment(\.dynamicTypeSize) private var typeSize

    func body(content: Content) -> some View {
        let scale = PgTextStyle.scale(typeSize)
        let size = style.size * scale
        content
            .font(.system(size: size, weight: style.fontWeight, design: style.mono ? .monospaced : .default))
            .tracking((style.tracking ?? 0) * size)
            .lineSpacing(style.extraLineSpacing * scale)
            .textCase(style.uppercase ? .uppercase : nil)
    }
}

struct PgShadowModifier: ViewModifier {
    let layers: [PgShadowLayer]

    func body(content: Content) -> some View {
        // box-shadow: размытие CSS — вдвое больше радиуса тени SwiftUI.
        layers.reduce(AnyView(content)) { view, layer in
            AnyView(view.shadow(color: Color(layer.color), radius: layer.blur / 2, x: layer.x, y: layer.y))
        }
    }
}

extension Path {
    /// Контур SVG (`d`, разбор — `SvgPath`) в масштабе `scale` от точки `origin`: иконки макета и контур островка.
    /// Неразобранный контур — пустой.
    init(svg data: String, scale: CGFloat = 1, origin: CGPoint = .zero) {
        self.init()
        guard let commands = try? SvgPath.parse(data) else {
            return
        }

        func point(_ point: PgPoint) -> CGPoint {
            CGPoint(x: origin.x + point.x * scale, y: origin.y + point.y * scale)
        }

        for command in commands {
            switch command {
            case let .move(to):
                move(to: point(to))
            case let .line(to):
                addLine(to: point(to))
            case let .cubic(first, second, to):
                addCurve(to: point(to), control1: point(first), control2: point(second))
            case let .quad(control, to):
                addQuadCurve(to: point(to), control: point(control))
            case .close:
                closeSubpath()
            }
        }
    }
}

/// Контур иконки макета (24×24, `PgIcons`) в размер прямоугольника; неразобранный контур — пустой (тесты пакета разбирают
/// каждую иконку).
struct PgIconShape: Shape {
    let data: String

    func path(in rect: CGRect) -> Path {
        Path(svg: data, scale: min(rect.width, rect.height) / 24, origin: rect.origin)
    }
}

/// Иконка макета: линия толщиной `lineWidth` в единицах контура 24×24 (как у Android), круглые концы и стыки.
struct PgIcon: View {
    let data: String
    var color = Color(PgColor.Text.primary)
    var size: CGFloat = 20
    var lineWidth: CGFloat = 1.8

    var body: some View {
        PgIconShape(data: data)
            .stroke(color, style: StrokeStyle(lineWidth: lineWidth * size / 24, lineCap: .round, lineJoin: .round))
            .frame(width: size, height: size)
            .accessibilityHidden(true)
    }
}

/// Фон окна (app.css .pg-app): bg.window, три цветных пятна color.decor и поверх них стекло glass.fill (.pg-glass), под
/// которым пятна бледнеют. Места и размеры пятен — как в app.css, от левого верхнего угла рамки телефона 393 пункта.
struct PgBackground: View {
    var body: some View {
        ZStack(alignment: .topLeading) {
            Color(PgColor.Bg.window)
            blob(PgColor.Decor.blob1, x: -110, y: -90, width: 360, height: 360)
            blob(PgColor.Decor.blob2, x: 220, y: 220, width: 340, height: 340)
            blob(PgColor.Decor.blob3, x: 10, y: 590, width: 380, height: 320)
            Color(PgGlass.fill)
        }
        .ignoresSafeArea()
        .accessibilityHidden(true)
    }

    /// Пятно — radial-gradient(цвет 35 %, прозрачный 70 %) до дальнего угла своего прямоугольника; овал — тот же круг,
    /// сжатый по высоте. Прозрачный край — тот же цвет без непрозрачности: переход без серой каймы.
    private func blob(_ color: PgRGBA, x: CGFloat, y: CGFloat, width: CGFloat, height: CGFloat) -> some View {
        let side = max(width, height)
        let gradient = Gradient(stops: [
            Gradient.Stop(color: Color(color), location: 0.35),
            Gradient.Stop(color: Color(color).opacity(0), location: 0.7),
        ])
        return RadialGradient(gradient: gradient, center: .center, startRadius: 0, endRadius: side / 2 * CGFloat(2).squareRoot())
            .frame(width: side, height: side)
            .scaleEffect(x: width / side, y: height / side)
            .frame(width: width, height: height)
            .offset(x: x, y: y)
    }
}

/// Надзаголовок карточки: type.overline, заглавными, если так в токенах.
struct Overline: View {
    let text: String
    var color = Color(PgColor.Text.muted)

    var body: some View {
        Text(text).pgText(PgType.overline).foregroundStyle(color)
    }
}

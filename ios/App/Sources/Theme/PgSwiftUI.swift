import PrivateGatewayTheme
import SwiftUI

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

extension View {
    /// Текст начертанием токена: шрифт, трекинг (доля размера), межстрочный интервал и заглавные.
    func pgText(_ style: PgTextStyle) -> some View {
        font(style.font)
            .tracking((style.tracking ?? 0) * style.size)
            .lineSpacing(style.extraLineSpacing)
            .textCase(style.uppercase ? .uppercase : nil)
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

struct PgShadowModifier: ViewModifier {
    let layers: [PgShadowLayer]

    func body(content: Content) -> some View {
        // box-shadow: размытие CSS — вдвое больше радиуса тени SwiftUI.
        layers.reduce(AnyView(content)) { view, layer in
            AnyView(view.shadow(color: Color(layer.color), radius: layer.blur / 2, x: layer.x, y: layer.y))
        }
    }
}

/// Контур иконки макета (24×24, `PgIcons`) в размер прямоугольника; неразобранный контур — пустой (тесты пакета разбирают
/// каждую иконку).
struct PgIconShape: Shape {
    let data: String

    func path(in rect: CGRect) -> Path {
        var path = Path()
        guard let commands = try? SvgPath.parse(data) else {
            return path
        }

        let scale = min(rect.width, rect.height) / 24
        func point(_ point: PgPoint) -> CGPoint {
            CGPoint(x: rect.minX + point.x * scale, y: rect.minY + point.y * scale)
        }

        for command in commands {
            switch command {
            case let .move(to):
                path.move(to: point(to))
            case let .line(to):
                path.addLine(to: point(to))
            case let .cubic(first, second, to):
                path.addCurve(to: point(to), control1: point(first), control2: point(second))
            case let .quad(control, to):
                path.addQuadCurve(to: point(to), control: point(control))
            case .close:
                path.closeSubpath()
            }
        }

        return path
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

/// Фон окна с цветными пятнами макета (color.decor) — под стеклянными карточками.
struct PgBackground: View {
    var body: some View {
        GeometryReader { proxy in
            ZStack {
                Color(PgColor.Bg.window)
                blob(PgColor.Decor.blob1, x: 0.05, y: 0.06, radius: 0.75, in: proxy.size)
                blob(PgColor.Decor.blob2, x: 1.0, y: 0.45, radius: 0.7, in: proxy.size)
                blob(PgColor.Decor.blob3, x: 0.1, y: 0.95, radius: 0.8, in: proxy.size)
            }
        }
        .ignoresSafeArea()
    }

    private func blob(_ color: PgRGBA, x: CGFloat, y: CGFloat, radius: CGFloat, in size: CGSize) -> some View {
        RadialGradient(colors: [Color(color), .clear], center: UnitPoint(x: x, y: y), startRadius: 0, endRadius: size.width * radius)
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

import PrivateGatewayTheme
import SwiftUI

/// Плотности меток доски 24: версия, выбранный сервер и итог проверки.
enum PgBadgeKind: Equatable { case badge, pill, result }

struct PgBadge: View {
    let text: String
    var ink = PgColor.Accent.text
    var tint = PgColor.Accent.tint
    var kind = PgBadgeKind.badge
    var identifier = ""

    var body: some View {
        Text(text)
            .pgText(PgTextStyle(size: PgType.overline.size, weight: kind == .pill ? 700 : 600))
            .foregroundStyle(Color(ink))
            .lineLimit(1)
            .fixedSize(horizontal: kind == .result, vertical: kind == .result)
            .padding(.horizontal, kind == .badge ? PgSpace.xs : kind == .pill ? 7 : 9)
            .padding(.vertical, kind == .badge ? 2 : 3)
            .background(RoundedRectangle(cornerRadius: kind == .result ? PgRadius.input : PgRadius.chip, style: .continuous).fill(Color(tint)))
            .accessibilityIdentifier(identifier)
    }
}

/// Полоса предупреждения (.banner): компактная — импорт; обычная — текст с необязательным значком.
struct PgBanner<Content: View>: View {
    var icon: String?
    var compact = false
    var ink = PgColor.Status.Warning.badge
    var tint = PgColor.Status.Warning.tint
    var edge = PgColor.Status.Warning.line
    var identifier = ""
    @ViewBuilder let content: () -> Content

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: compact ? 12 : 16, style: .continuous)
        HStack(alignment: .top, spacing: PgSpace.m) {
            if let icon {
                PgIcon(data: icon, color: Color(ink), size: 18, lineWidth: 2).padding(.top, 1)
                    .accessibilityHidden(true)
            }
            VStack(alignment: .leading, spacing: 2, content: content)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
        .padding(.vertical, compact ? PgSpace.m : PgSpace.l)
        .padding(.horizontal, compact ? PgSpace.l : 14)
        .background(shape.fill(Color(tint)))
        .overlay(shape.strokeBorder(Color(edge), lineWidth: 1))
        .accessibilityElement(children: .combine)
        .accessibilityIdentifier(identifier)
    }
}

/// Плитка действия (.btn-tile, .sc-tile): значок 18 над подписью caption, высота 58, скругление 14, белая с рамкой
/// line.strong; нажатая (.on, «Скрыть QR») — accent.tint и рамка accent.base, опасная — красные подпись и рамка,
/// недоступная — прозрачность 0,45. Почему недоступна — подсказка VoiceOver.
struct PgTileButton: View {
    let icon: String
    let label: String
    var on = false
    var danger = false
    var enabled = true
    var hint: String?
    var identifier = ""
    let action: () -> Void

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: 14, style: .continuous)
        Button(action: action) {
            VStack(spacing: PgSpace.xxs) {
                PgIcon(data: icon, color: Color(ink), size: 18, lineWidth: 1.9)
                Text(label)
                    .pgText(PgType.caption)
                    .foregroundStyle(Color(ink))
                    .lineLimit(1)
                    .minimumScaleFactor(0.8)
            }
            .padding(.horizontal, PgSpace.xxs)
            .frame(maxWidth: .infinity, minHeight: 58)
            .background(shape.fill(Color(on ? PgColor.Accent.tint : PgColor.Surface.solid)))
            .overlay(shape.strokeBorder(Color(border), lineWidth: 1))
            .contentShape(Rectangle())
        }
        .buttonStyle(PgPressStyle())
        .disabled(!enabled)
        .opacity(enabled ? 1 : 0.45)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(label)
        .accessibilityHint(hint ?? "")
        .accessibilityAddTraits(on ? [.isButton, .isSelected] : .isButton)
        .accessibilityIdentifier(identifier)
    }

    private var ink: PgRGBA {
        if danger {
            return PgColor.Status.Danger.text
        }

        return on ? PgColor.Accent.text : PgColor.Text.primary
    }

    private var border: PgRGBA {
        if danger {
            return PgColor.Status.Danger.line
        }

        return on ? PgColor.Accent.base : PgColor.Line.strong
    }
}

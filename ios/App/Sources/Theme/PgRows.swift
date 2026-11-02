import PrivateGatewayTheme
import SwiftUI

/// Тон плитки значка (.row-icon строки-ссылки, .dialog-icon диалога): цвет подложки и значка. Синий — accent.tint и
/// accent.text, предупреждения — оттенок и текст status.*, цветные плитки строк — surface.inset и tile.*.
enum PgTone: Equatable {
    case accent
    case warning
    case caution
    case danger
    case violet
    case teal
    case amber

    var tint: PgRGBA {
        switch self {
        case .accent: return PgColor.Accent.tint
        case .warning: return PgColor.Status.Warning.tint
        case .caution: return PgColor.Status.Caution.tint
        case .danger: return PgColor.Status.Danger.tint
        case .violet, .teal, .amber: return PgColor.Surface.inset
        }
    }

    var ink: PgRGBA {
        switch self {
        case .accent: return PgColor.Accent.text
        case .warning: return PgColor.Status.Warning.text
        case .caution: return PgColor.Status.Caution.text
        case .danger: return PgColor.Status.Danger.text
        case .violet: return PgColor.Tile.violet
        case .teal: return PgColor.Tile.teal
        case .amber: return PgColor.Tile.amber
        }
    }
}

/// Строка настройки (.setting): заголовок полужирным (bodyStrong), под ним подпись caption цветом text.muted, справа —
/// переключатель, выбор или кнопка. Поля 12 × 16 — строка лежит в карточке без своих полей (`pgCard(padding: 0)`), строки
/// разделяет `PgDivider`; `padded: false` — для строки в карточке со своими полями. Недоступная — серые заголовок и подпись.
/// `warning` — третья строка подписи цветом status.warning.badge (.sb-block доски 12): почему настройка сейчас не действует.
struct PgSettingRow<Trailing: View>: View {
    let title: String
    var caption: String?
    var warning: String?
    var enabled = true
    var padded = true
    @ViewBuilder let trailing: () -> Trailing

    var body: some View {
        HStack(spacing: PgSpace.l) {
            VStack(alignment: .leading, spacing: 2) {
                Text(title).pgText(PgType.bodyStrong).foregroundStyle(Color(enabled ? PgColor.Text.primary : PgColor.Text.muted))
                if let caption, !caption.isEmpty {
                    Text(caption).pgText(PgType.caption).foregroundStyle(Color(PgColor.Text.muted))
                }

                if let warning, !warning.isEmpty {
                    Text(warning).pgText(PgType.caption).foregroundStyle(Color(PgColor.Status.Warning.badge))
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)

            trailing()
        }
        .padding(.vertical, padded ? PgSpace.l : 0)
        .padding(.horizontal, padded ? PgSpace.card : 0)
    }
}

/// Строка-ссылка в карточке (.link-row): плитка значка 34 со скруглением radius.input (.row-icon, тон — `PgTone`),
/// заголовок полужирным, подпись caption в одну строку, стрелка «›» по правому краю цветом text.muted; высота не меньше 52,
/// поля 16. Крупная (`large`, «Для опытных» доски 04) — плитка 36 со значком 20, поля 10 сверху и снизу, стрелка линией 2.
/// Строки разделяет `PgDivider`; недоступная — серым, прозрачность 0,6.
struct PgLinkRow: View {
    let icon: String
    var detail: String?
    let title: String
    var caption: String?
    var tone = PgTone.accent
    var large = false
    var enabled = true
    var identifier = ""
    let action: () -> Void

    var body: some View {
        let glyph: CGFloat = large ? 20 : 18
        let tile: CGFloat = large ? 36 : 34
        Button(action: action) {
            HStack(spacing: PgSpace.l) {
                ZStack {
                    PgIcon(data: icon, color: Color(tone.ink), size: glyph, lineWidth: 1.9)
                    if let detail {
                        PgIcon(data: detail, color: Color(tone.ink), size: glyph, lineWidth: 1.9)
                    }
                }
                .frame(width: tile, height: tile)
                .background(RoundedRectangle(cornerRadius: PgRadius.input, style: .continuous).fill(Color(tone.tint)))

                VStack(alignment: .leading, spacing: 2) {
                    Text(title).pgText(PgType.bodyStrong).foregroundStyle(Color(enabled ? PgColor.Text.primary : PgColor.Text.muted))
                    if let caption, !caption.isEmpty {
                        Text(caption).pgText(PgType.caption).foregroundStyle(Color(PgColor.Text.muted)).lineLimit(1)
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)

                PgIcon(data: PgIcons.chevronRight, color: Color(PgColor.Text.muted), size: 16, lineWidth: large ? 2 : 1.8)
            }
            .padding(.horizontal, PgSpace.card)
            .padding(.vertical, large ? PgSpace.m : 0)
            .frame(minHeight: 52)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .disabled(!enabled)
        .opacity(enabled ? 1 : 0.6)
        .accessibilityIdentifier(identifier)
    }
}

/// Полоса между строками карточки (.divider; .setting + .setting и .link-row + .link-row макета): 1 пункт line.divider.
struct PgDivider: View {
    var body: some View {
        Rectangle()
            .fill(Color(PgColor.Line.divider))
            .frame(height: 1)
            .accessibilityHidden(true)
    }
}

import Foundation
import PrivateGatewayTheme
import SwiftUI

/// Вид диалога (доска 18, DialogKind десктопа): цвет значка и основной кнопки.
enum PgDialogKind: Equatable {
    /// Предупреждение: оранжевый значок, основная кнопка — безопасный вариант («Оставить включенным»).
    case warning
    /// Вопрос: синий значок («Включить строгий режим?»).
    case info
    /// Удаление: красный значок и красная основная кнопка «Удалить».
    case danger

    var tone: PgTone {
        switch self {
        case .warning: return .warning
        case .info: return .accent
        case .danger: return .danger
        }
    }
}

/// Что выбрал пользователь в диалоге. Нажатие на затемнение — ничего не выбрал, значит, ничего не делать.
enum PgDialogChoice: Equatable {
    case primary
    case secondary
    case dismissed
}

/// Диалог подтверждения (.dialog макета, DialogViewModel десктопа): значок в плитке тона, заголовок, текст и две кнопки во
/// всю ширину — основная и вторая; вторая может быть опасной (красная рамка и текст). Тексты — из `PgTexts`.
struct PgDialog: Identifiable, Equatable {
    let id = UUID()
    let kind: PgDialogKind
    /// Значок (`PgIcons`) и деталь поверх него — щит со знаком «!», как у десктопа.
    let icon: String
    var detail: String?
    let title: String
    let text: String
    let primary: String
    let secondary: String
    var secondaryDanger = false

    /// Какая кнопка — «да»: опасная вторая («Выключить», «Перейти на прокси») или основная («Удалить», «Включить»).
    var yes: PgDialogChoice {
        secondaryDanger ? .secondary : .primary
    }
}

/// Вид диалога: белая карточка radius.card с тенью shadow.dialog и полями 20, значок 26 в плитке 46 тона диалога,
/// заголовок type.status, текст small высотой строки тела цветом text.secondary, кнопки одна под другой — основная сверху.
/// Затемнение и место в окне дает `PgLayers`.
struct PgDialogView: View {
    let dialog: PgDialog
    let onChoice: (PgDialogChoice) -> Void

    var body: some View {
        let tone = dialog.kind.tone
        VStack(alignment: .leading, spacing: 0) {
            ZStack {
                PgIcon(data: dialog.icon, color: Color(tone.ink), size: 26, lineWidth: 1.9)
                if let detail = dialog.detail {
                    PgIcon(data: detail, color: Color(tone.ink), size: 26, lineWidth: 1.9)
                }
            }
            .frame(width: 46, height: 46)
            .background(RoundedRectangle(cornerRadius: PgRadius.button, style: .continuous).fill(Color(tone.tint)))
            .padding(.bottom, PgSpace.l)

            Text(dialog.title)
                .pgText(PgType.status)
                .foregroundStyle(Color(PgColor.Text.primary))
                .fixedSize(horizontal: false, vertical: true)
                .accessibilityAddTraits(.isHeader)
            Text(dialog.text)
                .pgText(PgType.small.with(lineHeight: PgType.body.lineHeight))
                .foregroundStyle(Color(PgColor.Text.secondary))
                .fixedSize(horizontal: false, vertical: true)
                .padding(.top, PgSpace.l)

            VStack(spacing: PgSpace.s) {
                PgActionButton(label: dialog.primary, danger: dialog.kind == .danger, identifier: "dialog.primary") {
                    onChoice(.primary)
                }
                PgActionButton(label: dialog.secondary, kind: .secondary, danger: dialog.secondaryDanger, identifier: "dialog.secondary") {
                    onChoice(.secondary)
                }
            }
            .padding(.top, 18)
        }
        .padding(20)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: PgRadius.card, style: .continuous).fill(Color(PgColor.Surface.solid)))
        .pgShadow(PgShadow.dialog)
        .padding(.horizontal, PgSpace.island)
        .accessibilityElement(children: .contain)
        .accessibilityAddTraits(.isModal)
    }
}

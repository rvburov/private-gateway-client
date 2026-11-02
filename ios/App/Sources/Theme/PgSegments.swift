import PrivateGatewayTheme
import SwiftUI

/// Сегменты (.segments и .segment, RadioButton.segment десктопа): дорожка control.segmentTrack со скруглением radius.button
/// и отступом 4, сегмент высотой 38 — текст small цветом text.secondary; выбранный — белая плашка radius.input с тенью
/// карточки, текст text.primary полужирным. Переход с вопросом (data-confirm макета, ConfirmRadioButton десктопа): сегмент
/// сам не отмечается — отметка приходит из `selection` после «да» в диалоге `confirm`.
struct PgSegments<Value: Hashable>: View {
    let options: [PgOption<Value>]
    let selection: Value
    var enabled = true
    var identifier = ""
    var confirm: (Value) -> PgDialog? = { _ in nil }
    let onSelect: (Value) -> Void

    @Environment(\.pgLayers) private var layers

    var body: some View {
        HStack(spacing: 0) {
            ForEach(options) { option in
                segment(option)
            }
        }
        .padding(PgSpace.xxs)
        .background(RoundedRectangle(cornerRadius: PgRadius.button, style: .continuous).fill(Color(PgColor.Control.segmentTrack)))
        .disabled(!enabled)
        .opacity(enabled ? 1 : 0.6)
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier(identifier)
    }

    private func segment(_ option: PgOption<Value>) -> some View {
        let on = option.value == selection
        return Button {
            Self.pick(option.value, selection: selection, dialog: confirm(option.value), layers: layers, onSelect: onSelect)
        } label: {
            Text(option.title)
                .pgText(on ? PgType.small.with(weight: PgType.bodyStrong.weight) : PgType.small)
                .foregroundStyle(Color(on ? PgColor.Text.primary : enabled ? PgColor.Text.secondary : PgColor.Text.muted))
                .lineLimit(1)
                .minimumScaleFactor(0.8)
                .padding(.horizontal, PgSpace.xxs)
                .frame(maxWidth: .infinity, minHeight: 38)
                .background {
                    if on {
                        RoundedRectangle(cornerRadius: PgRadius.input, style: .continuous)
                            .fill(Color(PgColor.Surface.solid))
                            .pgShadow(PgShadow.card)
                    }
                }
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(on ? [.isButton, .isSelected] : .isButton)
        .accessibilityIdentifier(identifier.isEmpty ? "" : "\(identifier).\(option.value)")
    }

    /// Нажатие на сегмент (app.js .segment): выбранный — ничего; переход с вопросом — сначала диалог, выбор — только после
    /// «да»; иначе — сразу.
    @MainActor
    static func pick(_ value: Value, selection: Value, dialog: PgDialog?, layers: PgLayers?, onSelect: @escaping (Value) -> Void) {
        guard value != selection else {
            return
        }

        PgLayers.confirming(dialog, in: layers) {
            onSelect(value)
        }
    }
}

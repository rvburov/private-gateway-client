import Foundation
import PrivateGatewayTheme
import SwiftUI

/// Вариант выбора для сегментов и выпадающего списка: значение и подпись (из `PgTexts`).
struct PgOption<Value: Hashable>: Identifiable {
    let value: Value
    let title: String

    var id: Value {
        value
    }
}

/// Выбор из списка (.btn-select, Button.select): белая кнопка высотой 36 со скруглением 10 и рамкой line.strong, подпись
/// выбранного small Medium и синяя галочка вниз; нажатие открывает меню у кнопки (.popup) — текущий пункт синим полужирным
/// без заливки. Опасный выбор — через диалог (`confirm`): отметка приходит из `selection` после «да». Меню и диалог рисуют
/// слои окна (`PgLayers`).
struct PgSelect<Value: Hashable>: View {
    let options: [PgOption<Value>]
    let selection: Value
    /// Название для VoiceOver — заголовок строки настройки («Стек TUN»); значение — подпись выбранного.
    var label = ""
    var enabled = true
    var identifier = ""
    var confirm: (Value) -> PgDialog? = { _ in nil }
    let onSelect: (Value) -> Void

    @Environment(\.pgLayers) private var layers
    @State private var anchor = UUID()

    var body: some View {
        Button {
            guard let layers else {
                return
            }

            layers.openMenu(Self.menu(anchor: anchor, options: options, selection: selection, layers: layers, confirm: confirm, onSelect: onSelect))
        } label: {
            HStack(spacing: PgSpace.xs) {
                Text(title)
                    .pgText(PgType.small.with(weight: 500))
                    .foregroundStyle(Color(enabled ? PgColor.Text.primary : PgColor.Text.muted))
                    .lineLimit(1)
                PgIcon(data: PgIcons.chevronDown, color: Color(enabled ? PgColor.Accent.text : PgColor.Text.muted), size: 14, lineWidth: 2.6)
            }
            .padding(.leading, PgSpace.l)
            .padding(.trailing, PgSpace.m)
            .frame(height: 36)
            .contentShape(Rectangle())
        }
        .buttonStyle(PgSelectStyle())
        .disabled(!enabled)
        .opacity(enabled ? 1 : 0.6)
        .pgMenuAnchor(anchor)
        .accessibilityLabel(label.isEmpty ? title : label)
        .accessibilityValue(label.isEmpty ? "" : title)
        .accessibilityIdentifier(identifier)
    }

    private var title: String {
        options.first { $0.value == selection }?.title ?? ""
    }

    /// Меню выбора: пункты по порядку, текущий отмечен; выбран текущий — ничего, другой — сразу или после «да» в диалоге
    /// `confirm`.
    @MainActor
    static func menu(
        anchor: UUID, options: [PgOption<Value>], selection: Value, layers: PgLayers, confirm: @escaping (Value) -> PgDialog?,
        onSelect: @escaping (Value) -> Void
    ) -> PgMenu {
        let values = options.map(\.value)
        let items = options.map { PgMenuItem(title: $0.title, current: $0.value == selection) }
        return PgMenu(anchor: anchor, items: items) { [weak layers] index in
            let value = values[index]
            guard value != selection else {
                return
            }

            PgLayers.confirming(confirm(value), in: layers) {
                onSelect(value)
            }
        }
    }
}

/// Рамка кнопки выбора: line.strong, при нажатии — accent.base (app.css .btn-select:active).
private struct PgSelectStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .background(RoundedRectangle(cornerRadius: PgRadius.input, style: .continuous).fill(Color(PgColor.Surface.solid)))
            .overlay(
                RoundedRectangle(cornerRadius: PgRadius.input, style: .continuous)
                    .strokeBorder(Color(configuration.isPressed ? PgColor.Accent.base : PgColor.Line.strong), lineWidth: 1)
            )
    }
}

/// Меню у кнопки (.popup + .btn-option, Popup десктопа): белое, скругление radius.card, рамка line.border, тень
/// shadow.popup, поля 6, ширина по содержимому, не уже 220; пункт — строка layout.touchTarget, текущий — синим полужирным без
/// заливки, опасный — красным.
struct PgMenuView: View {
    let menu: PgMenu
    let onPick: (Int) -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            ForEach(menu.items.indices, id: \.self) { index in
                let item = menu.items[index]
                Button {
                    onPick(index)
                } label: {
                    Text(item.title)
                        .pgText(item.current ? PgType.body.with(weight: PgType.bodyStrong.weight) : PgType.body)
                        .foregroundStyle(Color(Self.ink(item)))
                        .lineLimit(1)
                        .padding(.horizontal, PgSpace.m)
                        .frame(maxWidth: .infinity, minHeight: PgLayout.touchTarget, alignment: .leading)
                        .contentShape(Rectangle())
                }
                .buttonStyle(PgPressStyle())
                .accessibilityAddTraits(item.current ? [.isButton, .isSelected] : .isButton)
            }
        }
        .padding(PgSpace.xs)
        .frame(minWidth: 220, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: PgRadius.card, style: .continuous).fill(Color(PgColor.Surface.solid)))
        .overlay(RoundedRectangle(cornerRadius: PgRadius.card, style: .continuous).stroke(Color(PgColor.Line.border), lineWidth: 1))
        .pgShadow(PgShadow.popup)
        .accessibilityElement(children: .contain)
        .accessibilityAddTraits(.isModal)
    }

    private static func ink(_ item: PgMenuItem) -> PgRGBA {
        if item.current {
            return PgColor.Accent.text
        }

        return item.danger ? PgColor.Status.Danger.text : PgColor.Text.primary
    }
}

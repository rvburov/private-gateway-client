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

/// Пункт-ссылка под чертой внизу меню выбора («Свой адрес…» у «DNS в туннеле», .btn-plain доски 05): выбранным не
/// становится — открывает свое (лист), выбор приходит из `selection` потом.
struct PgMenuLink {
    let title: String
    let action: () -> Void
}

/// Выбор из списка (.btn-select, Button.select): белая кнопка высотой 36 со скруглением 10 и рамкой line.strong, подпись
/// выбранного small Medium и синяя галочка вниз; нажатие открывает меню у кнопки (.popup) — текущий пункт синим полужирным
/// без заливки. Опасный выбор — через диалог (`confirm`): отметка приходит из `selection` после «да». Меню и диалог рисуют
/// слои окна (`PgLayers`). Выбранного нет среди пунктов (свой адрес DNS) — на кнопке `valueTitle`, в меню текущего нет;
/// `link` — пункт под чертой внизу меню.
struct PgSelect<Value: Hashable>: View {
    let options: [PgOption<Value>]
    let selection: Value
    /// Название для VoiceOver — заголовок строки настройки («Стек TUN»); значение — подпись выбранного.
    var label = ""
    var enabled = true
    var identifier = ""
    var confirm: (Value) -> PgDialog? = { _ in nil }
    var valueTitle: String?
    var link: PgMenuLink?
    let onSelect: (Value) -> Void

    @Environment(\.pgLayers) private var layers
    @State private var anchor = UUID()

    var body: some View {
        Button {
            guard let layers else {
                return
            }

            layers.openMenu(Self.menu(anchor: anchor, options: options, selection: selection, layers: layers, confirm: confirm, link: link, onSelect: onSelect))
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
        valueTitle ?? options.first { $0.value == selection }?.title ?? ""
    }

    /// Меню выбора: пункты по порядку, текущий отмечен; выбран текущий — ничего, другой — сразу или после «да» в диалоге
    /// `confirm`; ссылка `link` — последней, под чертой, открывает свое.
    @MainActor
    static func menu(
        anchor: UUID, options: [PgOption<Value>], selection: Value, layers: PgLayers, confirm: @escaping (Value) -> PgDialog?,
        link: PgMenuLink? = nil, onSelect: @escaping (Value) -> Void
    ) -> PgMenu {
        let values = options.map(\.value)
        var items = options.map { PgMenuItem(title: $0.title, current: $0.value == selection) }
        if let link {
            items.append(PgMenuItem(title: link.title, link: true))
        }

        return PgMenu(anchor: anchor, items: items) { [weak layers] index in
            guard values.indices.contains(index) else {
                link?.action()
                return
            }

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

/// Рамка кнопки выбора: line.strong, при нажатии — accent.base (app.css .btn-select:active). Ее же носит «Сменить ▾» главной.
struct PgSelectStyle: ButtonStyle {
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
/// shadow.popup, поля 6, ширина по содержимому — не уже 220 и не шире 340; над пунктами — надзаголовок, если он есть; пункт —
/// строка layout.touchTarget, текущий — синим полужирным без заливки, опасный — красным; пункт быстрой смены — значок страны,
/// название полужирным, протокол второй строкой и задержка справа; ссылки — под чертой, синим.
struct PgMenuView: View {
    let menu: PgMenu
    let onPick: (Int) -> Void

    var body: some View {
        let firstLink = menu.items.firstIndex(where: \.link)
        VStack(alignment: .leading, spacing: 0) {
            if let title = menu.title {
                Overline(text: title)
                    .padding(.horizontal, PgSpace.m)
                    .padding(.top, PgSpace.xs)
                    .padding(.bottom, PgSpace.xxs)
            }

            ForEach(menu.items.indices, id: \.self) { index in
                let item = menu.items[index]
                if index == firstLink {
                    PgDivider().padding(.horizontal, PgSpace.xs).padding(.vertical, 2)
                }

                Button {
                    onPick(index)
                } label: {
                    row(item)
                }
                .buttonStyle(PgPressStyle())
                .accessibilityAddTraits(item.current ? [.isButton, .isSelected] : .isButton)
            }
        }
        .padding(PgSpace.xs)
        .frame(minWidth: 220, maxWidth: 340, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: PgRadius.card, style: .continuous).fill(Color(PgColor.Surface.solid)))
        .overlay(RoundedRectangle(cornerRadius: PgRadius.card, style: .continuous).stroke(Color(PgColor.Line.border), lineWidth: 1))
        .pgShadow(PgShadow.popup)
        .accessibilityElement(children: .contain)
        .accessibilityAddTraits(.isModal)
    }

    private func row(_ item: PgMenuItem) -> some View {
        HStack(spacing: 0) {
            if let badge = item.badge {
                Text(badge)
                    .pgText(PgTextStyle(size: PgType.overline.size, weight: 700))
                    .foregroundStyle(Color(PgColor.Accent.text))
                    .frame(width: 30, height: 22)
                    .background(RoundedRectangle(cornerRadius: PgRadius.chip, style: .continuous).fill(Color(PgColor.Accent.tint)))
                    .accessibilityHidden(true)
            }

            VStack(alignment: .leading, spacing: 1) {
                Text(item.title)
                    .pgText(Self.style(item))
                    .foregroundStyle(Color(Self.ink(item)))
                    .lineLimit(1)
                if let caption = item.caption {
                    Text(caption).pgText(PgType.caption).foregroundStyle(Color(PgColor.Text.muted)).lineLimit(1)
                }
            }
            .padding(.horizontal, item.badge == nil ? 0 : PgSpace.m)
            .padding(.vertical, item.caption == nil ? 0 : PgSpace.xs)
            .frame(maxWidth: .infinity, alignment: .leading)

            if let trailing = item.trailing {
                Text(trailing).pgText(PgType.mono).foregroundStyle(Color(item.trailingColor ?? PgColor.Text.muted)).lineLimit(1)
            }
        }
        .padding(.horizontal, PgSpace.m)
        .frame(maxWidth: .infinity, minHeight: PgLayout.touchTarget, alignment: .leading)
        .contentShape(Rectangle())
    }

    /// Ссылка — small полужирным (.btn-link), пункт с второй строкой — полужирным всегда (.t-strong), обычный — body,
    /// текущий — полужирным.
    private static func style(_ item: PgMenuItem) -> PgTextStyle {
        if item.link {
            return PgType.small.with(weight: PgType.bodyStrong.weight)
        }

        return item.current || item.caption != nil ? PgType.body.with(weight: PgType.bodyStrong.weight) : PgType.body
    }

    private static func ink(_ item: PgMenuItem) -> PgRGBA {
        if item.current || item.link {
            return PgColor.Accent.text
        }

        return item.danger ? PgColor.Status.Danger.text : PgColor.Text.primary
    }
}

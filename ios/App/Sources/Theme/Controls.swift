import PrivateGatewayTheme
import SwiftUI

/// Кнопка макета: акцентная заливка, скругление radius.button, высота не меньше layout.touchTarget — как `PgButton` Android.
struct PgButton: View {
    let label: String
    var enabled = true
    var identifier = ""
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Text(label)
                .pgText(PgType.bodyStrong)
                .foregroundStyle(Color(PgColor.Surface.solid))
                .frame(maxWidth: .infinity, minHeight: PgLayout.touchTarget)
                .background(RoundedRectangle(cornerRadius: PgRadius.button, style: .continuous).fill(Color(enabled ? PgColor.Accent.base : PgColor.Control.trackOff)))
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .disabled(!enabled)
        .accessibilityIdentifier(identifier)
    }
}

/// Текстовая кнопка акцентного цвета (ссылка-действие): «Готово», «Сбросить», «Поделиться».
struct PgTextAction: View {
    let label: String
    var color = PgColor.Accent.text
    var enabled = true
    var identifier = ""
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Text(label)
                .pgText(PgType.bodyStrong)
                .foregroundStyle(Color(enabled ? color : PgColor.Text.muted))
                .frame(minHeight: PgLayout.touchTarget)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .disabled(!enabled)
        .accessibilityIdentifier(identifier)
    }
}

/// Переключатель с названием и пояснением: дорожка — accent.base или control.trackOff, бегунок — surface.solid.
struct PgToggleRow: View {
    let title: String
    var text: String?
    let isOn: Bool
    var identifier = ""
    let onChange: (Bool) -> Void

    var body: some View {
        Button {
            onChange(!isOn)
        } label: {
            HStack(spacing: PgSpace.m) {
                VStack(alignment: .leading, spacing: 2) {
                    Text(title).pgText(PgType.body).foregroundStyle(Color(PgColor.Text.primary))
                    if let text, !text.isEmpty {
                        Text(text).pgText(PgType.caption).foregroundStyle(Color(PgColor.Text.secondary))
                    }
                }

                Spacer(minLength: 0)
                ZStack(alignment: isOn ? .trailing : .leading) {
                    Capsule().fill(Color(isOn ? PgColor.Accent.base : PgColor.Control.trackOff))
                    Circle().fill(Color(PgColor.Surface.solid)).frame(width: 20, height: 20).padding(2)
                }
                .frame(width: 44, height: 24)
            }
            .frame(minHeight: PgLayout.touchTarget)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(.isButton)
        .accessibilityValue(isOn ? "1" : "0")
        .accessibilityIdentifier(identifier)
    }
}

/// Вариант выбора (один из нескольких): название, пояснение и кружок выбора справа.
struct PgChoiceRow: View {
    let title: String
    var text: String?
    let selected: Bool
    var identifier = ""
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            HStack(spacing: PgSpace.m) {
                VStack(alignment: .leading, spacing: 2) {
                    Text(title).pgText(PgType.body).foregroundStyle(Color(PgColor.Text.primary))
                    if let text, !text.isEmpty {
                        Text(text).pgText(PgType.caption).foregroundStyle(Color(PgColor.Text.secondary))
                    }
                }

                Spacer(minLength: 0)
                ZStack {
                    Circle().stroke(Color(selected ? PgColor.Accent.base : PgColor.Control.checkboxBorder), lineWidth: 2)
                    if selected {
                        Circle().fill(Color(PgColor.Accent.base)).frame(width: 10, height: 10)
                    }
                }
                .frame(width: 20, height: 20)
            }
            .frame(minHeight: PgLayout.touchTarget)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(selected ? [.isButton, .isSelected] : .isButton)
        .accessibilityIdentifier(identifier)
    }
}

/// Заголовок вложенной страницы: «Назад» и название.
struct PgPageHeader: View {
    let title: String
    let texts: PgTexts
    let onBack: () -> Void

    var body: some View {
        HStack(spacing: PgSpace.s) {
            Button(action: onBack) {
                PgIcon(data: PgIcons.back, size: 22, lineWidth: 2)
                    .frame(width: PgLayout.touchTarget, height: PgLayout.touchTarget)
                    .contentShape(Circle())
            }
            .buttonStyle(.plain)
            .accessibilityLabel(texts.commonBack)
            .accessibilityIdentifier("back")

            Text(title).pgText(PgType.h1).foregroundStyle(Color(PgColor.Text.primary)).lineLimit(2)
            Spacer(minLength: 0)
        }
        .padding(.top, PgSpace.l)
    }
}

/// Поле ввода макета: подложка surface.inset, рамка line.border, подсказка цвета text.muted.
struct PgField: View {
    let placeholder: String
    @Binding var text: String
    var style = PgType.body
    var multiline = false
    var identifier = ""

    var body: some View {
        Group {
            if multiline {
                TextField("", text: $text, prompt: prompt, axis: .vertical)
                    .lineLimit(4...12)
            } else {
                TextField("", text: $text, prompt: prompt)
            }
        }
        .pgText(style)
        .foregroundStyle(Color(PgColor.Text.primary))
        .textInputAutocapitalization(.never)
        .autocorrectionDisabled()
        .padding(PgSpace.m)
        .frame(minHeight: multiline ? 96 : PgLayout.touchTarget, alignment: .topLeading)
        .background(RoundedRectangle(cornerRadius: PgRadius.input, style: .continuous).fill(Color(PgColor.Surface.inset)))
        .overlay(RoundedRectangle(cornerRadius: PgRadius.input, style: .continuous).stroke(Color(PgColor.Line.border), lineWidth: 1))
        .accessibilityIdentifier(identifier)
    }

    /// Подсказка — `Text`, поэтому цвет — `foregroundColor` (вариант `foregroundStyle` для `Text` — только с iOS 17).
    private var prompt: Text {
        Text(placeholder).foregroundColor(Color(PgColor.Text.muted))
    }
}

/// Круглая кнопка-иконка в шапке: «+», «Обновить».
struct PgRoundButton: View {
    let icon: String
    let label: String
    var enabled = true
    var identifier = ""
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            PgIcon(data: icon, color: Color(enabled ? PgColor.Text.primary : PgColor.Text.muted), size: 24, lineWidth: 2)
                .frame(width: PgLayout.touchTarget, height: PgLayout.touchTarget)
                .contentShape(Circle())
        }
        .buttonStyle(.plain)
        .disabled(!enabled)
        .accessibilityLabel(label)
        .accessibilityIdentifier(identifier)
    }
}

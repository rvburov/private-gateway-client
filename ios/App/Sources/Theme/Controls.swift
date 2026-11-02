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

/// Кнопка действия (.btn-primary, .btn-secondary; Button.primary и Button.secondary десктопа): основная — заливка
/// accent.base и белый полужирный текст, вторая — белая с рамкой line.strong; опасная — красная (status.danger). Во всю
/// ширину, скругление radius.input, высота не меньше layout.touchTarget; при нажатии — сжатие до 0,98.
struct PgActionButton: View {
    enum Kind {
        case primary
        case secondary
    }

    let label: String
    var kind = Kind.primary
    var danger = false
    var enabled = true
    var identifier = ""
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Text(label)
                .pgText(kind == .primary ? PgType.bodyStrong : PgType.body)
                .foregroundStyle(Color(ink))
                .multilineTextAlignment(.center)
                .padding(.horizontal, kind == .primary ? 18 : 14)
                .frame(maxWidth: .infinity, minHeight: PgLayout.touchTarget)
                .background(RoundedRectangle(cornerRadius: PgRadius.input, style: .continuous).fill(Color(paint)))
                .overlay {
                    if kind == .secondary {
                        RoundedRectangle(cornerRadius: PgRadius.input, style: .continuous).strokeBorder(Color(border), lineWidth: 1)
                    }
                }
                .contentShape(Rectangle())
        }
        .buttonStyle(PgPressStyle())
        .disabled(!enabled)
        .opacity(enabled ? 1 : 0.45)
        .accessibilityIdentifier(identifier)
    }

    private var paint: PgRGBA {
        switch kind {
        case .primary: return danger ? PgColor.Status.Danger.base : PgColor.Accent.base
        case .secondary: return PgColor.Surface.solid
        }
    }

    private var ink: PgRGBA {
        switch kind {
        case .primary: return PgColor.Surface.solid
        case .secondary: return danger ? PgColor.Status.Danger.text : PgColor.Text.primary
        }
    }

    private var border: PgRGBA {
        danger ? PgColor.Status.Danger.line : PgColor.Line.strong
    }
}

/// Нажатие кнопки (app.css :active, :pressed десктопа): сжатие до 0,98, без серой заливки.
struct PgPressStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label.scaleEffect(configuration.isPressed ? 0.98 : 1)
    }
}

/// Переключатель (.switch, ToggleButton.switch): дорожка 48 × 28, бегунок 22 цвета surface.solid с тенью карточки; включен —
/// accent.base, выключен — control.trackOff, недоступен — прозрачность 0,45. Только вид: нажимается строка, в которой он
/// стоит (`PgToggleRow`).
struct PgSwitch: View {
    let isOn: Bool
    var enabled = true

    var body: some View {
        ZStack(alignment: isOn ? .trailing : .leading) {
            Capsule().fill(Color(isOn ? PgColor.Accent.base : PgColor.Control.trackOff))
            Circle()
                .fill(Color(PgColor.Surface.solid))
                .frame(width: 22, height: 22)
                .pgShadow(PgShadow.card)
                .padding(3)
        }
        .frame(width: 48, height: 28)
        .opacity(enabled ? 1 : 0.45)
        .animation(.easeOut(duration: 0.15), value: isOn)
        .accessibilityHidden(true)
    }
}

/// Строка с переключателем — строка настройки (.setting: заголовок полужирным, подпись caption цветом text.muted) с
/// переключателем .switch справа; нажимается вся строка. Опасное включение или выключение — через диалог (data-confirm-on и
/// data-confirm-off макета, ConfirmSwitch десктопа): строка сама не переключается, положение приходит из `isOn` после «да».
/// `padded` — поля 12 × 16 для строки в карточке без своих полей (`pgCard(padding: 0)`, строки разделяет `PgDivider`).
struct PgToggleRow: View {
    let title: String
    var text: String?
    let isOn: Bool
    var enabled = true
    var padded = false
    var confirmOn: PgDialog?
    var confirmOff: PgDialog?
    var identifier = ""
    let onChange: (Bool) -> Void

    @Environment(\.pgLayers) private var layers

    var body: some View {
        Button {
            let next = !isOn
            PgLayers.confirming(next ? confirmOn : confirmOff, in: layers) {
                onChange(next)
            }
        } label: {
            PgSettingRow(title: title, caption: text, enabled: enabled, padded: padded) {
                PgSwitch(isOn: isOn, enabled: enabled)
            }
            .frame(minHeight: PgLayout.touchTarget)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .disabled(!enabled)
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(.isButton)
        .accessibilityValue(isOn ? "1" : "0")
        .accessibilityIdentifier(identifier)
    }
}

/// Флажок (.check, CheckBox.check): квадрат 20 со скруглением 6 и рамкой 1,5 control.checkboxBorder на surface.solid;
/// отмечен — accent.base с белой галочкой Chip.Ok. Нажимается вся строка (высота 40, поля 8), недоступный — серым,
/// прозрачность 0,6.
struct PgCheck: View {
    let title: String
    let isOn: Bool
    var enabled = true
    var identifier = ""
    let onChange: (Bool) -> Void

    var body: some View {
        Button {
            onChange(!isOn)
        } label: {
            HStack(spacing: PgSpace.m) {
                ZStack {
                    RoundedRectangle(cornerRadius: 6, style: .continuous)
                        .fill(Color(isOn ? PgColor.Accent.base : PgColor.Surface.solid))
                    RoundedRectangle(cornerRadius: 6, style: .continuous)
                        .strokeBorder(Color(isOn ? PgColor.Accent.base : PgColor.Control.checkboxBorder), lineWidth: 1.5)
                    if isOn {
                        PgIcon(data: PgIcons.chipOk, color: Color(PgColor.Surface.solid), size: 12, lineWidth: 3)
                    }
                }
                .frame(width: 20, height: 20)

                Text(title)
                    .pgText(PgType.body)
                    .foregroundStyle(Color(enabled ? PgColor.Text.primary : PgColor.Text.muted))
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            .padding(.horizontal, PgSpace.s)
            .frame(minHeight: 40)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .disabled(!enabled)
        .opacity(enabled ? 1 : 0.6)
        .accessibilityAddTraits(isOn ? [.isButton, .isSelected] : .isButton)
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

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
/// ширину, скругление radius.input, высота не меньше layout.touchTarget; при нажатии — сжатие до 0,98. Крупная кнопка
/// (.sc-connect карточки сервера, .im-add «Добавить профиля») — своя высота `height`, начертание `style` и скругление
/// `radius`.
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
    var height: Double = PgLayout.touchTarget
    var style: PgTextStyle?
    var radius: Double = PgRadius.input
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Text(label)
                .pgText(style ?? (kind == .primary ? PgType.bodyStrong : PgType.body))
                .foregroundStyle(Color(ink))
                .multilineTextAlignment(.center)
                .padding(.horizontal, kind == .primary ? 18 : 14)
                .frame(maxWidth: .infinity, minHeight: height)
                .background(RoundedRectangle(cornerRadius: radius, style: .continuous).fill(Color(paint)))
                .overlay {
                    if kind == .secondary {
                        RoundedRectangle(cornerRadius: radius, style: .continuous).strokeBorder(Color(border), lineWidth: 1)
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

/// Значок, который крутится, пока идет работа (data-spin макета, Classes.spin десктопа): оборот за 0,9 с; кнопка вокруг него
/// не меняется и не гаснет (.btn-icon.steady) — «Обновить серверы» в «Серверах» и на главной.
struct PgSpinningIcon: View {
    let data: String
    let spinning: Bool
    var color = Color(PgColor.Text.primary)
    var size: CGFloat = 20
    var lineWidth: CGFloat = 1.8

    var body: some View {
        if spinning {
            TimelineView(.animation) { context in
                let turn = context.date.timeIntervalSinceReferenceDate.truncatingRemainder(dividingBy: 0.9) / 0.9
                PgIcon(data: data, color: color, size: size, lineWidth: lineWidth)
                    .rotationEffect(.degrees(turn * 360))
            }
        } else {
            PgIcon(data: data, color: color, size: size, lineWidth: lineWidth)
        }
    }
}

/// Небольшая кнопка в строке (.btn-small, Button.small десктопа): высота 36, поля 12, скругление 10, белая с рамкой
/// line.strong, текст small; мягкая (`soft`, .btn-small.soft) — подложка accent.tint, рамка accent.line, текст accent.text
/// полужирным. Значок 15 слева может крутиться (`spinning`, data-spin): пока крутится, кнопка не меняется и нажатия не
/// принимает — работа уже идет. Недоступная — серым текстом, прозрачность 0,6 (решение 530).
struct PgSmallButton: View {
    let label: String
    var icon: String?
    var soft = false
    var spinning = false
    var enabled = true
    var identifier = ""
    let action: () -> Void

    var body: some View {
        let ink = enabled ? (soft ? PgColor.Accent.text : PgColor.Text.primary) : PgColor.Text.muted
        let shape = RoundedRectangle(cornerRadius: 10, style: .continuous)
        Button(action: action) {
            HStack(spacing: PgSpace.xs) {
                if let icon {
                    PgSpinningIcon(data: icon, spinning: spinning, color: Color(ink), size: 15, lineWidth: 2.2)
                }

                Text(label)
                    .pgText(soft ? PgType.small.with(weight: PgType.bodyStrong.weight) : PgType.small)
                    .foregroundStyle(Color(ink))
                    .lineLimit(1)
            }
            .padding(.horizontal, PgSpace.l)
            .frame(minHeight: 36)
            .background(shape.fill(Color(soft ? PgColor.Accent.tint : PgColor.Surface.solid)))
            .overlay(shape.strokeBorder(Color(soft ? PgColor.Accent.line : PgColor.Line.strong), lineWidth: 1))
            .contentShape(Rectangle())
        }
        .buttonStyle(PgPressStyle())
        .disabled(!enabled)
        .allowsHitTesting(!spinning)
        .opacity(enabled ? 1 : 0.6)
        .accessibilityIdentifier(identifier)
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
/// `padded` — поля 12 × 16 для строки в карточке без своих полей (`pgCard(padding: 0)`, строки разделяет `PgDivider`);
/// `warning` — третья строка подписи цветом status.warning.badge: почему настройка сейчас не действует.
struct PgToggleRow: View {
    let title: String
    var text: String?
    var warning: String?
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
            PgSettingRow(title: title, caption: text, warning: warning, enabled: enabled, padded: padded) {
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

/// Кнопка справа в шапке подстраницы: значок, подпись для VoiceOver и действие («+» — «Добавить подписку»).
struct PgHeaderAction {
    let icon: String
    let label: String
    var identifier = ""
    let action: () -> Void
}

/// Заголовок вложенной страницы: «Назад», название и необязательная кнопка справа (`action`). Неподвижная шапка над
/// прокруткой (`fixed`, .pg-page-header, .im-head и .sb-head доски): высота 60, поля 8 по краям, зазор 4, «Назад» со значком 22
/// линией 2,2, название h1; иначе — первая строка прокручиваемой колонки, поля дает колонка.
struct PgPageHeader: View {
    let title: String
    let texts: PgTexts
    var action: PgHeaderAction?
    var fixed = false
    let onBack: () -> Void

    var body: some View {
        HStack(spacing: fixed ? PgSpace.xxs : PgSpace.s) {
            Button(action: onBack) {
                PgIcon(data: PgIcons.back, size: 22, lineWidth: fixed ? 2.2 : 2)
                    .frame(width: PgLayout.touchTarget, height: PgLayout.touchTarget)
                    .contentShape(Circle())
            }
            .buttonStyle(.plain)
            .accessibilityLabel(texts.commonBack)
            .accessibilityIdentifier("back")

            Text(title)
                .pgText(PgType.h1)
                .foregroundStyle(Color(PgColor.Text.primary))
                .lineLimit(2)
                .minimumScaleFactor(fixed ? 0.8 : 1)
                .accessibilityAddTraits(.isHeader)
            Spacer(minLength: 0)
            if let action {
                Button(action: action.action) {
                    PgIcon(data: action.icon, size: 22, lineWidth: 2)
                        .frame(width: PgLayout.touchTarget, height: PgLayout.touchTarget)
                        .contentShape(Rectangle())
                }
                .buttonStyle(PgPressStyle())
                .accessibilityLabel(action.label)
                .accessibilityIdentifier(action.identifier)
            }
        }
        .padding(.top, fixed ? 0 : PgSpace.l)
        .padding(.leading, fixed ? PgSpace.s : 0)
        .padding(.trailing, fixed ? (action == nil ? PgSpace.windowSideMobile : PgSpace.s) : 0)
        .frame(minHeight: fixed ? 60 : nil)
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

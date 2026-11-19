import PrivateGatewayCore
import PrivateGatewayTheme
import SwiftUI

/// PIN-код блока сайтов для экранов (решение 742) — `BlockPinControl` Android: текущий код, запись, проверка с паузой и сброс
/// входом в аккаунт. `reset` — nil, когда сбрасывать нечем (тесты экранов, восстановление копии): «Забыли PIN-код?» не видно.
struct BlockPinControl {
    var pin: BlockPin?
    var set: (BlockPin?) -> Void
    var check: (String) -> PinCheck
    var reset: ((_ email: String, _ password: String, _ code: String?) async -> LoginResult)?
    var email = ""

    /// Без PIN-кода: ослабить блок можно сразу.
    static var none: BlockPinControl {
        BlockPinControl(pin: nil, set: { _ in }, check: { _ in .right })
    }

    /// Тот же код без сброса.
    var withoutReset: BlockPinControl {
        var control = self
        control.reset = nil
        return control
    }
}

/// Что спросить кодом: заголовок и текст листа и действие после верного кода.
struct PinAsk {
    let title: String
    let text: String
    let allow: () -> Void

    /// Лист по виду ослабления блока (`BlockGuard`): адрес — в заголовок.
    static func of(_ kind: BlockGuard, _ texts: PgTexts, allow: @escaping () -> Void) -> PinAsk {
        PinAsk(
            title: texts.text(key: kind.titleKey, [kind.pattern ?? ""]) ?? "", text: texts.text(key: kind.textKey) ?? "", allow: allow
        )
    }
}

/// Шаг листа PIN-кода: спросить код, сбросить входом в аккаунт или задать новый (после сброса — со строкой «PIN-код сброшен…»).
enum PinStep {
    case prompt(PinAsk)
    case reset
    case setup(notice: Bool)
}

/// Лист PIN-кода — все три шага в одном листе: «Забыли PIN-код?» и верный код для смены не закрывают лист, а меняют шаг.
struct BlockPinSheet: View {
    let texts: PgTexts
    let control: BlockPinControl
    @Binding var step: PinStep?

    var body: some View {
        switch step {
        case let .prompt(ask)?:
            BlockPinPrompt(texts: texts, ask: ask, control: control, onForgot: { step = .reset }, onClose: { step = nil }) {
                step = nil
                ask.allow()
            }
        case .reset?:
            BlockPinReset(texts: texts, control: control, onDone: { step = .setup(notice: true) }, onClose: { step = nil })
        case let .setup(notice)?:
            BlockPinSetup(texts: texts, control: control, notice: notice) { step = nil }
        case nil:
            EmptyView()
        }
    }
}

/// Карточка «Блок сайтов → PIN-код»: «Задан: без него блок не снять» или «Не задан…» и «Изменить» / «Задать».
struct BlockPinCard: View {
    let texts: PgTexts
    let set: Bool
    let onChange: () -> Void

    var body: some View {
        HStack(spacing: PgSpace.m) {
            VStack(alignment: .leading, spacing: 3) {
                Text(texts.blockPinTitle).pgText(PgType.bodyStrong).foregroundStyle(Color(PgColor.Text.primary))
                Text(set ? texts.blockPinOn : texts.blockPinOff)
                    .pgText(PgType.caption)
                    .foregroundStyle(Color(PgColor.Text.muted))
                    .fixedSize(horizontal: false, vertical: true)
                    .accessibilityIdentifier("block.pin.state")
            }
            .frame(maxWidth: .infinity, alignment: .leading)

            PgActionButton(label: set ? texts.blockPinChangeButton : texts.blockPinSetButton, kind: .secondary, identifier: "block.pin.change", action: onChange)
                .fixedSize()
        }
        .padding(.vertical, PgSpace.l)
        .padding(.horizontal, PgSpace.card)
        .pgCard(padding: 0)
        .accessibilityIdentifier("block.pin")
    }
}

/// «Снять блок с …?» (макет v2): текст, поле PIN-кода, «Забыли PIN-код?», «Оставить блок» и красная «Снять блок». Пять неверных
/// подряд — минута без попыток.
private struct BlockPinPrompt: View {
    let texts: PgTexts
    let ask: PinAsk
    let control: BlockPinControl
    let onForgot: () -> Void
    let onClose: () -> Void
    let onAllowed: () -> Void

    @State private var pin = ""
    @State private var error: String?

    var body: some View {
        PgSheetHeader(title: ask.title, closeLabel: texts.cardClose, identifier: "block.pin.close", onClose: onClose)
        ScrollView {
            VStack(alignment: .leading, spacing: PgSpace.l) {
                Text(ask.text).pgText(PgType.small).foregroundStyle(Color(PgColor.Text.secondary)).fixedSize(horizontal: false, vertical: true)
                PinField(label: texts.blockPinTitle, shown: false, centered: true, value: $pin, identifier: "block.pin.field", onSubmit: confirm)
                if let error {
                    Text(error).pgText(PgType.caption).foregroundStyle(Color(PgColor.Status.Danger.text)).accessibilityIdentifier("block.pin.error")
                }

                if control.reset != nil {
                    Button(action: onForgot) {
                        Text(texts.blockPinForgot).pgText(PgType.bodyStrong).foregroundStyle(Color(PgColor.Accent.text)).frame(minHeight: 40)
                    }
                    .buttonStyle(.plain)
                    .accessibilityIdentifier("block.pin.forgot")
                }
            }
            .padding(.vertical, PgSpace.card)
        }

        HStack(spacing: PgSpace.s) {
            PgActionButton(label: texts.blockPinKeep, identifier: "block.pin.keep", height: 50, action: onClose)
            PgActionButton(label: texts.blockPinUnblock, kind: .secondary, danger: true, enabled: !pin.isEmpty, identifier: "block.pin.confirm", height: 50, action: confirm)
        }
    }

    private func confirm() {
        guard !pin.isEmpty else { return }
        let check = control.check(pin)
        pin = ""
        switch check {
        case .right: onAllowed()
        case .wrong: error = texts.blockPinWrong
        case .paused: error = texts.blockPinPaused
        }
    }
}

/// «Сбросить PIN-код» (макет v2): вход тем же аккаунтом, что в приложении; администратору — второй шаг с кодом 2FA. Вошли —
/// код снят, блоки остались, следующий шаг — задать новый.
private struct BlockPinReset: View {
    let texts: PgTexts
    let control: BlockPinControl
    let onDone: () -> Void
    let onClose: () -> Void

    @State private var email: String
    @State private var password = ""
    @State private var code = ""
    @State private var codeStep = false
    @State private var busy = false
    @State private var error: AccountError?
    @State private var minutes = 0

    init(texts: PgTexts, control: BlockPinControl, onDone: @escaping () -> Void, onClose: @escaping () -> Void) {
        self.texts = texts
        self.control = control
        self.onDone = onDone
        self.onClose = onClose
        _email = State(initialValue: control.email)
    }

    var body: some View {
        PgSheetHeader(title: texts.blockPinResetTitle, closeLabel: texts.cardClose, identifier: "block.reset.close", onClose: onClose)
        ScrollView {
            VStack(alignment: .leading, spacing: PgSpace.l) {
                Text(texts.blockPinResetText).pgText(PgType.small).foregroundStyle(Color(PgColor.Text.secondary)).fixedSize(horizontal: false, vertical: true)
                if codeStep {
                    field(texts.accountCode) {
                        TextField("", text: Binding(get: { code }, set: { code = String($0.prefix(LoginForm.recoveryMax)) }))
                            .keyboardType(.numberPad)
                            .textContentType(.oneTimeCode)
                            .accessibilityIdentifier("block.reset.code")
                    }
                } else {
                    field(texts.accountEmail) {
                        TextField("", text: Binding(get: { email }, set: { email = String($0.prefix(LoginForm.emailMax)) }))
                            .keyboardType(.emailAddress)
                            .textContentType(.username)
                            .accessibilityIdentifier("block.reset.email")
                    }
                    field(texts.accountPassword) {
                        SecureField("", text: Binding(get: { password }, set: { password = String($0.prefix(LoginForm.passwordMax)) }))
                            .textContentType(.password)
                            .accessibilityIdentifier("block.reset.password")
                    }
                }

                if let error {
                    Text(LoginLook.error(error, minutes: minutes, texts))
                        .pgText(PgType.caption)
                        .foregroundStyle(Color(PgColor.Status.Danger.text))
                        .fixedSize(horizontal: false, vertical: true)
                        .accessibilityIdentifier("block.reset.error")
                }
            }
            .padding(.vertical, PgSpace.card)
        }

        HStack(spacing: PgSpace.s) {
            PgActionButton(label: texts.dialogCancel, kind: .secondary, identifier: "block.reset.cancel", height: 50, action: onClose)
            PgActionButton(
                label: texts.blockPinResetButton, enabled: !busy && !email.trimmingCharacters(in: .whitespaces).isEmpty && !password.isEmpty,
                identifier: "block.reset.submit", height: 50, action: submit
            )
        }
    }

    private func field<Input: View>(_ title: String, @ViewBuilder input: () -> Input) -> some View {
        VStack(alignment: .leading, spacing: PgSpace.xs) {
            Text(title).pgText(PgType.small.with(weight: PgType.bodyStrong.weight)).foregroundStyle(Color(PgColor.Text.primary))
            input()
                .pgText(PgType.body)
                .foregroundStyle(Color(PgColor.Text.primary))
                .textInputAutocapitalization(.never)
                .autocorrectionDisabled()
                .padding(.horizontal, PgSpace.m)
                .frame(minHeight: 44)
                .background(RoundedRectangle(cornerRadius: PgRadius.input, style: .continuous).fill(Color(PgColor.Surface.solid)))
                .overlay(RoundedRectangle(cornerRadius: PgRadius.input, style: .continuous).strokeBorder(Color(PgColor.Line.strong), lineWidth: 1))
                .accessibilityLabel(title)
        }
    }

    private func submit() {
        guard let reset = control.reset, !busy else { return }
        busy = true
        error = nil
        let entered = codeStep ? code.trimmingCharacters(in: .whitespaces) : nil
        Task {
            let result = await reset(email, password, entered)
            busy = false
            code = ""
            switch result {
            case .ok:
                password = ""
                control.set(nil)
                onDone()
            case .codeRequired:
                codeStep = true
            case let .failed(failure, retry):
                error = failure
                minutes = retry
            }
        }
    }
}

/// «PIN-код для снятия блока» (макет v2): что закрывает код, «PIN-код · 4–8 цифр», «Еще раз», «Задать PIN-код» и «Отмена»;
/// у заданного кода — «Убрать PIN-код»; после сброса — строка «PIN-код сброшен…».
private struct BlockPinSetup: View {
    let texts: PgTexts
    let control: BlockPinControl
    let notice: Bool
    let onClose: () -> Void

    @State private var pin = ""
    @State private var repeated = ""
    @State private var error: String?

    var body: some View {
        PgSheetHeader(title: texts.blockPinSetupTitle, closeLabel: texts.cardClose, identifier: "block.setup.close", onClose: onClose)
        ScrollView {
            VStack(alignment: .leading, spacing: PgSpace.l) {
                if notice {
                    Text(texts.blockPinResetDone)
                        .pgText(PgType.small)
                        .foregroundStyle(Color(PgColor.Status.Success.badge))
                        .fixedSize(horizontal: false, vertical: true)
                        .accessibilityIdentifier("block.setup.notice")
                }

                Text(texts.blockPinSetupText).pgText(PgType.small).foregroundStyle(Color(PgColor.Text.secondary)).fixedSize(horizontal: false, vertical: true)
                PinField(label: texts.blockPinField, value: $pin, identifier: "block.setup.pin", onSubmit: {})
                PinField(label: texts.blockPinRepeat, value: $repeated, identifier: "block.setup.repeat", onSubmit: save)
                if let error {
                    Text(error).pgText(PgType.caption).foregroundStyle(Color(PgColor.Status.Danger.text)).accessibilityIdentifier("block.setup.error")
                }

                if control.pin != nil {
                    PgActionButton(label: texts.blockPinRemove, kind: .secondary, danger: true, identifier: "block.setup.remove") {
                        control.set(nil)
                        onClose()
                    }
                }
            }
            .padding(.vertical, PgSpace.card)
        }

        HStack(spacing: PgSpace.s) {
            PgActionButton(label: texts.dialogCancel, kind: .secondary, identifier: "block.setup.cancel", height: 50, action: onClose)
            PgActionButton(label: texts.blockPinSave, identifier: "block.setup.save", height: 50, action: save)
        }
    }

    private func save() {
        guard BlockPin.valid(pin) else {
            error = texts.blockPinInvalid
            return
        }

        guard pin == repeated, let created = BlockPin.create(pin) else {
            error = texts.blockPinMismatch
            return
        }

        control.set(created)
        pin = ""
        repeated = ""
        onClose()
    }
}

/// Поле PIN-кода (макет v2): моноширинное, только цифры 0–9, не больше 8, точками; подпись над ним — в листе «Задать»
/// (`shown`), в листе «Снять блок» поле одно и без подписи, по центру.
private struct PinField: View {
    let label: String
    var shown = true
    var centered = false
    @Binding var value: String
    let identifier: String
    let onSubmit: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: PgSpace.xs) {
            if shown {
                Text(label).pgText(PgType.small.with(weight: PgType.bodyStrong.weight)).foregroundStyle(Color(PgColor.Text.primary))
            }

            SecureField("", text: Binding(get: { value }, set: { value = String($0.filter { ("0" ... "9").contains($0) }.prefix(BlockPin.maxLength)) }))
                .keyboardType(.numberPad)
                .textContentType(.oneTimeCode)
                .multilineTextAlignment(centered ? .center : .leading)
                .pgText(PgTextStyle(size: PgType.body.size, weight: PgType.body.weight, mono: true))
                .foregroundStyle(Color(PgColor.Text.primary))
                .padding(.horizontal, PgSpace.m)
                .frame(minHeight: 44)
                .background(RoundedRectangle(cornerRadius: PgRadius.input, style: .continuous).fill(Color(PgColor.Surface.solid)))
                .overlay(RoundedRectangle(cornerRadius: PgRadius.input, style: .continuous).strokeBorder(Color(PgColor.Line.strong), lineWidth: 1))
                .submitLabel(.done)
                .onSubmit(onSubmit)
                .accessibilityLabel(label)
                .accessibilityIdentifier(identifier)
        }
    }
}

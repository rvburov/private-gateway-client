import PrivateGatewayCore
import PrivateGatewayTheme
import SwiftUI

/// Вход в аккаунт сайта (доска 10, решение 621) — `LoginScreen` Android: до любого другого экрана и до приветствия первого
/// запуска; островка нет. Знак, заголовок и пояснение; почему вход нужен снова (доступ отозван или срок истек). Два шага
/// (2FA — только у администраторов): почта, пароль с «глазом» и «Войти»; если сайт ответил «нужен код» — «Код подтверждения
/// администратора»: код 2FA или резервный код, «Войти» и «Войти другим аккаунтом». «Войти» — «Вход…» и недоступна, пока идет
/// запрос; «Готово» на клавиатуре в последнем поле шага отправляет форму. Ошибка — текстом под формой; у 403 и 409 — кнопка
/// на страницу сайта (решение 626). Сегментов языка нет: язык приложения iOS выбирает в своих Настройках.
struct LoginView: View {
    let texts: PgTexts
    @ObservedObject var account: AccountModel

    @State private var shown = false
    @FocusState private var focus: Field?
    @Environment(\.openURL) private var openURL

    private enum Field {
        case email
        case password
        case code
    }

    var body: some View {
        let form = account.form
        ScrollViewReader { scroll in
            ScrollView {
                VStack(alignment: .leading, spacing: 0) {
                    // Знак на плитке 72 (.login-logo макета): радиус panel, подложка surface.solid, рамка и тень карточки.
                    ZStack {
                        PgIcon(data: PgIcons.shield, color: Color(PgColor.Accent.base), size: 36, lineWidth: 1.9)
                        PgIcon(data: PgIcons.markOk, color: Color(PgColor.Accent.base), size: 36, lineWidth: 1.9)
                    }
                    .frame(width: 72, height: 72)
                    .background(RoundedRectangle(cornerRadius: PgRadius.panel, style: .continuous).fill(Color(PgColor.Surface.solid)))
                    .overlay(RoundedRectangle(cornerRadius: PgRadius.panel, style: .continuous).strokeBorder(Color(PgColor.Line.border), lineWidth: 1))
                    .pgShadow(PgShadow.card)
                    .frame(maxWidth: .infinity)
                    .padding(.top, 28)
                    .accessibilityHidden(true)

                    Text(form.codeStep ? texts.accountCodeTitle : texts.accountTitle)
                        .pgText(PgType.h1)
                        .foregroundStyle(Color(PgColor.Text.primary))
                        .multilineTextAlignment(.center)
                        .frame(maxWidth: .infinity)
                        .padding(.top, PgSpace.card)
                        .accessibilityAddTraits(.isHeader)
                        .accessibilityIdentifier("login.title")
                    Text(form.codeStep ? texts.accountCodeText(form.email.trimmingCharacters(in: .whitespaces)) : texts.accountText)
                        .pgText(PgType.body)
                        .foregroundStyle(Color(PgColor.Text.secondary))
                        .multilineTextAlignment(.center)
                        .fixedSize(horizontal: false, vertical: true)
                        .frame(maxWidth: 360)
                        .frame(maxWidth: .infinity)
                        .padding(.top, PgSpace.l)

                    if let notice = account.state?.notice {
                        noticeView(notice).padding(.top, PgSpace.card)
                    }
                    if account.cleanupPending {
                        PgBanner(icon: PgIcons.warning, identifier: "login.cleanup.pending") {
                            Text(texts.text(key: "account.cleanupPending") ?? "")
                                .pgText(PgType.small).foregroundStyle(Color(PgColor.Status.Warning.badge))
                                .fixedSize(horizontal: false, vertical: true)
                        }
                        .padding(.top, PgSpace.card)
                        PgActionButton(
                            label: texts.text(key: account.restoringNetwork ? "account.restoringNetwork" : "account.cleanupRetry") ?? "",
                            kind: .secondary, enabled: !account.signingOut && !account.restoringNetwork,
                            identifier: "login.cleanup.retry", action: account.retryCleanup
                        )
                        .padding(.top, PgSpace.m)
                    }

                    Group {
                        if form.codeStep {
                            codeStep(form)
                        } else {
                            firstStep(form)
                        }
                    }
                    .padding(.top, 20)

                    if let error = form.error, !account.cleanupPending {
                        Text(LoginLook.error(error, minutes: form.retryMinutes, texts))
                            .pgText(PgType.small)
                            .foregroundStyle(Color(PgColor.Status.Danger.text))
                            .fixedSize(horizontal: false, vertical: true)
                            .padding(.top, PgSpace.l)
                            .accessibilityIdentifier("login.error")
                    }

                    PgActionButton(
                        label: account.signingOut ? (texts.text(key: "account.signingOut") ?? "") : account.restoringNetwork
                            ? (texts.text(key: "account.restoringNetwork") ?? "") : form.busy ? texts.accountSigningIn : texts.accountSignIn,
                        enabled: form.canSubmit && !account.signingOut && !account.restoringNetwork, identifier: "login.submit", height: 50,
                        action: account.submit
                    )
                    .padding(.top, PgSpace.l)
                    .id("submit")

                    if let page = LoginLook.page(form.error, texts) {
                        PgActionButton(label: page.label, kind: .secondary, identifier: "login.site", height: 46) {
                            openURL(page.url)
                        }
                        .padding(.top, PgSpace.l)
                    }

                    if form.codeStep {
                        Button(action: account.back) {
                            Text(texts.accountCodeBack)
                                .pgText(PgType.small)
                                .foregroundStyle(Color(form.busy ? PgColor.Text.muted : PgColor.Accent.base))
                                .padding(.vertical, PgSpace.m)
                                .padding(.horizontal, PgSpace.s)
                                .frame(minHeight: PgLayout.touchTarget)
                        }
                        .buttonStyle(.plain)
                        .disabled(form.busy)
                        .frame(maxWidth: .infinity)
                        .padding(.top, PgSpace.xs)
                        .accessibilityIdentifier("login.back")
                    }
                }
                .padding(.horizontal, PgSpace.windowSideMobile + 4)
                .padding(.top, 20)
                .padding(.bottom, 24)
            }
            .onChange(of: focus) { field in
                // Последнее поле шага: на экране должны быть и «Войти», и ошибка под формой, а не только само поле.
                if field == .password || field == .code {
                    withAnimation { scroll.scrollTo("submit", anchor: .bottom) }
                }
            }
        }
        .onChange(of: account.form.codeStep) { codeStep in
            // Второй шаг открылся — код вводят сразу.
            if codeStep {
                focus = .code
            }
        }
        .accessibilityIdentifier("login")
    }

    private func firstStep(_ form: LoginForm) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            label(texts.accountEmail)
            TextField("", text: binding(form.email, account.setEmail), prompt: prompt(texts.accountEmail))
                .keyboardType(.emailAddress)
                .textContentType(.username)
                .submitLabel(.next)
                .focused($focus, equals: .email)
                .onSubmit { focus = .password }
                .modifier(LoginInput(focused: focus == .email))
                .accessibilityLabel(texts.accountEmail)
                .accessibilityIdentifier("login.email")

            label(texts.accountPassword).padding(.top, PgSpace.l)
            // «Глаз» справа внутри рамки поля (.login-secret макета), рамка фокуса — у всего поля.
            HStack(spacing: PgSpace.xxs) {
                Group {
                    if shown {
                        TextField("", text: binding(form.password, account.setPassword), prompt: prompt(texts.accountPassword))
                    } else {
                        SecureField("", text: binding(form.password, account.setPassword), prompt: prompt(texts.accountPassword))
                    }
                }
                .textContentType(.password)
                .submitLabel(.done)
                .focused($focus, equals: .password)
                .onSubmit(account.submit)
                .accessibilityLabel(texts.accountPassword)
                .accessibilityIdentifier("login.password")

                Button {
                    shown.toggle()
                } label: {
                    PgIcon(data: shown ? PgIcons.eyeOff : PgIcons.eye, color: Color(PgColor.Text.muted), size: 18, lineWidth: 1.9)
                        .frame(width: PgLayout.touchTarget, height: PgLayout.touchTarget)
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .accessibilityLabel(texts.accountPasswordShow)
                .accessibilityAddTraits(shown ? .isSelected : [])
                .accessibilityIdentifier("login.password.show")
            }
            .modifier(LoginInput(focused: focus == .password, trailing: PgSpace.xxs))
        }
    }

    private func codeStep(_ form: LoginForm) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            let title = form.recovery ? texts.accountRecovery : texts.accountCode
            label(title)
            TextField("", text: binding(form.code, account.setCode), prompt: prompt(title))
                .keyboardType(form.recovery ? .asciiCapable : .numberPad)
                .textContentType(form.recovery ? nil : .oneTimeCode)
                .submitLabel(.done)
                .focused($focus, equals: .code)
                .onSubmit(account.submit)
                .modifier(LoginInput(focused: focus == .code))
                .accessibilityLabel(title)
                .accessibilityIdentifier("login.code")
            Text(form.recovery ? texts.accountRecoveryHint : texts.accountCodeHint)
                .pgText(PgType.caption)
                .foregroundStyle(Color(PgColor.Text.muted))
                .padding(.top, PgSpace.xs)
                .accessibilityIdentifier("login.code.hint")
            Button(action: account.toggleRecovery) {
                Text(form.recovery ? texts.accountCodeUse : texts.accountRecoveryUse)
                    .pgText(PgType.small)
                    .foregroundStyle(Color(PgColor.Accent.base))
                    .frame(minHeight: PgLayout.touchTarget)
            }
            .buttonStyle(.plain)
            .padding(.top, PgSpace.xs)
            .accessibilityIdentifier("login.recovery.toggle")
        }
    }

    private func label(_ text: String) -> some View {
        Text(text)
            .pgText(PgType.small.with(weight: PgType.bodyStrong.weight))
            .foregroundStyle(Color(PgColor.Text.primary))
            .padding(.bottom, PgSpace.xs)
            .accessibilityHidden(true)
    }

    private func prompt(_ text: String) -> Text {
        Text(text).foregroundColor(Color(PgColor.Text.muted))
    }

    /// Поле формы: значение — из модели, ввод — через ее правило (длины сайта, только цифры у кода 2FA).
    private func binding(_ value: String, _ set: @escaping (String) -> Void) -> Binding<String> {
        Binding(get: { value }, set: set)
    }

    /// Почему вход нужен снова — плашкой предупреждения над формой.
    private func noticeView(_ notice: AccountNotice) -> some View {
        HStack(alignment: .top, spacing: PgSpace.m) {
            PgIcon(data: PgIcons.warning, color: Color(PgColor.Status.Warning.text), size: 18, lineWidth: 2)
            Text(texts.text(key: notice.textKey) ?? "")
                .pgText(PgType.small)
                .foregroundStyle(Color(PgColor.Status.Warning.text))
                .fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 0)
        }
        .padding(.vertical, PgSpace.m)
        .padding(.horizontal, PgSpace.l)
        .background(RoundedRectangle(cornerRadius: PgRadius.input, style: .continuous).fill(Color(PgColor.Status.Warning.tint)))
        .accessibilityElement(children: .combine)
        .accessibilityIdentifier("login.notice")
    }
}

/// Поле формы входа (.input макета): высота 44, подложка surface.solid, рамка line.strong, в фокусе — accent.base 1,5.
private struct LoginInput: ViewModifier {
    let focused: Bool
    var trailing: Double = PgSpace.m

    func body(content: Content) -> some View {
        content
            .pgText(PgType.body)
            .foregroundStyle(Color(PgColor.Text.primary))
            .textInputAutocapitalization(.never)
            .autocorrectionDisabled()
            .padding(.leading, PgSpace.m)
            .padding(.trailing, trailing)
            .frame(minHeight: 44)
            .background(RoundedRectangle(cornerRadius: PgRadius.input, style: .continuous).fill(Color(PgColor.Surface.solid)))
            .overlay(
                RoundedRectangle(cornerRadius: PgRadius.input, style: .continuous)
                    .strokeBorder(Color(focused ? PgColor.Accent.base : PgColor.Line.strong), lineWidth: focused ? 1.5 : 1)
            )
    }
}

/// Правила экрана входа — тексты ошибок и кнопка на страницу сайта, как у Android.
enum LoginLook {
    /// Текст ошибки входа; у «Слишком много попыток» — через сколько минут (не меньше 1).
    static func error(_ error: AccountError, minutes: Int, _ texts: PgTexts) -> String {
        error == .rateLimited ? texts.accountErrorRateLimited(max(1, minutes)) : texts.text(key: error.textKey) ?? texts.accountErrorServer
    }

    /// Кнопка под ошибкой: 403 — 2FA администратора настраивается на сайте, 409 — устройства на странице устройств сайта.
    static func page(_ error: AccountError?, _ texts: PgTexts) -> (label: String, url: URL)? {
        switch error {
        case .setupRequired?:
            return (texts.accountSite, AccountAnswers.site.appendingPathComponent(String(AccountAnswers.loginPage.dropFirst())))
        case .deviceLimit?, .deviceChangeLimit?:
            return (texts.accountDevices, AccountAnswers.site.appendingPathComponent(String(AccountAnswers.devicesPage.dropFirst())))
        default:
            return nil
        }
    }
}

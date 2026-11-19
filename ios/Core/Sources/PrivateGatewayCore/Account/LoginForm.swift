/// Форма входа в два шага (2FA — только у администраторов) — `LoginUi` Android: почта и пароль, а если сайт ответил «нужен
/// код» — второй шаг `codeStep` с кодом 2FA или резервным кодом (`recovery`). Еще — идет ли запрос и почему вход не выполнен
/// (`error`, у «Слишком много попыток» — минуты `retryMinutes`). Пароль и код живут только здесь, в памяти окна: пароль
/// первого шага на втором ждет кода.
public struct LoginForm: Equatable, Sendable {
    public static let codeLength = 6
    /// Длины полей у сайта.
    public static let recoveryMax = 64
    public static let emailMax = 320
    public static let passwordMax = 4096

    public private(set) var email = ""
    public private(set) var password = ""
    public private(set) var code = ""
    public private(set) var recovery = false
    public private(set) var busy = false
    public private(set) var error: AccountError?
    public private(set) var retryMinutes = 0
    public private(set) var codeStep = false

    public init(email: String = "") {
        self.email = String(email.prefix(Self.emailMax))
    }

    /// Поля шага заполнены: почта и пароль; на втором шаге еще код 2FA — ровно 6 цифр, резервный — не пустой.
    public var ready: Bool {
        !email.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && !password.isEmpty
            && (!codeStep || (recovery ? !code.trimmingCharacters(in: .whitespaces).isEmpty : code.count == Self.codeLength))
    }

    /// Можно отправить: поля заполнены и запрос не идет.
    public var canSubmit: Bool { ready && !busy }

    public mutating func setEmail(_ text: String) {
        email = String(text.prefix(Self.emailMax))
    }

    public mutating func setPassword(_ text: String) {
        password = String(text.prefix(Self.passwordMax))
    }

    /// Код 2FA — только цифры 0–9 и не больше шести: сайт принимает ровно `^[0-9]{6}$`, а цифры других письменностей
    /// (арабские, полноширинные) он отклонил бы. Резервный — как введен, до длины сайта.
    public mutating func setCode(_ text: String) {
        code = recovery ? String(text.prefix(Self.recoveryMax)) : String(text.filter { ("0" ... "9").contains($0) }.prefix(Self.codeLength))
    }

    /// «Войти по резервному коду» ↔ «Войти по коду 2FA»: поле кода очищается.
    public mutating func toggleRecovery() {
        recovery.toggle()
        code = ""
        error = nil
    }

    /// «Войти другим аккаунтом» на втором шаге: снова почта и пароль; почта остается, пароль и код стерты.
    public mutating func back() {
        guard !busy else { return }
        self = LoginForm(email: email)
    }

    /// Начать запрос: что отправить — код 2FA и резервный код (не больше одного, только на втором шаге); nil — нельзя.
    public mutating func begin() -> (code: String?, recovery: String?)? {
        guard canSubmit else { return nil }
        busy = true
        error = nil
        guard codeStep else { return (nil, nil) }
        return recovery ? (nil, code) : (code, nil)
    }

    /// Форма после ответа сайта: код стирается всегда, почта остается. Пароль — на первом шаге и когда второй кончился (вошли,
    /// пароль сменили, пока вводили код); на втором шаге он ждет следующего кода. «Нужен код» — второй шаг, без ошибки.
    public mutating func answered(_ result: LoginResult) {
        let onCodeStep = codeStep
        busy = false
        code = ""
        retryMinutes = 0
        switch result {
        case .ok:
            password = ""
            codeStep = false
            recovery = false
        case .codeRequired:
            codeStep = true
            recovery = false
            error = nil
        case let .failed(failure, minutes):
            if onCodeStep && failure == .invalid {
                password = ""
                codeStep = false
                recovery = false
                error = failure
            } else {
                if !onCodeStep {
                    password = ""
                }

                error = failure
                retryMinutes = minutes
            }
        }
    }
}

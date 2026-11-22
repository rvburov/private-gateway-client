import Foundation
import PrivateGatewayCore
import UIKit

/// Вход в аккаунт сайта и выход (доска 10, «Настройки» → «Аккаунт») — `AccountViewModel` Android: состояние входа — из
/// `AccountService`, форма в два шага — своя (`LoginForm`). Пока окно на экране, сайт спрашивается при запуске, при возврате
/// в приложение и раз в 30 минут; отзыв и истечение срока выключают защиту (`onLost`) и показывают вход с причиной.
@MainActor
final class AccountModel: ObservableObject {
    /// Вход; nil — еще не прочитан: окно показывает только фон, чтобы экран входа не мелькал у тех, кто вошел.
    @Published private(set) var state: AccountState?
    @Published private(set) var form = LoginForm()
    @Published private(set) var signingOut = false
    @Published private(set) var cleanupPending = false
    @Published private(set) var restoringNetwork = false

    /// Доступ кончился (выход, отзыв, срок) — выключить защиту; задает окно.
    var onLost: (() async -> Bool)?

    private let service: AccountService
    private var formGeneration: UInt64 = 0

    /// - Parameters:
    ///   - source: сайт; nil — настоящий (`AccountClient`).
    ///   - form, state: начальная форма и вход — тестам экранов.
    init(container: SharedContainer = .current(), source: (any AccountSource)? = nil, form: LoginForm = LoginForm(), state: AccountState? = nil) {
        let version = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? ""
        let device = UIDevice.current
        let name = DeviceInfo.phoneName(model: device.model, system: "\(device.systemName) \(device.systemVersion)")
        service = AccountService(
            store: container.account, source: source ?? AccountClient(appVersion: version),
            device: { DeviceInfo(uid: $0, name: name, appVersion: version) }
        )
        self.form = form
        self.state = state
    }

    /// Вход есть и срок не прошел.
    var allowed: Bool {
        state?.allowed(now: Date()) ?? false
    }

    func load() async {
        state = await service.state()
    }

    func setEmail(_ text: String) {
        form.setEmail(text)
    }

    func setPassword(_ text: String) {
        form.setPassword(text)
    }

    func setCode(_ text: String) {
        form.setCode(text)
    }

    /// «Войти по резервному коду» ↔ «Войти по коду 2FA».
    func toggleRecovery() {
        form.toggleRecovery()
    }

    /// «Войти другим аккаунтом» на втором шаге.
    func back() {
        form.back()
    }

    /// «Войти» и «Готово» на клавиатуре в последнем поле; запрос уже идет или поля не заполнены — ничего.
    func submit() {
        guard !signingOut, !restoringNetwork, let codes = form.begin() else {
            return
        }

        let email = form.email
        let password = form.password
        formGeneration &+= 1
        let attempt = formGeneration
        Task {
            // Прежде чем новый аккаунт станет разрешенным для расширения, все прежние сохранения VPN и cleanup закончены.
            let cleared = await cleanupAccess()
            guard attempt == formGeneration, !signingOut else { return }
            guard cleared else {
                form.answered(.failed(.network))
                return
            }
            let result = await service.login(email: email, password: password, code: codes.code, recovery: codes.recovery)
            guard attempt == formGeneration, !signingOut else { return }
            form.answered(result)
            let loaded = await service.state()
            if attempt == formGeneration, !signingOut { state = loaded }
        }
    }

    /// Повторить только восстановление сети: пароль и запрос входа не нужны, авторизация сама не продолжается.
    func retryCleanup() {
        guard !signingOut, !restoringNetwork, !form.busy else { return }
        restoringNetwork = true
        Task { _ = await cleanupAccess() }
    }

    private func cleanupAccess() async -> Bool {
        let attempt = formGeneration
        restoringNetwork = true
        let confirmed = await onLost?() ?? true
        guard attempt == formGeneration else { return false }
        cleanupPending = !confirmed
        restoringNetwork = false
        return confirmed
    }

    /// Сброс PIN-кода блока сайтов (решение 742): вход тем же аккаунтом, защита остается.
    func confirm(email: String, password: String, code: String?) async -> LoginResult {
        let result = await service.confirm(email: email, password: password, code: code)
        state = await service.state()
        return result
    }

    /// «Выйти» в «Настройках» (после вопроса): экран входа — сразу и с пустыми полями; прежние запуски отменяются, профиль
    /// выключается. Новый вход ждет завершения очистки и выхода на сайте (не дольше 3 секунд).
    func signOut() async {
        guard !signingOut else { return }
        signingOut = true
        defer { signingOut = false }
        formGeneration &+= 1
        form = LoginForm()
        state = AccountState()
        let remote = Task { await service.signOut() }
        _ = await cleanupAccess()
        await remote.value
        state = AccountState()
    }

    /// Спросить сайт о доступе: `force` — по расписанию; иначе (запуск, возврат в приложение) не чаще раза в минуту. Отзыв
    /// и истечение — защита выключается, окно показывает вход с причиной.
    func refresh(force: Bool) async {
        let attempt = formGeneration
        let verdict = await service.refresh(force: force)
        let loaded = await service.state()
        guard attempt == formGeneration, !signingOut else { return }
        state = loaded
        if verdict == .revoked || verdict == .expired {
            _ = await cleanupAccess()
        }
    }

    /// Пока окно на экране: срок — по часам, сайт — раз в 30 минут; срок, который дойдет раньше, будит раньше.
    func watch() async {
        await refresh(force: false)
        while !Task.isCancelled {
            let until = state?.record.map { $0.expiresAt.timeIntervalSinceNow } ?? AccountGate.checkEvery
            let wait = min(AccountGate.checkEvery, max(1, until))
            try? await Task.sleep(nanoseconds: UInt64(wait * 1_000_000_000))
            guard !Task.isCancelled else {
                return
            }

            let attempt = formGeneration
            let expiry = await service.expireIfDue()
            guard attempt == formGeneration, !signingOut else { continue }
            if expiry == .expired {
                let loaded = await service.state()
                guard attempt == formGeneration, !signingOut else { continue }
                state = loaded
                _ = await cleanupAccess()
            } else if wait >= AccountGate.checkEvery {
                await refresh(force: true)
            }
        }
    }
}

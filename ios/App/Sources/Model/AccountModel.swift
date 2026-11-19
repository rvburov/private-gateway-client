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

    /// Доступ кончился (выход, отзыв, срок) — выключить защиту; задает окно.
    var onLost: (() async -> Void)?

    private let service: AccountService

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
        guard let codes = form.begin() else {
            return
        }

        let email = form.email
        let password = form.password
        Task {
            let result = await service.login(email: email, password: password, code: codes.code, recovery: codes.recovery)
            form.answered(result)
            state = await service.state()
        }
    }

    /// Сброс PIN-кода блока сайтов (решение 742): вход тем же аккаунтом, защита остается.
    func confirm(email: String, password: String, code: String?) async -> LoginResult {
        let result = await service.confirm(email: email, password: password, code: code)
        state = await service.state()
        return result
    }

    /// «Выйти» в «Настройках» (после вопроса): экран входа — сразу и с пустыми полями; сайту — выход, пока защита еще идет
    /// (не дольше 3 секунд), потом защита выключается.
    func signOut() async {
        form = LoginForm()
        state = AccountState()
        await service.signOut()
        state = await service.state()
        await onLost?()
    }

    /// Спросить сайт о доступе: `force` — по расписанию; иначе (запуск, возврат в приложение) не чаще раза в минуту. Отзыв
    /// и истечение — защита выключается, окно показывает вход с причиной.
    func refresh(force: Bool) async {
        let verdict = await service.refresh(force: force)
        state = await service.state()
        if verdict == .revoked || verdict == .expired {
            await onLost?()
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

            if await service.expireIfDue() == .expired {
                state = await service.state()
                await onLost?()
            } else if wait >= AccountGate.checkEvery {
                await refresh(force: true)
            }
        }
    }
}

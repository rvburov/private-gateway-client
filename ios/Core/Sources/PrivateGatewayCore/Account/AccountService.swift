import Foundation

/// Что приложение спрашивает у сайта — шов над `AccountClient`, в тестах подделка.
public protocol AccountSource: Sendable {
    /// Вход по почте и паролю; на втором шаге — еще код 2FA (`code`) или резервный код (`recovery`), не больше одного.
    func login(email: String, password: String, code: String?, recovery: String?, device: DeviceInfo) async -> LoginResult
    func status(token: String) async -> StatusResult
    /// Выйти на сайте; ошибка сети не мешает выйти в приложении: true — сайт принял.
    func logout(token: String) async -> Bool
}

/// Вход в аккаунт сайта Private Gateway Web — `AccountRepository` Android (решения 616–619, 621): пользоваться защитой можно
/// только с действующим входом, решение о доступе принимает сайт. Проверка сайта — при запуске, при возврате в приложение и
/// раз в 30 минут, пока оно на экране; срок известен расширению туннеля и без приложения. Отзыв — только ответ 401; без ответа
/// действует последний подтвержденный срок. Ответ про старый токен (вышли или вошли заново, пока шел запрос) отбрасывается.
public actor AccountService {
    private let store: AccountStore
    private let source: any AccountSource
    private let device: @Sendable (String) -> DeviceInfo
    private let clock: @Sendable () -> Date
    private let logoutWait: TimeInterval
    /// Меняется при каждом входе и выходе: ответ сайта применяется, только если вход все тот же.
    private var generation = 0
    private var lastCheck: Date?

    /// - Parameter device: сведения об устройстве по его uid.
    public init(
        store: AccountStore, source: any AccountSource, device: @escaping @Sendable (String) -> DeviceInfo,
        clock: @escaping @Sendable () -> Date = Date.init, logoutWait: TimeInterval = AccountGate.logoutWait
    ) {
        self.store = store
        self.source = source
        self.device = device
        self.clock = clock
        self.logoutWait = logoutWait
    }

    public func state() -> AccountState {
        store.read()
    }

    /// Вход: без кода — первый шаг (участнику его хватает, администратору сайт ответит «нужен код»); на втором — не больше
    /// одного из `code` и `recovery`. Связка ключей или файл не работают — `.store`.
    public func login(email: String, password: String, code: String?, recovery: String?) async -> LoginResult {
        let uid: String
        do {
            uid = try store.deviceUid()
        } catch {
            return .failed(.store)
        }

        generation += 1
        let attempt = generation
        let result = await source.login(email: email, password: password, code: code, recovery: recovery, device: device(uid))
        guard case let .ok(session) = result else {
            return result
        }

        // Пока шел запрос, вышли (или вошли снова): этот ответ уже не тот вход.
        guard attempt == generation else {
            return .failed(.network)
        }

        do {
            try store.save(session, now: clock())
        } catch {
            return .failed(.store)
        }

        lastCheck = clock()
        return result
    }

    /// Подтвердить аккаунт для сброса PIN-кода блока сайтов (решение 742): обычный вход тем же аккаунтом, защита остается.
    /// Другая почта — отказ без запроса к сайту: сброс не меняет аккаунт приложения.
    public func confirm(email: String, password: String, code: String?) async -> LoginResult {
        if let own = store.read().record?.email, own.caseInsensitiveCompare(email.trimmingCharacters(in: .whitespacesAndNewlines)) != .orderedSame {
            return .failed(.invalid)
        }

        return await login(email: email, password: password, code: code, recovery: nil)
    }

    /// Спросить сайт о доступе: при запуске, возврате в приложение (`force` = false — не чаще раза в минуту) и по расписанию.
    /// Входа нет или ответ уже не про этот вход — nil. Отзыв и истечение стирают вход: защиту выключает приложение.
    public func refresh(force: Bool) async -> AccessVerdict? {
        let now = clock()
        // Токен не прочитать (Связка ключей не отвечает) — как «сайт не ответил»: действует известный срок.
        guard store.read().record != nil, let token = try? store.token() else {
            return expireIfDue()
        }

        if !force, let last = lastCheck, now.timeIntervalSince(last) < AccountGate.recheckAfter {
            return nil
        }

        lastCheck = now
        let attempt = generation
        let status = await source.status(token: token)
        guard attempt == generation, (try? store.token()) == token, let current = store.read().record else {
            return nil
        }

        let verdict = AccountGate.verdict(current.expiresAt, status, now: clock())
        switch verdict {
        case let .keep(expires, confirmed):
            var next = current
            next.expiresAt = expires
            if confirmed {
                next.checkedAt = clock()
            }

            if case let .ok(_, user, device) = status {
                next.email = user.email
                next.displayName = user.displayName
                next.deviceName = device.name
            }

            if next != current {
                try? store.update(next)
            }
        case .revoked:
            generation += 1
            store.clear(.revoked)
        case .expired:
            generation += 1
            store.clear(.expired)
        }

        return verdict
    }

    /// Срок прошел без подтверждения (часы, а не сеть): выход как при истечении — `.expired`; иначе nil.
    @discardableResult
    public func expireIfDue() -> AccessVerdict? {
        guard let record = store.read().record, !AccountGate.allowed(record.expiresAt, now: clock()) else {
            return nil
        }

        generation += 1
        store.clear(.expired)
        return .expired
    }

    /// Выход («Настройки» → «Выйти»): вход стирается сразу (uid остается), потом сайту — POST /api/app/logout не дольше
    /// `AccountGate.logoutWait`: запрос идет через туннель, пока защита еще поднята. Защиту выключает приложение после.
    public func signOut() async {
        generation += 1
        let token = try? store.token()
        store.clear(nil)
        lastCheck = nil
        guard let token else {
            return
        }

        // Запрос не отменяется по истечении ожидания: дойдет — сайт снимет устройство и после выключения защиты.
        let source = source
        let wait = UInt64(logoutWait * 1_000_000_000)
        let sent = Task { await source.logout(token: token) }
        // Ждать ответа или предела — что раньше; ожидание ответа отменой не прерывается, поэтому гонка — через продолжение.
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            let once = ResumeOnce(continuation)
            Task {
                _ = await sent.value
                once.resume()
            }
            Task {
                try? await Task.sleep(nanoseconds: wait)
                once.resume()
            }
        }
    }
}

/// Продолжение, которое возобновляется один раз — из того, что случится первым.
private final class ResumeOnce: @unchecked Sendable {
    private let lock = NSLock()
    private var continuation: CheckedContinuation<Void, Never>?

    init(_ continuation: CheckedContinuation<Void, Never>) {
        self.continuation = continuation
    }

    func resume() {
        lock.lock()
        let pending = continuation
        continuation = nil
        lock.unlock()
        pending?.resume()
    }
}

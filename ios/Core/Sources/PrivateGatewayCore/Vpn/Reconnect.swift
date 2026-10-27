import Foundation

/// Что делать, когда связь с сервером пропала, — как `ReconnectPolicy` десктопа и Android («Переподключение»); по умолчанию —
/// `backoff60Seconds`. Переподключение — только к выбранному серверу: другой сервер сам не выбирается.
public enum ReconnectPolicy: String, CaseIterable, Sendable {
    /// «Экспоненц. до 60 с»: повторы с растущей паузой, не реже раза в 60 с, пока пользователь не выключит защиту.
    case backoff60Seconds = "Backoff60Seconds"
    /// «До 5 минут»: повторы в течение 5 минут с обрыва, потом «Ошибка».
    case upTo5Minutes = "UpTo5Minutes"
    /// «Не повторять»: обрыв — сразу «Ошибка».
    case never = "Never"

    /// Из сохраненного имени (имена — как у Android): нет значения или имя из будущей версии — по умолчанию.
    public static func read(_ name: String?) -> ReconnectPolicy {
        name.flatMap(ReconnectPolicy.init(rawValue:)) ?? .backoff60Seconds
    }
}

/// Сроки наблюдения за связью и переподключения, мс — `ReconnectTiming` десктопа и Android: проба раз в `every`, ответа ждать
/// не дольше `probeTimeout`; паузы между попытками — от `firstPause` вдвое до `maxPause`; `giveUpAfter` — предел «До 5 минут».
public struct ReconnectTiming: Equatable, Sendable {
    public var every: Int
    public var probeTimeout: Int
    public var firstPause: Int
    public var maxPause: Int
    public var giveUpAfter: Int

    public init(every: Int = 60_000, probeTimeout: Int = 10_000, firstPause: Int = 1000, maxPause: Int = 60_000, giveUpAfter: Int = 5 * 60_000) {
        self.every = every
        self.probeTimeout = probeTimeout
        self.firstPause = firstPause
        self.maxPause = maxPause
        self.giveUpAfter = giveUpAfter
    }

    /// Пауза перед попыткой `attempt` (с 1), мс: вдвое больше прежней (1, 2, 4… с), но не больше `maxPause`.
    public func delay(_ attempt: Int) -> Int {
        precondition(attempt >= 1, "Попытки считаются с 1, а не с \(attempt)")
        var delay = firstPause
        // У потолка удвоение кончается: и через много попыток — без переполнения.
        for _ in 1..<attempt {
            if delay >= maxPause {
                return maxPause
            }

            delay += delay
        }

        return min(delay, maxPause)
    }

    /// Пора ли сдаться: «Не повторять» — сразу, «До 5 минут» — когда с обрыва прошло `giveUpAfter`, «Экспоненц. до 60 с» —
    /// никогда: расширение пробует, пока пользователь сам не выключит защиту.
    public func givesUp(_ policy: ReconnectPolicy, sinceLost: Int) -> Bool {
        switch policy {
        case .never: return true
        case .upTo5Minutes: return sinceLost >= giveUpAfter
        case .backoff60Seconds: return false
        }
    }
}

/// Итог пробы связи с сервером.
public enum Health: Sendable {
    /// Сервер ответил: следующая проба — через `ReconnectTiming.every`.
    case alive
    /// Первая неудача — бывает и у живого сервера: вторая проба сразу.
    case probeAgain
    /// Вторая неудача подряд — обрыв: переподключение к тому же серверу (`Reconnection.after`).
    case lost
}

/// Наблюдение за связью, пока «Защищено», — как `HealthWatch` Android: раз в `every` проба через туннель; одна неудача бывает и
/// у живого сервера — вторая проба сразу; две неудачи подряд — обрыв. Сеть сменилась или iPhone проснулся — проба сразу
/// (`nudge`); проверка сразу после подключения — тоже `nudge`. Время — явное (мс); сам ничего не ждет и не пробует. Не
/// потокобезопасен — им владеет одна задача наблюдения.
public final class HealthWatch {
    private let timing: ReconnectTiming
    // Неудачи подряд.
    private var failures = 0
    // Когда кончилась прежняя проба (сначала — «Защищено»): от нее считается срок следующей.
    private var lastAt: Int
    // Подгон: следующая проба — сразу.
    private var nudged = false

    public init(now: Int, timing: ReconnectTiming = ReconnectTiming()) {
        lastAt = now
        self.timing = timing
    }

    /// Когда пробовать: через `every` после прежней пробы; после неудачи или подгона — сразу.
    public var nextProbeAt: Int { nudged || failures > 0 ? lastAt : lastAt + timing.every }

    /// Сеть сменилась или iPhone проснулся — проба сразу. Во время пробы сигнал ждет ее конца: проба могла уйти еще в
    /// прежнюю сеть, следующая — тоже сразу.
    public func nudge() {
        nudged = true
    }

    /// Проба началась: подгон забран.
    public func begin() {
        nudged = false
    }

    /// Проба кончилась в `now`: `answered` — сервер ответил вовремя.
    public func probed(answered: Bool, now: Int) -> Health {
        lastAt = now
        failures = answered ? 0 : failures + 1
        switch failures {
        case 0: return .alive
        case 1: return .probeAgain
        default: return .lost
        }
    }
}

/// Что после неудачной попытки переподключения.
public enum ReconnectStep: Sendable {
    /// Пробовать снова: следующая попытка — `Reconnection.attempt`, в `Reconnection.nextAttemptAt`.
    case retry
    /// Попытки кончились по политике — «Ошибка» с кодом последней неудачи; другой сервер сам не выбирается.
    case giveUp
}

/// Одно переподключение — от обрыва до «Защищено» или до отказа, как `Reconnection` Android. Только к выбранному серверу:
/// попытка — новые маршруты и ядро, затем одна проба. Туннель остается все это время — трафик ждет, а не идет мимо. Перед
/// попыткой — пауза `ReconnectTiming.delay` от конца прежней (первая — от обрыва); подгон — попытка сразу. Политика читается
/// на каждой неудаче: ее смена в настройках действует без переподключения. Не потокобезопасен.
public final class Reconnection {
    /// Когда пропала связь — для предела «До 5 минут».
    public let lostAt: Int
    private let timing: ReconnectTiming
    /// Номер попытки, которую ждут или делают (с 1).
    public private(set) var attempt = 1
    // Когда кончилась прежняя попытка (сначала — обрыв): от нее считается пауза.
    private var lastAt: Int
    // Подгон: следующая попытка — без паузы.
    private var nudged = false

    private init(lostAt: Int, timing: ReconnectTiming) {
        self.lostAt = lostAt
        self.timing = timing
        lastAt = lostAt
    }

    /// Связь пропала в `now` — переподключение к тому же серверу; «Не повторять» — nil: сразу «Ошибка».
    public static func after(_ now: Int, _ policy: ReconnectPolicy, timing: ReconnectTiming = ReconnectTiming()) -> Reconnection? {
        policy == .never ? nil : Reconnection(lostAt: now, timing: timing)
    }

    /// Когда начать попытку `attempt`: после паузы `ReconnectTiming.delay`; после подгона — сразу.
    public var nextAttemptAt: Int { nudged ? lastAt : lastAt + timing.delay(attempt) }

    /// Сеть сменилась или iPhone проснулся — попытка сразу, без остатка паузы. Во время попытки сигнал ждет ее конца.
    public func nudge() {
        nudged = true
    }

    /// Попытка началась: подгон забран; ее номер — для журнала.
    public func begin() -> Int {
        nudged = false
        return attempt
    }

    /// Попытка не удалась в `now`: сдаться по `policy` (с обрыва прошло достаточно) или пробовать снова — номер на 1 больше.
    public func failed(_ now: Int, _ policy: ReconnectPolicy) -> ReconnectStep {
        lastAt = now
        if timing.givesUp(policy, sinceLost: now - lostAt) {
            return .giveUp
        }

        attempt += 1
        return .retry
    }
}

/// Часы и ожидание наблюдения: время — мс; ожидание кончается раньше, если пришел подгон.
public protocol GuardTimer: Sendable {
    func now() -> Int
    /// Ждать до `at`: true — раньше пришел подгон (сеть сменилась, iPhone проснулся); отмена задачи — ошибка.
    func wait(until at: Int) async throws -> Bool
}

/// Куда идет проба связи через туннель — тот же адрес, что у Android и десктопа (`PingBatch.TARGET`): TLS к нему проходит
/// только через ядро и сервер защиты.
public enum TunnelProbe {
    public static let host = "cp.cloudflare.com"
    public static let port = 443
}

/// Часы наблюдения на настоящем времени: монотонные (`ContinuousClock` идет и во сне iPhone, перевод часов его не сдвигает);
/// ожидание — кусками по полсекунды, чтобы подгон прервал его быстро. Подгон, пришедший вне ожидания (во время пробы или
/// попытки), ждет следующего ожидания — оно кончается сразу.
public final class SystemGuardTimer: GuardTimer, @unchecked Sendable {
    private let start = ContinuousClock.now
    private let lock = NSLock()
    private var nudged = false

    public init() {}

    public func now() -> Int {
        let elapsed = (ContinuousClock.now - start).components
        return Int(elapsed.seconds) * 1000 + Int(elapsed.attoseconds / 1_000_000_000_000_000)
    }

    /// Сеть сменилась или iPhone проснулся.
    public func nudge() {
        lock.lock()
        nudged = true
        lock.unlock()
    }

    public func wait(until at: Int) async throws -> Bool {
        while true {
            if takeNudge() {
                return true
            }

            let left = at - now()
            if left <= 0 {
                return false
            }

            try await Task.sleep(nanoseconds: UInt64(min(left, 500)) * 1_000_000)
        }
    }

    private func takeNudge() -> Bool {
        lock.lock()
        defer { lock.unlock() }
        let was = nudged
        nudged = false
        return was
    }
}

/// Что расширение показывает по ходу наблюдения: «Переподключение» с номером попытки, снова «Защищено», «Ошибка» с кодом.
public protocol GuardEvents: Sendable {
    func reconnecting(_ attempt: Int) async
    func protectedAgain() async
    func gaveUp(_ error: String) async
}

/// Наблюдение за связью, пока защита включена, и переподключение — `TunnelGuard` Android: `HealthWatch` решает, когда
/// пробовать и когда связь пропала, `Reconnection` — паузы и предел попыток по политике. Попытка — `restart` (новые
/// маршруты и ядро к тому же серверу) и одна проба; удалась — снова «Защищено». Код, который повтором не лечится (сервер
/// удален, ядро не берется за сервер), — сразу «Ошибка». Подгон таймера (сеть сменилась, iPhone проснулся) — проба или
/// попытка сразу. Сразу после подключения — проба без ожидания. Работает, пока не сдастся или задачу не отменят.
public struct TunnelGuard: Sendable {
    /// Коды, при которых попытка повторяется: ядро не запустилось — бывает и от сети.
    public static let retried: Set<String> = [TunnelErrors.coreFailed]

    private let timer: any GuardTimer
    private let policy: @Sendable () async -> ReconnectPolicy
    private let probe: @Sendable () async -> Bool
    private let restart: @Sendable (_ attempt: Int) async -> String?
    private let events: any GuardEvents
    private let timing: ReconnectTiming

    public init(
        timer: any GuardTimer, policy: @escaping @Sendable () async -> ReconnectPolicy, probe: @escaping @Sendable () async -> Bool,
        restart: @escaping @Sendable (_ attempt: Int) async -> String?, events: any GuardEvents, timing: ReconnectTiming = ReconnectTiming()
    ) {
        self.timer = timer
        self.policy = policy
        self.probe = probe
        self.restart = restart
        self.events = events
        self.timing = timing
    }

    public func run() async {
        do {
            var watch = HealthWatch(now: timer.now(), timing: timing)
            watch.nudge()
            while true {
                try await waitUntil({ watch.nextProbeAt }, watch.nudge)
                watch.begin()
                if watch.probed(answered: await probe(), now: timer.now()) != .lost {
                    continue
                }

                let again = try await reconnect()
                if !again {
                    return
                }

                watch = HealthWatch(now: timer.now(), timing: timing)
            }
        } catch {
            // Задачу отменили: защиту выключили или туннель закрыт.
        }
    }

    /// Переподключение к тому же серверу; true — снова «Защищено», false — сдались.
    private func reconnect() async throws -> Bool {
        guard let reconnection = Reconnection.after(timer.now(), await policy(), timing: timing) else {
            await events.gaveUp(TunnelErrors.serverNotResponding)
            return false
        }

        await events.reconnecting(reconnection.attempt)
        while true {
            try await waitUntil({ reconnection.nextAttemptAt }, reconnection.nudge)
            let error = await restart(reconnection.begin())
            if let error, !Self.retried.contains(error) {
                await events.gaveUp(error)
                return false
            }

            if error == nil, await probe() {
                await events.protectedAgain()
                return true
            }

            if reconnection.failed(timer.now(), await policy()) == .giveUp {
                await events.gaveUp(error ?? TunnelErrors.serverNotResponding)
                return false
            }

            await events.reconnecting(reconnection.attempt)
        }
    }

    /// Ждать до `at()` — или раньше, если пришел подгон: тогда `nudge`, и срок пересчитывается.
    private func waitUntil(_ at: () -> Int, _ nudge: () -> Void) async throws {
        while true {
            let next = at()
            if next <= timer.now() {
                return
            }

            if try await timer.wait(until: next) {
                nudge()
            }
        }
    }
}

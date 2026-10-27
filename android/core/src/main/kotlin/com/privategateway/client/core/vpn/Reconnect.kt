package com.privategateway.client.core.vpn

/**
 * Что делать, когда связь с сервером пропала, — как `ReconnectPolicy` десктопа («Переподключение»); по умолчанию —
 * [Backoff60Seconds]. Переподключение — только к выбранному серверу: другой сервер сам не выбирается, как у десктопа
 * (`docs/architecture/service.md`).
 */
enum class ReconnectPolicy {
    /** «Экспоненц. до 60 с»: повторы с растущей паузой, не реже раза в 60 с, пока пользователь не выключит защиту. */
    Backoff60Seconds,

    /** «До 5 минут»: повторы в течение 5 минут с обрыва, потом «Ошибка». */
    UpTo5Minutes,

    /** «Не повторять»: обрыв — сразу «Ошибка». */
    Never,
    ;

    companion object {
        /** Из сохраненного имени: нет значения или имя из будущей версии — по умолчанию, как у десктопа. */
        fun read(name: String?): ReconnectPolicy = entries.firstOrNull { it.name == name } ?: Backoff60Seconds
    }
}

/**
 * Сроки наблюдения за связью и переподключения, мс — `ReconnectTiming.Default` десктопа: проба раз в [everyMs], ответа
 * ждать не дольше [probeTimeoutMs]; паузы между попытками — от [firstPauseMs] вдвое до [maxPauseMs]; [giveUpAfterMs] —
 * предел политики «До 5 минут».
 */
data class ReconnectTiming(
    val everyMs: Long = 60_000,
    val probeTimeoutMs: Long = 10_000,
    val firstPauseMs: Long = 1_000,
    val maxPauseMs: Long = 60_000,
    val giveUpAfterMs: Long = 5 * 60_000,
) {
    /** Пауза перед попыткой [attempt] (с 1), мс: вдвое больше прежней (1, 2, 4… с), но не больше [maxPauseMs]. */
    fun delay(attempt: Int): Long {
        require(attempt >= 1) { "Попытки считаются с 1, а не с $attempt" }
        var delay = firstPauseMs
        // У потолка удвоение кончается: и через много попыток — без переполнения.
        repeat(attempt - 1) {
            if (delay >= maxPauseMs) {
                return maxPauseMs
            }

            delay += delay
        }

        return minOf(delay, maxPauseMs)
    }

    /**
     * Пора ли сдаться: «Не повторять» — сразу, «До 5 минут» — когда с обрыва прошло [giveUpAfterMs], «Экспоненц. до
     * 60 с» — никогда: служба пробует, пока пользователь сам не выключит защиту.
     */
    fun givesUp(policy: ReconnectPolicy, sinceLostMs: Long): Boolean = when (policy) {
        ReconnectPolicy.Never -> true
        ReconnectPolicy.UpTo5Minutes -> sinceLostMs >= giveUpAfterMs
        ReconnectPolicy.Backoff60Seconds -> false
    }
}

/** Итог пробы связи с сервером. */
enum class Health {
    /** Сервер ответил: следующая проба — через [ReconnectTiming.everyMs]. */
    Alive,

    /** Первая неудача — бывает и у живого сервера: вторая проба сразу. */
    ProbeAgain,

    /** Вторая неудача подряд — обрыв: переподключение к тому же серверу ([Reconnection.after]). */
    Lost,
}

/**
 * Наблюдение за связью, пока «Защищено», — как `WatchLoopAsync` десктопа: раз в [ReconnectTiming.everyMs] проба (HTTPS
 * к проверочному адресу из процесса приложения, через туннель); одна неудача бывает и у живого сервера — вторая проба
 * сразу; две неудачи подряд — обрыв. Сеть сменилась или телефон проснулся — проба сразу, а не через минуту ([nudge]);
 * проверка сразу после подключения — тоже [nudge]. Время — явное (мс, часы службы); сам ничего не ждет и не пробует:
 * служба ждет до [nextProbeAt], пробует и сообщает итог. Не потокобезопасен — служба меняет его под своим замком.
 */
class HealthWatch(now: Long, private val timing: ReconnectTiming = ReconnectTiming()) {
    // Неудачи подряд.
    private var failures = 0

    // Когда кончилась прежняя проба (сначала — «Защищено»): от нее считается срок следующей.
    private var lastAt = now

    // Подгон: следующая проба — сразу.
    private var nudged = false

    /** Когда пробовать: через [ReconnectTiming.everyMs] после прежней пробы; после неудачи или подгона — сразу. */
    val nextProbeAt: Long
        get() = if (nudged || failures > 0) lastAt else lastAt + timing.everyMs

    /**
     * Сеть телефона сменилась или он проснулся — проба сразу. Во время пробы сигнал ждет ее конца: проба могла уйти
     * еще в прежнюю сеть, следующая — тоже сразу.
     */
    fun nudge() {
        nudged = true
    }

    /** Проба началась: подгон забран. */
    fun begin() {
        nudged = false
    }

    /** Проба кончилась в [now]: [answered] — сервер ответил вовремя. */
    fun probed(answered: Boolean, now: Long): Health {
        lastAt = now
        failures = if (answered) 0 else failures + 1
        return when (failures) {
            0 -> Health.Alive
            1 -> Health.ProbeAgain
            else -> Health.Lost
        }
    }
}

/** Что после неудачной попытки переподключения. */
enum class ReconnectStep {
    /** Пробовать снова: следующая попытка — [Reconnection.attempt], в [Reconnection.nextAttemptAt]. */
    Retry,

    /** Попытки кончились по политике — «Ошибка» с кодом последней неудачи; другой сервер сам не выбирается. */
    GiveUp,
}

/**
 * Одно переподключение — от обрыва до «Защищено» или до отказа, как `ReconnectLoopAsync` десктопа. Только к выбранному
 * серверу: попытка — тот же запуск туннеля и ядра под замком службы, затем одна проба; другой сервер сам
 * не выбирается. Карта туннеля остается все это время — трафик ждет, а не идет мимо. Перед попыткой [attempt] — пауза
 * [ReconnectTiming.delay] от конца прежней (первая — от обрыва); сеть сменилась или телефон проснулся — попытка сразу
 * ([nudge]). Политика читается на каждой неудаче: ее смена в настройках действует без переподключения. Удачная
 * попытка — «Защищено» и новый [HealthWatch]; следующий обрыв — новое переподключение с первой паузы. Время — явное
 * (мс, часы службы). Не потокобезопасен — служба меняет его под своим замком.
 */
class Reconnection private constructor(
    /** Когда пропала связь — для предела «До 5 минут». */
    val lostAt: Long,
    private val timing: ReconnectTiming,
) {
    /** Номер попытки, которую ждут или делают (с 1), — `attempt` состояния «Переподключение». */
    var attempt = 1
        private set

    // Когда кончилась прежняя попытка (сначала — обрыв): от нее считается пауза.
    private var lastAt = lostAt

    // Подгон: следующая попытка — без паузы.
    private var nudged = false

    /** Когда начать попытку [attempt]: после паузы [ReconnectTiming.delay]; после подгона — сразу. */
    val nextAttemptAt: Long
        get() = if (nudged) lastAt else lastAt + timing.delay(attempt)

    /**
     * Сеть телефона сменилась или он проснулся — попытка сразу, без остатка паузы. Во время попытки сигнал ждет ее
     * конца: следующая, если понадобится, — тоже сразу.
     */
    fun nudge() {
        nudged = true
    }

    /** Попытка началась: подгон забран; ее номер — для журнала. */
    fun begin(): Int {
        nudged = false
        return attempt
    }

    /**
     * Попытка не удалась в [now]: сдаться по [policy] (с обрыва прошло достаточно) или пробовать снова — номер попытки
     * на 1 больше.
     */
    fun failed(now: Long, policy: ReconnectPolicy): ReconnectStep {
        lastAt = now
        if (timing.givesUp(policy, now - lostAt)) {
            return ReconnectStep.GiveUp
        }

        attempt++
        return ReconnectStep.Retry
    }

    companion object {
        /**
         * Связь пропала в [now] (две неудачные пробы подряд, в том числе сразу после подключения) — переподключение к
         * тому же серверу; «Не повторять» — null: сразу «Ошибка», как `LostAsync` десктопа.
         */
        fun after(now: Long, policy: ReconnectPolicy, timing: ReconnectTiming = ReconnectTiming()): Reconnection? =
            if (policy == ReconnectPolicy.Never) null else Reconnection(now, timing)
    }
}

package com.privategateway.client.core.vpn

import com.privategateway.client.core.xray.PingBatch
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.withTimeoutOrNull
import java.net.HttpURLConnection
import java.net.URI
import java.net.URL
import java.net.URLConnection
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicReference

/** Что служба показывает по ходу наблюдения: «Переподключение» с номером попытки, снова «Защищено», «Ошибка» с кодом. */
interface GuardEvents {
    suspend fun reconnecting(attempt: Int)

    suspend fun protectedAgain()

    suspend fun gaveUp(error: String)
}

/**
 * Наблюдение за связью, пока защита включена, и переподключение — `WatchLoopAsync` и `ReconnectLoopAsync` десктопа:
 * [HealthWatch] решает, когда пробовать и когда связь пропала, [Reconnection] — паузы и предел попыток по политике.
 * Попытка — [restart] (новые карта туннеля и ядро к тому же серверу, под замком службы) и одна проба; удалась — снова
 * «Защищено». Код, который повтором не лечится (сервер удален, VPN занят другой программой, ядро не берется за сервер), —
 * сразу «Ошибка». [nudge] (сеть сменилась, телефон проснулся) — проба или попытка сразу. Сразу после подключения —
 * проба без ожидания. Работает, пока не сдастся или его не отменят; время — [clock], мс.
 */
class TunnelGuard(
    private val clock: () -> Long,
    private val policy: suspend () -> ReconnectPolicy,
    private val probe: suspend () -> Boolean,
    private val restart: suspend (attempt: Int) -> String?,
    private val events: GuardEvents,
    private val timing: ReconnectTiming = ReconnectTiming(),
) {
    private val nudges = Channel<Unit>(Channel.CONFLATED)

    /** Сеть сменилась или телефон проснулся. Пока идет проба или попытка, сигнал ждет ее конца. */
    fun nudge() {
        nudges.trySend(Unit)
    }

    suspend fun run() {
        var watch = HealthWatch(clock(), timing).apply { nudge() }
        while (true) {
            waitUntil({ watch.nextProbeAt }, watch::nudge)
            watch.begin()
            if (watch.probed(probe(), clock()) != Health.Lost) {
                continue
            }

            if (!reconnect()) {
                return
            }

            watch = HealthWatch(clock(), timing)
        }
    }

    /** Переподключение к тому же серверу; true — снова «Защищено», false — сдались. */
    private suspend fun reconnect(): Boolean {
        val reconnection = Reconnection.after(clock(), policy(), timing)
        if (reconnection == null) {
            events.gaveUp(TunnelErrors.SERVER_NOT_RESPONDING)
            return false
        }

        events.reconnecting(reconnection.attempt)
        while (true) {
            waitUntil({ reconnection.nextAttemptAt }, reconnection::nudge)
            val error = restart(reconnection.begin())
            if (error != null && error !in RETRIED) {
                events.gaveUp(error)
                return false
            }

            if (error == null && probe()) {
                events.protectedAgain()
                return true
            }

            if (reconnection.failed(clock(), policy()) == ReconnectStep.GiveUp) {
                events.gaveUp(error ?: TunnelErrors.SERVER_NOT_RESPONDING)
                return false
            }

            events.reconnecting(reconnection.attempt)
        }
    }

    /** Ждать до [at] — или раньше, если пришел сигнал: тогда [nudge], и срок пересчитывается. */
    private suspend fun waitUntil(at: () -> Long, nudge: () -> Unit) {
        while (true) {
            val left = at() - clock()
            if (left <= 0) {
                return
            }

            if (withTimeoutOrNull(left) { nudges.receive() } != null) {
                nudge()
            }
        }
    }

    companion object {
        /** Коды, при которых попытка повторяется: ядро не запустилось — бывает и от сети. */
        val RETRIED = setOf(TunnelErrors.CORE_FAILED)
    }
}

/**
 * Проверка профиля до «Защищено» (решение 556), как предварительная проверка десктопа: ядро запустилось — это еще не
 * связь. Неверный ключ, неработающий сервер подписки или блокировка провайдером видны только по пробе через туннель.
 * Пробы идут, пока не прошел срок проверки (служба дает «Таймаут подключения», по умолчанию 10 с), с паузами
 * [PAUSES_MS]; последняя — в конце срока. Быстрые отказы первых секунд так получают 5–7 попыток, а не две за 2 с: вход
 * TUN ядра принимает соединение сам и сразу закрывает его, пока выход к серверу не готов (сотовая сеть сразу после
 * establish(), DNS оператора; отчет 2026-11-13 — PGC-NET-001 на сотовой сети, через минуту тот же сервер подключился).
 * Каждая проба — не дольше своего срока, проб — не меньше [MIN_TRIES]: худший случай, как и прежде, около двух сроков.
 * Не ответила ни одна — подключение не объявляется «Защищено», а сразу «Ошибка: сервер не отвечает» с уведомлением
 * «Повторить» и «Выбрать сервер», а не бесконечное «Переподключение», пока системный блок держит интернет закрытым.
 */
object StartCheck {
    /** Проб не меньше, даже если первая заняла весь срок: одна неудача бывает и у живого сервера. */
    const val MIN_TRIES = 2

    /** Паузы после неудачи, мс: ядро только что поднялось — сначала часто, дальше реже; последняя повторяется. */
    val PAUSES_MS: List<Long> = listOf(500, 1_000, 2_000, 3_000)

    /** Сколько ждать, пока Android покажет нашу сеть VPN для пробы, мс; срок проверки это ожидание включает. */
    const val NETWORK_WAIT_MS = 3_000L

    /** Вид отказа, когда проба не уложилась в свой срок целиком: сокет сам не сдался. */
    const val TIMED_OUT = "TimedOut"

    /**
     * Адрес пробы — по IP, без поиска имени: свой DNS пользователя может быть недоступен до входа в туннель или вовсе, а
     * проверяется путь через сервер, а не DNS. Любой ответ HTTPS (сертификат 1.1.1.1 выписан на сам IP) — сервер
     * пропускает трафик; HTTP без TLS Android приложению не дает.
     */
    const val TARGET = "https://1.1.1.1/"

    /**
     * Ответил ли профиль: [probe] до первого ответа, пока от начала не прошло [budgetMs], и не меньше [MIN_TRIES] раз;
     * каждая проба — не дольше [probeTimeoutMs]. [report] получает итог каждой пробы для журнала. Время — [clock], мс.
     */
    suspend fun answers(
        budgetMs: Long,
        probeTimeoutMs: Long,
        clock: () -> Long,
        report: (StartProbe) -> Unit = {},
        probe: suspend () -> TunnelProbeResult,
    ): Boolean {
        val deadline = clock() + budgetMs
        var attempt = 0
        while (true) {
            attempt++
            val started = clock()
            val result = withTimeoutOrNull(probeTimeoutMs) { probe() } ?: TunnelProbeResult(TIMED_OUT)
            report(StartProbe(attempt, result.failure, clock() - started))
            if (result.answered) {
                return true
            }

            val left = deadline - clock()
            if (attempt >= MIN_TRIES && left <= 0) {
                return false
            }

            // Пауза не выходит за срок — последняя проба приходится на его конец; после пробы, занявшей весь срок,
            // вторая — сразу.
            val pause = minOf(pauseAfter(attempt), left.coerceAtLeast(0))
            if (pause > 0) {
                delay(pause)
            }
        }
    }

    /** Пауза после неудачной пробы [attempt] (с 1), мс. */
    fun pauseAfter(attempt: Int): Long = PAUSES_MS[attempt.coerceIn(1, PAUSES_MS.size) - 1]
}

/**
 * Итог пробы: [failure] — null, если пришел ответ HTTP; иначе вид отказа — имя класса исключения, без текста: в тексте
 * бывает адрес.
 */
data class TunnelProbeResult(val failure: String?) {
    val answered: Boolean get() = failure == null

    companion object {
        val ANSWERED = TunnelProbeResult(null)

        /** Ответ пришел, но не HTTP: кода ответа нет. */
        const val NOT_HTTP = "NotHttp"
    }
}

/** Проба проверки до «Защищено»: номер (с 1), вид отказа (null — ответ) и сколько она шла, мс. */
data class StartProbe(val attempt: Int, val failure: String?, val ms: Long) {
    val answered: Boolean get() = failure == null

    /** Строка журнала «Проверки»: номер, итог и мс — без адресов и ключей. */
    fun line(): String = "start probe  try=$attempt ${failure ?: "answered"} ms=$ms"
}

/**
 * Проба связи через туннель — запрос HEAD к проверочному адресу из процесса приложения (приложение из своего VPN не
 * исключено), как `ReachesAsync` десктопа: любой ответ HTTP — связь есть; нет ответа за [timeoutMs] — нет.
 */
object TunnelProbe {
    /** Есть ли ответ. Блокирует поток — вызывать не из главного. */
    fun answers(url: String = PingBatch.TARGET, timeoutMs: Int = ReconnectTiming().probeTimeoutMs.toInt()): Boolean =
        check(url, timeoutMs).answered

    /**
     * Проба с видом отказа — для журнала. [open] открывает соединение: по умолчанию — сетью приложения по умолчанию,
     * служба передает свою сеть VPN. Отказ — любое исключение: сбой пробы — это «нет ответа», а не авария службы.
     * [opened] получает соединение, чтобы его можно было закрыть из другого потока. Блокирует поток.
     */
    fun check(
        url: String,
        timeoutMs: Int,
        open: (URL) -> URLConnection = { it.openConnection() },
        opened: (HttpURLConnection) -> Unit = {},
    ): TunnelProbeResult = try {
        val connection = open(URI(url).toURL()) as HttpURLConnection
        try {
            opened(connection)
            connection.requestMethod = "HEAD"
            connection.connectTimeout = timeoutMs
            connection.readTimeout = timeoutMs
            connection.instanceFollowRedirects = false
            connection.useCaches = false
            if (connection.responseCode > 0) {
                TunnelProbeResult.ANSWERED
            } else {
                TunnelProbeResult(TunnelProbeResult.NOT_HTTP)
            }
        } finally {
            connection.disconnect()
        }
    } catch (e: Exception) {
        TunnelProbeResult(e.javaClass.simpleName)
    }

    /**
     * [check] на потоке ввода-вывода с отменой: блокирующее чтение сокета на отмену корутины само не прерывается,
     * поэтому отмена закрывает соединение. Так срок пробы в [StartCheck] настоящий, а «Отключить» во время проверки не
     * ждет ее конца.
     */
    suspend fun probe(
        url: String,
        timeoutMs: Int,
        open: (URL) -> URLConnection = { it.openConnection() },
    ): TunnelProbeResult = coroutineScope {
        val closer = Closer()
        val work = async(Dispatchers.IO) { check(url, timeoutMs, open, closer::opened) }
        try {
            work.await()
        } catch (e: CancellationException) {
            closer.close()
            throw e
        }
    }

    /** Соединение пробы для закрытия из другого потока; отмена до открытия закрывает его сразу после открытия. */
    private class Closer {
        private val connection = AtomicReference<HttpURLConnection?>()
        private val closed = AtomicBoolean(false)

        fun opened(value: HttpURLConnection) {
            connection.set(value)
            if (closed.get()) {
                value.disconnect()
            }
        }

        fun close() {
            closed.set(true)
            connection.get()?.disconnect()
        }
    }
}

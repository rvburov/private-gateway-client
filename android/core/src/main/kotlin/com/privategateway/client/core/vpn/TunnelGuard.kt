package com.privategateway.client.core.vpn

import com.privategateway.client.core.xray.PingBatch
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.withTimeoutOrNull
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URI

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
 * Проба связи через туннель — запрос HEAD к проверочному адресу из процесса приложения (приложение из своего VPN не
 * исключено), как `ReachesAsync` десктопа: любой ответ HTTP — связь есть; нет ответа за [timeoutMs] — нет. Блокирует
 * поток — вызывать не из главного.
 */
object TunnelProbe {
    fun answers(url: String = PingBatch.TARGET, timeoutMs: Int = ReconnectTiming().probeTimeoutMs.toInt()): Boolean = try {
        val connection = URI(url).toURL().openConnection() as HttpURLConnection
        try {
            connection.requestMethod = "HEAD"
            connection.connectTimeout = timeoutMs
            connection.readTimeout = timeoutMs
            connection.instanceFollowRedirects = false
            connection.useCaches = false
            connection.responseCode > 0
        } finally {
            connection.disconnect()
        }
    } catch (_: IOException) {
        false
    }
}

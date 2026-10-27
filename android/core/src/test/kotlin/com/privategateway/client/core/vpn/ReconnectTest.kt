package com.privategateway.client.core.vpn

import org.junit.jupiter.api.Test
import org.junit.jupiter.params.ParameterizedTest
import org.junit.jupiter.params.provider.CsvSource
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

// Переподключение десктопа: desktop/tests/Unit/ReconnectTests.cs и случаи desktop/tests/Network/ReconnectTests.cs — на
// чистой логике с явным временем; запуск ядра и пробу делает служба приложения.
class ReconnectTest {
    private val second = 1_000L
    private val minute = 60 * second
    private val hour = 60 * minute
    private val timing = ReconnectTiming()
    private val backoff = ReconnectPolicy.Backoff60Seconds

    @Test
    fun `pause doubles up to the ceiling`() {
        assertEquals(listOf(1L, 2, 4, 8, 16, 32, 60, 60, 60), (1..9).map { timing.delay(it) / second })
        assertEquals(60 * second, timing.delay(Int.MAX_VALUE), "и через много попыток — без переполнения")
        assertFailsWith<IllegalArgumentException> { timing.delay(0) }
    }

    @ParameterizedTest
    @CsvSource("Never, 0, true", "UpTo5Minutes, 299, false", "UpTo5Minutes, 300, true", "Backoff60Seconds, 3600, false")
    fun `service gives up only by the policy`(policy: ReconnectPolicy, secondsSinceLost: Long, givesUp: Boolean) {
        assertEquals(givesUp, timing.givesUp(policy, secondsSinceLost * second))
    }

    @Test
    fun `timing is the desktop default`() {
        val expected = ReconnectTiming(
            everyMs = minute,
            probeTimeoutMs = 10 * second,
            firstPauseMs = second,
            maxPauseMs = minute,
            giveUpAfterMs = 5 * minute,
        )
        assertEquals(expected, timing)
    }

    @Test
    fun `policy is read from the saved name and anything else is the default`() {
        assertEquals(ReconnectPolicy.UpTo5Minutes, ReconnectPolicy.read("UpTo5Minutes"))
        assertEquals(ReconnectPolicy.Never, ReconnectPolicy.read("Never"))
        assertEquals(backoff, ReconnectPolicy.read("Backoff60Seconds"))
        assertEquals(backoff, ReconnectPolicy.read(null))
        assertEquals(backoff, ReconnectPolicy.read("Sometimes"), "имя из будущей версии")
    }

    @Test
    fun `server is probed once a minute while it answers`() {
        val watch = HealthWatch(now = 5 * minute)
        assertEquals(6 * minute, watch.nextProbeAt)
        watch.begin()
        assertEquals(Health.Alive, watch.probed(answered = true, now = 6 * minute + 300))
        assertEquals(7 * minute + 300, watch.nextProbeAt, "срок — от конца прежней пробы")
    }

    @Test
    fun `one failure probes again at once and the second in a row is a drop`() {
        val watch = HealthWatch(now = 0)
        watch.begin()
        assertEquals(Health.ProbeAgain, watch.probed(answered = false, now = minute + 10 * second))
        assertEquals(minute + 10 * second, watch.nextProbeAt, "одна неудача бывает и у живого сервера — вторая проба сразу")
        watch.begin()
        assertEquals(Health.Lost, watch.probed(answered = false, now = minute + 20 * second))
    }

    @Test
    fun `an answer between failures starts the count again`() {
        val watch = HealthWatch(now = 0)
        assertEquals(Health.ProbeAgain, watch.probed(answered = false, now = minute))
        assertEquals(Health.Alive, watch.probed(answered = true, now = minute + second))
        assertEquals(2 * minute + second, watch.nextProbeAt)
        assertEquals(Health.ProbeAgain, watch.probed(answered = false, now = 2 * minute + second))
        assertEquals(Health.Lost, watch.probed(answered = false, now = 2 * minute + 2 * second))
    }

    @Test
    fun `network change or waking up probes the server at once`() {
        // Network_change_checks_the_server_at_once…, Waking_up_checks_the_server_at_once: пробы раз в час — успеть может
        // только подгон.
        val watch = HealthWatch(now = 0, ReconnectTiming(everyMs = hour))
        watch.nudge()
        assertTrue(watch.nextProbeAt <= 5 * minute, "сразу, а не через час")
        watch.begin()
        assertEquals(Health.Alive, watch.probed(answered = true, now = 5 * minute))
        assertEquals(5 * minute + hour, watch.nextProbeAt, "сервер отвечает — смена сети ничего не ломает, дальше раз в час")

        watch.nudge()
        watch.begin()
        assertEquals(Health.ProbeAgain, watch.probed(answered = false, now = 6 * minute))
        watch.begin()
        assertEquals(Health.Lost, watch.probed(answered = false, now = 6 * minute + 2 * second))
    }

    @Test
    fun `network change during a probe brings the next probe at once`() {
        val watch = HealthWatch(now = 0)
        watch.begin()
        watch.nudge()
        assertEquals(Health.Alive, watch.probed(answered = true, now = minute))
        assertEquals(minute, watch.nextProbeAt, "проба могла уйти еще в прежнюю сеть — новая сразу")
        watch.begin()
        assertEquals(Health.Alive, watch.probed(answered = true, now = minute + second))
        assertEquals(2 * minute + second, watch.nextProbeAt, "подгон забран — дальше раз в минуту")
    }

    @Test
    fun `server that does not answer at connect is a drop and never is an error at once`() {
        // Server_that_does_not_answer_at_connect_is_not_shown_as_protected: проверка сразу после подключения — подгон; две
        // неудачи — «Переподключение», а не «Защищено»; с «Не повторять» — сразу «Ошибка».
        val watch = HealthWatch(now = 0).apply { nudge() }
        assertEquals(0L, watch.nextProbeAt)
        watch.begin()
        assertEquals(Health.ProbeAgain, watch.probed(answered = false, now = 10 * second))
        watch.begin()
        assertEquals(Health.Lost, watch.probed(answered = false, now = 20 * second))
        assertNotNull(Reconnection.after(20 * second, backoff))
        assertNotNull(Reconnection.after(20 * second, ReconnectPolicy.UpTo5Minutes))
        assertNull(Reconnection.after(20 * second, ReconnectPolicy.Never))
    }

    @Test
    fun `server that stops answering is retried with growing pauses and never given up by default`() {
        // Server_that_stops_answering_is_retried_until_it_comes_back: «Переподключение» — с попытки 1, дальше 2, 3…
        val lost = 10 * minute
        val reconnection = assertNotNull(Reconnection.after(lost, backoff))
        assertEquals(lost, reconnection.lostAt)
        assertEquals(1, reconnection.attempt)
        var now = lost
        val pauses = mutableListOf<Long>()
        repeat(9) {
            val at = reconnection.nextAttemptAt
            pauses.add(at - now)
            assertEquals(it + 1, reconnection.begin())
            now = at + 3 * second
            assertEquals(ReconnectStep.Retry, reconnection.failed(now, backoff))
        }

        assertEquals(listOf(1L, 2, 4, 8, 16, 32, 60, 60, 60), pauses.map { it / second }, "пауза — от конца прежней попытки")
        assertEquals(10, reconnection.attempt)
        assertEquals(ReconnectStep.Retry, reconnection.failed(lost + hour, backoff), "«Экспоненц. до 60 с» не сдается и через час")
        assertEquals(11, reconnection.attempt)
    }

    @Test
    fun `up to 5 minutes gives up once 5 minutes passed since the loss`() {
        // Proxy_gives_up_by_the_policy_with_an_error: пробует до предела с обрыва, а не с прежней попытки, потом сдается.
        val lost = 10 * minute
        val policy = ReconnectPolicy.UpTo5Minutes
        val reconnection = assertNotNull(Reconnection.after(lost, policy))
        assertEquals(ReconnectStep.Retry, reconnection.failed(lost + 5 * minute - 1, policy))
        assertEquals(2, reconnection.attempt)
        assertEquals(ReconnectStep.GiveUp, reconnection.failed(lost + 5 * minute, policy))
        assertEquals(2, reconnection.attempt, "попытка, на которой сдались, — последняя")
    }

    @Test
    fun `never stops at once and a policy changed to never stops at the next failure`() {
        // Core_that_exits_is_started_again_and_never_policy_stops_at_once.
        assertNull(Reconnection.after(0, ReconnectPolicy.Never))
        val reconnection = assertNotNull(Reconnection.after(0, backoff))
        assertEquals(ReconnectStep.Retry, reconnection.failed(second, backoff))
        // Политика — на каждой неудаче: ее смена в настройках действует без переподключения.
        assertEquals(ReconnectStep.GiveUp, reconnection.failed(3 * second, ReconnectPolicy.Never))
    }

    @Test
    fun `network change skips the rest of the pause`() {
        // Network_change_checks_the_server_at_once_and_skips_the_rest_of_the_pause: паузы по часу — успеть может только
        // подгон.
        val slow = ReconnectTiming(firstPauseMs = hour, maxPauseMs = hour)
        val reconnection = assertNotNull(Reconnection.after(0, backoff, slow))
        assertEquals(hour, reconnection.nextAttemptAt)
        reconnection.nudge()
        assertEquals(0L, reconnection.nextAttemptAt, "смена сети сразу после обрыва не пропадает")
        assertEquals(1, reconnection.begin())
        assertEquals(ReconnectStep.Retry, reconnection.failed(5 * second, backoff))
        assertEquals(5 * second + hour, reconnection.nextAttemptAt, "подгон забран — следующая попытка ждет свою паузу")
    }

    @Test
    fun `network change during an attempt brings the next attempt at once`() {
        // Network_change_during_an_attempt_brings_the_next_attempt_at_once.
        val slow = ReconnectTiming(everyMs = hour, firstPauseMs = hour, maxPauseMs = hour, giveUpAfterMs = hour)
        val reconnection = assertNotNull(Reconnection.after(0, backoff, slow))
        reconnection.nudge()
        assertEquals(1, reconnection.begin())
        reconnection.nudge()
        assertEquals(ReconnectStep.Retry, reconnection.failed(2 * second, backoff))
        assertEquals(2 * second, reconnection.nextAttemptAt, "сигнал ждал конца попытки, а не пропал")
        assertEquals(2, reconnection.begin())

        // И во время удачной попытки сеть сменилась — следующему переподключению этот сигнал первую паузу не сокращает.
        reconnection.nudge()
        val next = assertNotNull(Reconnection.after(10 * minute, backoff, slow))
        assertEquals(1, next.attempt)
        assertEquals(10 * minute + hour, next.nextAttemptAt)
    }
}

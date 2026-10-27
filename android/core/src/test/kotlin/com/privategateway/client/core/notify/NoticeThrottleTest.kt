package com.privategateway.client.core.notify

import com.privategateway.client.core.subscriptions.FailureReason
import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/** Повтор того же события — не чаще раза в 10 минут: случаи `NotificationCenterTests` десктопа. */
class NoticeThrottleTest {
    private val start = 1_000_000L
    private val lost = Notice(NoticeKind.ConnectionLost, "notify.lost.blocked", emptyList(), server = "Нидерланды 01")

    @Test
    fun `drop is not repeated within ten minutes`() {
        val throttle = NoticeThrottle()
        assertTrue(throttle.allows(lost, start))
        throttle.shown(lost, start)

        assertFalse(throttle.allows(lost, start), "то же событие сразу")
        assertFalse(throttle.allows(lost.copy(server = "Германия 02"), start + 60_000), "другой сервер — то же событие")
        assertFalse(throttle.allows(lost, start + NoticeThrottle.REPEAT_AFTER_MS - 1))
        assertTrue(throttle.allows(lost, start + NoticeThrottle.REPEAT_AFTER_MS))
        assertEquals(10L * 60 * 1000, NoticeThrottle.REPEAT_AFTER_MS)
    }

    @Test
    fun `pause counts from the last shown`() {
        val throttle = NoticeThrottle()
        throttle.shown(lost, start)
        val later = start + NoticeThrottle.REPEAT_AFTER_MS
        throttle.shown(lost, later)

        assertFalse(throttle.allows(lost, later + 1))
        assertTrue(throttle.allows(lost, later + NoticeThrottle.REPEAT_AFTER_MS))
    }

    @Test
    fun `other events and other subscriptions are not held`() {
        val throttle = NoticeThrottle()
        val stale = NotificationPolicy.forStale("Основная", FailureReason.Timeout)
        throttle.shown(lost, start)
        throttle.shown(stale, start)

        assertTrue(throttle.allows(Notice(NoticeKind.Connected, "notify.on.text", emptyList()), start), "другое событие")
        assertTrue(throttle.allows(stale.copy(subscription = "Запасная"), start), "другая подписка")
        assertTrue(throttle.allows(stale.copy(subscription = null), start), "без подписки — свой ключ")
        assertFalse(throttle.allows(stale.copy(failure = FailureReason.Tls), start), "та же подписка, другая причина — то же событие")
    }

    @Test
    fun `notice that was not shown does not hold the next one`() {
        // Уровень или открытое окно скрыли уведомление — отметки нет, следующее идет сразу.
        val throttle = NoticeThrottle()
        assertTrue(throttle.allows(lost, start))
        assertTrue(throttle.allows(lost, start + 1))
    }
}

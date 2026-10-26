package com.privategateway.client.core.subscriptions

import org.junit.jupiter.api.Test
import kotlin.test.assertEquals

class SubscriptionScheduleTest {
    private val hour = 60L * 60 * 1000
    private val now = 1_000 * hour

    @Test
    fun `due when the last success is older than the interval`() {
        val subscriptions = listOf(
            SubscriptionTimes("fresh", updatedAt = now - 11 * hour, lastAttemptAt = now - 11 * hour),
            SubscriptionTimes("old", updatedAt = now - 12 * hour, lastAttemptAt = now - 12 * hour),
            SubscriptionTimes("never", updatedAt = null, lastAttemptAt = null),
        )
        assertEquals(listOf("old", "never"), SubscriptionSchedule.due(subscriptions, RefreshInterval.Every12Hours, now))
        assertEquals(listOf("fresh", "old", "never"), SubscriptionSchedule.due(subscriptions, RefreshInterval.Every6Hours, now))
        assertEquals(listOf("never"), SubscriptionSchedule.due(subscriptions, RefreshInterval.Every24Hours, now))
    }

    @Test
    fun `after a failure it waits an hour`() {
        val failed = SubscriptionTimes("failed", updatedAt = now - 48 * hour, lastAttemptAt = now - 59 * 60 * 1000)
        assertEquals(emptyList(), SubscriptionSchedule.due(listOf(failed), RefreshInterval.Every6Hours, now))
        assertEquals(listOf("failed"), SubscriptionSchedule.due(listOf(failed.copy(lastAttemptAt = now - hour)), RefreshInterval.Every6Hours, now))
    }

    @Test
    fun `off means only the button`() {
        assertEquals(emptyList(), SubscriptionSchedule.due(listOf(SubscriptionTimes("never", null, null)), RefreshInterval.Off, now))
    }
}

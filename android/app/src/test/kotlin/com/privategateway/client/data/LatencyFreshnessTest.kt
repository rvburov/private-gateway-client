package com.privategateway.client.data

import org.junit.Test
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class LatencyFreshnessTest {
    @Test
    fun `latency expires at five minutes and after path change or a backward clock jump`() {
        val checked = LatencyCheck(at = 1_000)
        assertFalse(checked.isStale(1_000))
        assertFalse(checked.isStale(300_999))
        assertTrue(checked.isStale(301_000))
        assertTrue(checked.copy(stale = true).isStale(1_000))
        assertTrue(checked.isStale(999))
    }
}

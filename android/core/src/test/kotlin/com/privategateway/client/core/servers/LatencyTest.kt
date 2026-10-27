package com.privategateway.client.core.servers

import org.junit.jupiter.api.Test
import org.junit.jupiter.params.ParameterizedTest
import org.junit.jupiter.params.provider.CsvSource
import kotlin.test.assertEquals
import kotlin.test.assertNull

// Окраска задержки десктопа (ServersPageViewModel.Row, ServersPageTests: 148 мс — серым) и «—» для не измеренных (решение 428).
class LatencyTest {
    @ParameterizedTest
    @CsvSource("1, Good", "42, Good", "120, Good", "121, Slow", "148, Slow")
    fun `latency above 120 ms is slow`(ms: Int, expected: LatencyKind) {
        assertEquals(120, LatencyKind.SLOW_MS)
        assertEquals(expected, LatencyKind.of(Latency.of(ms)))
    }

    @Test
    fun `no answer differs from never measured`() {
        assertEquals(LatencyKind.Never, LatencyKind.of(null))
        for (failure in LatencyFailure.entries) {
            assertEquals(LatencyKind.NoAnswer, LatencyKind.of(Latency.failed(failure)), failure.name)
        }
    }

    @Test
    fun `measured latency is at least one millisecond`() {
        assertEquals(Latency(1, null), Latency.of(0))
        assertEquals(Latency(42, null), Latency.of(42))
        val failed = Latency.failed(LatencyFailure.CoreFailed)
        assertNull(failed.ms)
        assertEquals(LatencyFailure.CoreFailed, failed.failure)
    }
}

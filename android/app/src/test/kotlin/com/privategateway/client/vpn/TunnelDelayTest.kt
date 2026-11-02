package com.privategateway.client.vpn

import org.junit.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull

/** Числа «Задержки»: медиана трех замеров и сколько кругов до сервера уходит на новое соединение (как у десктопа). */
class TunnelDelayTest {
    @Test
    fun `median is the middle of the samples`() {
        assertEquals(42, DelayBreakdown.median(listOf(160, 38, 42)))
        assertEquals(12, DelayBreakdown.median(listOf(13, 10)))
        assertEquals(7, DelayBreakdown.median(listOf(7)))
        assertNull(DelayBreakdown.median(emptyList()))
    }

    @Test
    fun `rounds come from the measured numbers`() {
        assertEquals(4.2, DelayBreakdown(38, 42, 160, 0L).rounds)
        assertNull(DelayBreakdown(null, 42, 160, 0L).rounds, "до сервера не мерили (сервер по UDP)")
        assertNull(DelayBreakdown(38, 42, null, 0L).rounds)
        assertNull(DelayBreakdown(0, 42, 160, 0L).rounds)
    }
}

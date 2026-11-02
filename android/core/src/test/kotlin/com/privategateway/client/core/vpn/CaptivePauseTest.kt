package com.privategateway.client.core.vpn

import org.junit.jupiter.api.Test
import kotlin.test.assertEquals

/** Пауза для входа в Wi-Fi (решение 557): пять минут, остаток не уходит в минус, отсчет — минуты и секунды вверх. */
class CaptivePauseTest {
    @Test
    fun `pause lasts five minutes`() {
        assertEquals(300_000L, CaptivePause.DURATION_MS)
    }

    @Test
    fun `left never goes below zero`() {
        assertEquals(60_000L, CaptivePause.left(until = 100_000, now = 40_000))
        assertEquals(0L, CaptivePause.left(until = 100_000, now = 140_000))
    }

    @Test
    fun `clock shows minutes and seconds rounded up`() {
        assertEquals("5:00", CaptivePause.clock(300_000))
        assertEquals("4:59", CaptivePause.clock(299_000))
        assertEquals("0:05", CaptivePause.clock(4_001))
        assertEquals("0:01", CaptivePause.clock(1))
        assertEquals("0:00", CaptivePause.clock(0))
    }
}

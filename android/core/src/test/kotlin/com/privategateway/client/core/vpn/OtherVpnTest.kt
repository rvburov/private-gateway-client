package com.privategateway.client.core.vpn

import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/** Отзыв VPN Android (DECISIONS.md, п. 4): другое приложение заняло VPN или пользователь выключил наш в настройках. */
class OtherVpnTest {
    private fun run(appearsAt: Int?): Triple<Boolean, Int, Long> {
        var checks = 0
        var waited = 0L
        var result = false
        runTest {
            result = OtherVpn.tookOver(pause = { waited += it }) {
                checks++
                appearsAt != null && checks >= appearsAt
            }
        }

        return Triple(result, checks, waited)
    }

    @Test
    fun `vpn of another app that comes up after ours is revoked is found`() {
        val (other, checks, waited) = run(appearsAt = 3)
        assertTrue(other)
        assertEquals(3, checks, "дальше не ждем")
        assertEquals(OtherVpn.SETTLE_MS + 2 * OtherVpn.STEP_MS, waited)
    }

    @Test
    fun `first check waits for our own tunnel to go away`() {
        val (other, checks, waited) = run(appearsAt = 1)
        assertTrue(other)
        assertEquals(1, checks)
        assertEquals(OtherVpn.SETTLE_MS, waited, "до первой проверки — пауза: свой туннель до Android 11 виден как чужой")
    }

    @Test
    fun `no other vpn means it was turned off by hand, and the wait is bounded`() {
        val (other, checks, waited) = run(appearsAt = null)
        assertFalse(other)
        assertEquals(OtherVpn.WATCH_MS, waited)
        assertEquals(1 + ((OtherVpn.WATCH_MS - OtherVpn.SETTLE_MS) / OtherVpn.STEP_MS).toInt(), checks)
    }
}

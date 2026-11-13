package com.privategateway.client.core.vpn

import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.delay
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/**
 * Проверка профиля до «Защищено» (решение 556) на виртуальном времени: быстрые отказы первых секунд повторяются до
 * конца срока, а не два раза (отчет 2026-11-13, PGC-NET-001 на сотовой сети); мертвый сервер — «Сервер не отвечает» за
 * срок, а не бесконечно; зависшая проба обрывается своим сроком.
 */
@OptIn(ExperimentalCoroutinesApi::class)
class StartCheckTest {
    /** Пробы по очереди из списка: сколько идет (мс) и вид отказа (null — ответ); кончился список — ответ сразу. */
    private class Rig(private val scope: TestScope, steps: List<Pair<Long, String?>>) {
        val startedAt = mutableListOf<Long>()
        val lines = mutableListOf<String>()
        private val queue = ArrayDeque(steps)
        private val clock = { scope.testScheduler.currentTime }

        suspend fun run(budgetMs: Long = BUDGET_MS, probeTimeoutMs: Long = BUDGET_MS): Boolean =
            StartCheck.answers(budgetMs, probeTimeoutMs, clock, report = { lines += it.line() }) {
                startedAt += clock()
                check(startedAt.size <= 100) { "пробы без конца" }
                val (ms, failure) = queue.removeFirstOrNull() ?: (0L to null)
                delay(ms)
                TunnelProbeResult(failure)
            }
    }

    @Test
    fun `first answer is enough`() = runTest {
        val rig = Rig(this, emptyList())
        assertTrue(rig.run())
        assertEquals(listOf(0L), rig.startedAt)
        assertEquals(listOf("start probe  try=1 answered ms=0"), rig.lines)
    }

    @Test
    fun `fast failures in the first seconds are retried until the tunnel answers`() = runTest {
        val rig = Rig(this, List(3) { 100L to "ConnectException" } + (200L to null))
        assertTrue(rig.run(), "ядро в первые секунды еще не вышло к серверу — это не «Сервер не отвечает»")

        // Паузы 0,5, 1 и 2 с после проб по 0,1 с; ответ — на четвертой, через 4 с.
        assertEquals(listOf(0L, 600L, 1_700L, 3_800L), rig.startedAt)
        assertEquals(4_000L, testScheduler.currentTime)
        assertEquals("start probe  try=1 ConnectException ms=100", rig.lines.first())
        assertEquals("start probe  try=4 answered ms=200", rig.lines.last())
    }

    @Test
    fun `a server that only fails fast is given up after the budget, not after two probes`() = runTest {
        val rig = Rig(this, List(100) { 200L to "SSLHandshakeException" })
        assertFalse(rig.run())

        // Пробы по 0,2 с, паузы 0,5, 1, 2, 3 с; последняя пауза укорочена до конца срока — там и последняя проба.
        assertEquals(listOf(0L, 700L, 1_900L, 4_100L, 7_300L, 10_000L), rig.startedAt)
        assertEquals(10_200L, testScheduler.currentTime, "сдались по сроку, а не после двух проб и не позже")
        assertTrue(rig.lines.all { it.contains("SSLHandshakeException") }, rig.lines.toString())
    }

    @Test
    fun `a probe that hangs is cut at its timeout and a dead server fails within two timeouts`() = runTest {
        val rig = Rig(this, List(5) { 1_000_000L to null })
        assertFalse(rig.run())

        // Первая проба заняла весь срок — вторая сразу, без паузы; дальше срок прошел.
        assertEquals(listOf(0L, 10_000L), rig.startedAt)
        assertEquals(20_000L, testScheduler.currentTime)
        assertEquals(listOf("start probe  try=1 TimedOut ms=10000", "start probe  try=2 TimedOut ms=10000"), rig.lines)
    }

    @Test
    fun `a second probe is made even when the first took the whole budget`() = runTest {
        val slow = Rig(this, listOf(1_000_000L to null, 2_000L to null))
        assertTrue(slow.run(), "одна неудача бывает и у живого сервера")
        assertEquals(listOf(0L, 10_000L), slow.startedAt)

        // Срок уже съело ожидание сети VPN: проб все равно две.
        val late = Rig(this, List(5) { 100L to "ConnectException" })
        val started = testScheduler.currentTime
        assertFalse(late.run(budgetMs = 0))
        assertEquals(listOf(0L, 100L), late.startedAt.map { it - started })
    }

    @Test
    fun `answer slower than the pauses but within the probe timeout counts`() = runTest {
        val rig = Rig(this, listOf(9_500L to null))
        assertTrue(rig.run())
        assertEquals(listOf("start probe  try=1 answered ms=9500"), rig.lines)
    }

    @Test
    fun `pauses grow and the last one repeats`() {
        assertEquals(listOf(500L, 1_000L, 2_000L, 3_000L, 3_000L, 3_000L), (1..6).map(StartCheck::pauseAfter))
    }

    private companion object {
        /** Срок проверки и пробы — «Таймаут подключения» по умолчанию. */
        const val BUDGET_MS = 10_000L
    }
}

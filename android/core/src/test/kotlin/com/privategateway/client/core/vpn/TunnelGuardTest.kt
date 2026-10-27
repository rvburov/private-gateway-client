package com.privategateway.client.core.vpn

import com.sun.net.httpserver.HttpServer
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.advanceTimeBy
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.net.InetSocketAddress
import java.net.ServerSocket
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/** Наблюдение и переподключение на виртуальном времени — случаи `ReconnectTests` и `WatchTests` десктопа. */
@OptIn(ExperimentalCoroutinesApi::class)
class TunnelGuardTest {
    /** Служба для проверки: пробы и попытки по очереди из списков, события — строками с временем. */
    private class Rig(scope: TestScope, probes: List<Boolean>, restarts: List<String?> = emptyList()) : GuardEvents {
        val log = mutableListOf<String>()
        val probesAt = mutableListOf<Long>()
        val restartsAt = mutableListOf<Long>()
        var policy = ReconnectPolicy.Backoff60Seconds
        private val probeQueue = ArrayDeque(probes)
        private val restartQueue = ArrayDeque(restarts)
        private val clock = { scope.testScheduler.currentTime }

        val guard = TunnelGuard(
            clock = clock,
            policy = { policy },
            probe = {
                probesAt += clock()
                // Проба идет секунду — как настоящая, а не мгновенно.
                delay(1_000)
                probeQueue.removeFirstOrNull() ?: true
            },
            restart = { attempt ->
                restartsAt += clock()
                log += "restart $attempt @${clock()}"
                restartQueue.removeFirstOrNull()
            },
            events = this,
        )

        override suspend fun reconnecting(attempt: Int) {
            log += "reconnecting $attempt @${clock()}"
        }

        override suspend fun protectedAgain() {
            log += "protected @${clock()}"
        }

        override suspend fun gaveUp(error: String) {
            log += "gave up $error @${clock()}"
        }
    }

    private fun TestScope.start(rig: Rig): Job = launch { rig.guard.run() }

    @Test
    fun `live server is probed at once and then every minute`() = runTest {
        val rig = Rig(this, probes = emptyList())
        val job = start(rig)
        advanceTimeBy(3 * 61_000L + 1)
        job.cancel()

        // Проба сразу после подключения, дальше — через минуту после конца прежней (проба идет секунду).
        assertEquals(listOf(0L, 61_000L, 122_000L, 183_000L), rig.probesAt)
        assertTrue(rig.log.isEmpty())
    }

    @Test
    fun `one failure is probed again at once and is not a drop`() = runTest {
        val rig = Rig(this, probes = listOf(true, false, true))
        val job = start(rig)
        advanceTimeBy(2 * 60_000L)
        job.cancel()

        assertEquals(listOf(0L, 61_000L, 62_000L), rig.probesAt.take(3), "вторая проба — сразу после неудачи")
        assertTrue(rig.log.isEmpty(), "одна неудача бывает и у живого сервера")
    }

    @Test
    fun `two failures reconnect to the same server after a pause and probe it`() = runTest {
        val rig = Rig(this, probes = listOf(false, false, true))
        val job = start(rig)
        advanceTimeBy(10_000)
        job.cancel()

        // Обрыв — после второй пробы (2 с); первая попытка — через секунду; после нее — проба.
        assertEquals(listOf("reconnecting 1 @2000", "restart 1 @3000", "protected @4000"), rig.log)
    }

    @Test
    fun `pauses double up to a minute and a network change tries at once`() = runTest {
        val rig = Rig(this, probes = listOf(false, false), restarts = List(8) { TunnelErrors.CORE_FAILED })
        val job = start(rig)
        advanceTimeBy(2_000 + 1_000 + 2_000 + 4_000 + 8_000 + 16_000 + 32_000 + 60_000 + 60_000)
        runCurrent()
        assertEquals(listOf(3_000L, 5_000L, 9_000L, 17_000L, 33_000L, 65_000L, 125_000L, 185_000L), rig.restartsAt)
        assertTrue(rig.log.contains("reconnecting 9 @185000"), "номер попытки, которую ждут")

        rig.guard.nudge()
        runCurrent()
        assertEquals(185_000L, rig.restartsAt.last() , "попытка 8 уже была; подгон — следующая сразу")
        advanceTimeBy(1)
        job.cancel()
    }

    @Test
    fun `nudge during a pause tries at once`() = runTest {
        val rig = Rig(this, probes = listOf(false, false), restarts = List(3) { TunnelErrors.CORE_FAILED })
        val job = start(rig)
        advanceTimeBy(3_500)
        assertEquals(listOf(3_000L), rig.restartsAt)

        rig.guard.nudge()
        runCurrent()
        assertEquals(listOf(3_000L, 3_500L), rig.restartsAt, "сеть сменилась — не ждать 2 с")
        job.cancel()
    }

    @Test
    fun `up to five minutes gives up with the server not responding`() = runTest {
        val rig = Rig(this, probes = List(200) { false })
        rig.policy = ReconnectPolicy.UpTo5Minutes
        val job = start(rig)
        advanceTimeBy(10 * 60_000L)
        job.cancel()

        val last = rig.log.last()
        assertTrue(last.startsWith("gave up ${TunnelErrors.SERVER_NOT_RESPONDING}"), last)
        val at = last.substringAfter('@').toLong()
        assertTrue(at - 2_000 >= 5 * 60_000L && at - 2_000 < 6 * 60_000L, "сдались через 5 минут после обрыва, а не раньше: $at")
    }

    @Test
    fun `never gives up at once and policy change applies to the next failure`() = runTest {
        val never = Rig(this, probes = listOf(false, false))
        never.policy = ReconnectPolicy.Never
        start(never).join()
        assertEquals(listOf("gave up ${TunnelErrors.SERVER_NOT_RESPONDING} @2000"), never.log, "«Не повторять» — сразу «Ошибка»")

        val changed = Rig(this, probes = List(10) { false })
        val job = start(changed)
        advanceTimeBy(2_000 + 1_000 + 1_000)
        changed.policy = ReconnectPolicy.Never
        advanceTimeBy(5_000)
        job.join()
        assertTrue(changed.log.last().startsWith("gave up"), changed.log.toString())
        assertEquals(1, changed.restartsAt.size, "смена политики — на ближайшей неудаче, без переподключения")
    }

    @Test
    fun `giving up names the last failure`() = runTest {
        val rig = Rig(this, probes = List(400) { false }, restarts = List(400) { TunnelErrors.CORE_FAILED })
        rig.policy = ReconnectPolicy.UpTo5Minutes
        start(rig).join()
        assertTrue(rig.log.last().startsWith("gave up ${TunnelErrors.CORE_FAILED}"), "«Ошибка» с кодом последней неудачи: ${rig.log.last()}")
    }

    @Test
    fun `errors a retry cannot fix give up at once`() = runTest {
        val rig = Rig(this, probes = listOf(false, false), restarts = listOf(TunnelErrors.NO_SERVER))
        start(rig).join()
        assertEquals(listOf("reconnecting 1 @2000", "restart 1 @3000", "gave up ${TunnelErrors.NO_SERVER} @3000"), rig.log)
    }

    @Test
    fun `probe answers on any http response and not on silence or refusal`() {
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/204") { it.sendResponseHeaders(204, -1); it.close() }
        server.createContext("/500") { it.sendResponseHeaders(500, -1); it.close() }
        server.start()
        val silent = ServerSocket(0, 1, java.net.InetAddress.getLoopbackAddress())
        val closed = ServerSocket(0).use { it.localPort }
        try {
            val port = server.address.port
            assertTrue(TunnelProbe.answers("http://127.0.0.1:$port/204", 2_000))
            assertTrue(TunnelProbe.answers("http://127.0.0.1:$port/500", 2_000), "любой ответ — сервер на связи")
            assertFalse(TunnelProbe.answers("http://127.0.0.1:${silent.localPort}/", 300), "соединение есть, ответа нет")
            assertFalse(TunnelProbe.answers("http://127.0.0.1:$closed/", 2_000))
        } finally {
            server.stop(0)
            silent.close()
        }
    }
}

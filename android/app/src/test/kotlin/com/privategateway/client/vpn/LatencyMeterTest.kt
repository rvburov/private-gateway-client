package com.privategateway.client.vpn

import android.content.Context
import androidx.datastore.preferences.core.PreferenceDataStoreFactory
import androidx.room.Room
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.privategateway.client.FakeBox
import com.privategateway.client.Fixtures
import com.privategateway.client.core.servers.Latency
import com.privategateway.client.core.servers.LatencyFailure
import com.privategateway.client.core.servers.ProxyProtocol
import com.privategateway.client.core.servers.TransportKind
import com.privategateway.client.core.vpn.TunnelState
import com.privategateway.client.core.vpn.TunnelStatus
import com.privategateway.client.core.xray.XrayConfig
import com.privategateway.client.data.AppDatabase
import com.privategateway.client.data.ServerRepository
import com.privategateway.client.data.SettingsRepository
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.async
import kotlinx.coroutines.cancel
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.After
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import org.junit.runner.RunWith
import org.robolectric.shadows.ShadowLog
import java.util.concurrent.CopyOnWriteArrayList
import kotlin.test.assertEquals
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlin.test.assertFalse

@RunWith(AndroidJUnit4::class)
class LatencyMeterTest {
    @get:Rule
    val folder = TemporaryFolder()

    private val context = ApplicationProvider.getApplicationContext<Context>()
    private val database = Room.inMemoryDatabaseBuilder(context, AppDatabase::class.java).build()
    private val scope = CoroutineScope(Dispatchers.IO + SupervisorJob())
    private val servers = ServerRepository(database, FakeBox())
    private val settings by lazy { SettingsRepository(PreferenceDataStoreFactory.create(scope = scope) { folder.root.resolve("settings.preferences_pb") }) }
    private val monitor = TunnelMonitor()
    private val engine = FakeEngine()
    private val meter by lazy { LatencyMeter(engine, servers, settings, monitor) { listOf("fe80::1%wlan0", "192.0.2.53") } }

    /** libXray без libXray: на каждый конфиг порции — задержка по порядку, вызовы и DNS записываются. */
    private class FakeEngine : CoreEngine {
        val calls = CopyOnWriteArrayList<Int>()
        val dns = CopyOnWriteArrayList<String>()

        /** Задержать ответ, пока тест не отпустит, и что сделать в это время. */
        @Volatile var during: (() -> Unit)? = null

        override fun start(configJson: String, protect: (Int) -> Boolean, dns: String): String? = null

        override fun changeDns(dns: String): String? = null

        override fun stop() = Unit

        override fun ping(request: String, dns: String): String? {
            val count = Regex("xrayJson").findAll(request).count()
            calls += count
            this.dns += dns
            during?.invoke()
            val results = (1..count).joinToString(",") { """{"success":true,"delay":${it * 10}}""" }
            return """{"success":true,"data":{"results":[$results]}}"""
        }

        override fun version(): String? = null
    }

    @After
    fun close() {
        scope.cancel()
        database.close()
    }

    private fun <T> wait(block: suspend () -> T): T = runBlocking { withTimeout(10_000) { block() } }

    /**
     * Семь серверов, что ядро умеет (VLESS и Trojan, кроме тех, что без шифрования к открытому адресу, — их ядро не
     * берет), и один Hysteria2 — его ядро не меряет.
     */
    private fun fill(): Pair<List<String>, String> {
        val all = Fixtures.servers().distinctBy { Fixtures.identity(it) }
        val supported = all.filter { (it.protocol == ProxyProtocol.Vless || it.protocol == ProxyProtocol.Trojan) && !XrayConfig.unencrypted(it) }.take(7)
        val hysteria = all.first { it.protocol == ProxyProtocol.Hysteria2 }
        wait { servers.add(supported + hysteria) }
        val items = wait { servers.servers.first { it.size == 8 } }
        val other = items.single { it.protocol == ProxyProtocol.Hysteria2 }.id
        return items.filter { it.id != other }.map { it.id } to other
    }

    @Test
    fun `a physical network change discards the result even if vpn session stays connected`() {
        val (supported, _) = fill()
        monitor.set(TunnelStatus(TunnelState.Connected, serverId = "active", since = 1))
        var current = "192.0.2.53"
        val meter = LatencyMeter(engine, servers, settings, monitor, NetworkDns { listOf(current) })
        engine.during = { current = "198.51.100.53" }
        assertEquals(MeasureResult.Busy, wait { meter.measure() })
        val items = wait { servers.servers.first() }.associateBy { it.id }
        assertTrue(supported.all { items.getValue(it).latency == null })
    }

    @Test
    fun `cancellation reaches the isolated worker and no cancelled results are saved`() {
        val (supported, _) = fill()
        val started = CompletableDeferred<Unit>()
        val release = CompletableDeferred<Unit>()
        val cancelled = CompletableDeferred<Unit>()
        val worker = object : LatencyWorker {
            override fun ping(request: String, dns: String): String? {
                started.complete(Unit)
                runBlocking { withTimeout(15_000) { release.await() } }
                return engine.ping(request, dns)
            }
            override fun cancel() { cancelled.complete(Unit) }
        }
        val measuring = LatencyMeter(worker, servers, settings, monitor, NetworkDns { emptyList() })
        val job = scope.async { measuring.measure() }
        wait { started.await() }
        try {
            wait { job.cancelAndJoin(); cancelled.await() }
        } finally {
            release.complete(Unit)
        }
        val items = wait { servers.servers.first() }.associateBy { it.id }
        assertTrue(supported.all { items.getValue(it).latency == null })
        assertEquals(false, measuring.measuring.value)
    }

    @Test
    fun `a cancelled native request is discarded instead of marking candidates unavailable`() {
        val (supported, _) = fill()
        val worker = LatencyWorker { _, _ -> throw kotlinx.coroutines.CancellationException("cancelled") }
        val measuring = LatencyMeter(worker, servers, settings, monitor, NetworkDns { emptyList() })
        kotlin.test.assertFailsWith<kotlinx.coroutines.CancellationException> { wait { measuring.measure() } }
        val items = wait { servers.servers.first() }.associateBy { it.id }
        assertTrue(supported.all { items.getValue(it).latency == null })
    }

    @Test
    fun `native errors cannot expose a short custom xhttp header to android logs`() {
        val secret = "p7!"
        val original = Fixtures.servers().first { it.protocol == ProxyProtocol.Vless && !XrayConfig.unencrypted(it) }
        val server = original.copy(transport = original.transport.copy(kind = TransportKind.Xhttp, path = "/audit",
            extraJson = """{"headers":{"X-Test":"$secret"}}"""))
        wait { servers.add(listOf(server)) }
        val worker = LatencyWorker { _, _ -> """{"success":false,"error":"failed header $secret"}""" }
        ShadowLog.clear()
        val measuring = LatencyMeter(worker, servers, settings, monitor, NetworkDns { emptyList() })
        assertEquals(MeasureResult.Done, wait { measuring.measure() })
        val logged = ShadowLog.getLogsForTag("PrivateGateway").joinToString("\n") { it.msg }
        assertTrue(logged.contains("pingBatch"))
        assertFalse(logged.contains(secret), "секрет из extra попал в Android Logcat")
    }

    @Test
    fun `servers are measured five at a time through the core and hysteria2 is not sent`() {
        val (supported, hysteria) = fill()
        assertEquals(MeasureResult.Done, wait { meter.measure() })

        assertEquals(listOf(5, 2), engine.calls, "порции по 5, в libXray — только то, что ядро умеет")
        assertEquals(listOf("192.0.2.53:53", "192.0.2.53:53"), engine.dns, "DNS сети для адресов серверов")
        val items = wait { servers.servers.first() }.associateBy { it.id }
        assertEquals(listOf(10, 20, 30, 40, 50, 10, 20), supported.map { items.getValue(it).latency?.ms })
        assertEquals(Latency.failed(LatencyFailure.Unsupported), items.getValue(hysteria).latency)
        assertEquals(LatencyProgress(8, 8), meter.progress.value)
        assertTrue(servers.latencyChecks.value.values.all { !it.stale && it.at > 0 })
    }

    @Test
    fun `isolated worker measures while protection remains active`() {
        fill()
        monitor.set(TunnelStatus(TunnelState.Connected, serverId = "x"))
        assertEquals(MeasureResult.Done, wait { meter.measure() })
        assertEquals(listOf(5, 2), engine.calls)
        assertEquals(TunnelState.Connected, monitor.status.value.state)
        assertTrue(wait { servers.servers.first() }.any { it.latency?.ms != null })
    }

    @Test
    fun `android closing the internet without vpn measures nothing and drops a chunk`() {
        // «Блокировать подключения без VPN»: все серверы «не ответили» бы из-за блока, а не сами (решение 445).
        val (supported, _) = fill()
        monitor.setBlocked(true)
        assertEquals(MeasureResult.Blocked, wait { meter.measure() })
        assertTrue(engine.calls.isEmpty())

        monitor.setBlocked(false)
        engine.during = { monitor.setBlocked(true) }
        assertEquals(MeasureResult.Blocked, wait { meter.measure() })
        assertEquals(listOf(5), engine.calls, "блок пришел во время порции — дальше порции не идут")
        val items = wait { servers.servers.first() }.associateBy { it.id }
        assertTrue(supported.all { items.getValue(it).latency == null }, "итоги порции под блоком не записаны")
    }

    @Test
    fun `a chunk measured while protection came up is dropped`() {
        val (supported, _) = fill()
        engine.during = { monitor.set(TunnelStatus(TunnelState.Connecting, serverId = supported.first())) }
        assertEquals(MeasureResult.Busy, wait { meter.measure() })

        assertEquals(listOf(5), engine.calls, "дальше порции не идут")
        val items = wait { servers.servers.first() }.associateBy { it.id }
        assertTrue(supported.all { items.getValue(it).latency == null }, "итогам порции рядом с ядром туннеля не верим")
    }

    @Test
    fun `a second tap while measuring is not counted`() {
        fill()
        val release = CompletableDeferred<Unit>()
        engine.during = { runBlocking { release.await() } }
        val first = scope.async { meter.measure() }
        wait { meter.measuring.first { it } }

        assertEquals(MeasureResult.Running, wait { meter.measure() })
        release.complete(Unit)
        engine.during = null
        assertEquals(MeasureResult.Done, wait { first.await() })
        assertEquals(false, meter.measuring.value)
    }

    @Test
    fun `core failure for a chunk is recorded as such`() {
        val (supported, _) = fill()
        val failing = object : CoreEngine by engine {
            override fun ping(request: String, dns: String): String? = """{"success":false,"error":"isolated process"}"""
        }
        val meter = LatencyMeter(failing, servers, settings, monitor) { emptyList() }
        assertEquals(MeasureResult.Done, wait { meter.measure() })
        val items = wait { servers.servers.first() }.associateBy { it.id }
        assertTrue(supported.all { items.getValue(it).latency == Latency.failed(LatencyFailure.CoreFailed) })
        assertNull(items.values.firstOrNull { it.latency?.ms != null })
    }
}

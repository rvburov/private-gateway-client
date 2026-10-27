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
import com.privategateway.client.core.vpn.TunnelState
import com.privategateway.client.core.vpn.TunnelStatus
import com.privategateway.client.data.AppDatabase
import com.privategateway.client.data.ServerRepository
import com.privategateway.client.data.SettingsRepository
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.async
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.After
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import org.junit.runner.RunWith
import java.util.concurrent.CopyOnWriteArrayList
import kotlin.test.assertEquals
import kotlin.test.assertNull
import kotlin.test.assertTrue

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

    /** Семь серверов, что ядро умеет, и один Hysteria2 — его ядро не меряет. */
    private fun fill(): Pair<List<String>, String> {
        val all = Fixtures.servers().distinctBy { Fixtures.identity(it) }
        val supported = all.filter { it.protocol == ProxyProtocol.Vless || it.protocol == ProxyProtocol.Trojan }.take(7)
        val hysteria = all.first { it.protocol == ProxyProtocol.Hysteria2 }
        wait { servers.add(supported + hysteria) }
        val items = wait { servers.servers.first { it.size == 8 } }
        val other = items.single { it.protocol == ProxyProtocol.Hysteria2 }.id
        return items.filter { it.id != other }.map { it.id } to other
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
    }

    @Test
    fun `nothing is measured under protection`() {
        fill()
        monitor.set(TunnelStatus(TunnelState.Connected, serverId = "x"))
        assertEquals(MeasureResult.Busy, wait { meter.measure() })
        assertTrue(engine.calls.isEmpty())
        assertTrue(wait { servers.servers.first() }.all { it.latency == null })
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

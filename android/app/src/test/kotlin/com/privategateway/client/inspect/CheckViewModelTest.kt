package com.privategateway.client.inspect

import android.net.Uri
import androidx.lifecycle.viewModelScope
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.privategateway.client.R
import com.privategateway.client.core.vpn.TunnelState
import com.privategateway.client.core.vpn.TunnelStatus
import com.privategateway.client.ui.Message
import com.privategateway.client.vpn.DelayBreakdown
import com.privategateway.client.vpn.JournalLevel
import com.privategateway.client.vpn.TunnelDelay
import com.privategateway.client.vpn.TunnelJournal
import com.privategateway.client.vpn.TunnelMonitor
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.async
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.job
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.test.UnconfinedTestDispatcher
import kotlinx.coroutines.test.resetMain
import kotlinx.coroutines.test.setMain
import kotlinx.coroutines.withTimeout
import kotlinx.coroutines.withTimeoutOrNull
import org.junit.After
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.atomic.AtomicInteger
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/** Модель «Проверки» без устройства: подсказки Android, задержка через туннель, повтор проверки, отчет и журнал. */
@OptIn(ExperimentalCoroutinesApi::class)
@RunWith(AndroidJUnit4::class)
class CheckViewModelTest {
    private val monitor = TunnelMonitor()
    private val journal = TunnelJournal()
    private val asked = CopyOnWriteArrayList<Boolean>()
    private var hints: (Boolean) -> PhoneHints = { PhoneHints() }
    private val measured = AtomicInteger()

    /** Замер задержки ждет, пока тест не отпустит (если задано). */
    @Volatile
    private var gate: CompletableDeferred<Unit>? = null
    private val written = CopyOnWriteArrayList<String>()

    @Volatile
    private var writes = true

    private val meter = object : TunnelDelay {
        override suspend fun measure(): DelayBreakdown? {
            measured.incrementAndGet()
            gate?.await()
            return DelayBreakdown(38, 42, 160, 1L)
        }
    }

    private val modelLazy = lazy {
        CheckViewModel(
            monitor,
            { up ->
                asked += up
                hints(up)
            },
            meter,
            journal,
            { _, text ->
                written += text
                writes
            },
        )
    }

    private val model by modelLazy

    @Before
    fun main() = Dispatchers.setMain(UnconfinedTestDispatcher())

    @After
    fun reset() {
        if (modelLazy.isInitialized()) {
            runBlocking { model.viewModelScope.coroutineContext.job.cancelAndJoin() }
        }
        Dispatchers.resetMain()
    }

    private fun <T> waitFor(read: () -> T, done: (T) -> Boolean): T = runBlocking {
        withTimeout(10_000) {
            while (!done(read())) {
                delay(20)
            }

            read()
        }
    }

    @Test
    fun `check reads android hints and measures the delay only under protection`() {
        hints = { PhoneHints(proxy = "192.0.2.10:3128") }
        model.run()
        val off = waitFor({ model.ui.value }) { !it.running && it.hints != null }
        assertEquals(PhoneHints(proxy = "192.0.2.10:3128"), off.hints)
        assertEquals(listOf(false), asked, "без туннеля чужим считается любой VPN")
        assertEquals(0, measured.get(), "без защиты задержку не меряют")
        assertNull(off.delay)

        monitor.set(TunnelStatus(TunnelState.Connected, serverId = "nl"))
        model.run()
        val on = waitFor({ model.ui.value }) { !it.running && it.delay != null }
        assertEquals(listOf(false, true), asked)
        assertEquals(1, measured.get())
        assertEquals(DelayBreakdown(38, 42, 160, 1L), on.delay)

        // Защита выключилась — прежнего замера нет: числа были бы о другом подключении.
        monitor.set(TunnelStatus())
        model.run()
        assertNull(waitFor({ model.ui.value }) { !it.running && it.delay == null }.delay)
    }

    @Test
    fun `a second press while checking checks once more right after`() {
        monitor.set(TunnelStatus(TunnelState.Connected, serverId = "nl"))
        val release = CompletableDeferred<Unit>()
        gate = release
        model.run()
        waitFor({ measured.get() }) { it == 1 }
        assertTrue(model.ui.value.running)
        model.run()
        release.complete(Unit)
        waitFor({ model.ui.value }) { !it.running }
        assertEquals(2, measured.get(), "повторное нажатие — еще одна проверка, а не две параллельно")
        assertEquals(2, asked.size)
    }

    @Test
    fun `refresh is not counted while measuring and the ping tile asks for the scroll once`() {
        monitor.set(TunnelStatus(TunnelState.Connected, serverId = "nl"))
        val release = CompletableDeferred<Unit>()
        gate = release
        model.focusDelay()
        assertTrue(waitFor({ model.ui.value }) { it.measuringDelay }.focusDelay)
        model.measureDelay()
        release.complete(Unit)
        waitFor({ model.ui.value }) { !it.measuringDelay && it.delay != null }
        assertEquals(1, measured.get())
        model.delayShown()
        assertFalse(model.ui.value.focusDelay)
    }

    @Test
    fun `report is said to be saved only when written`() = runBlocking<Unit> {
        val file = Uri.parse("content://com.example.documents/report.txt")
        val message = async(start = CoroutineStart.UNDISPATCHED) { model.messages.first() }
        model.save(file, "report")
        val saved: Message = withTimeout(10_000) { message.await() }
        assertEquals(R.string.diag_exported, saved.text)
        assertEquals(listOf("report"), written)

        writes = false
        val none = async(start = CoroutineStart.UNDISPATCHED) { withTimeoutOrNull(300) { model.messages.first() } }
        model.save(file, "again")
        assertNull(none.await(), "файл не записан — «Отчет сохранен» не показывается")
        assertEquals(listOf("report", "again"), written)
    }

    @Test
    fun `journal entries reach the screen and a failing android answer is no hint`() {
        journal.warning("no answer through the tunnel  reconnecting attempt=1")
        val entry = waitFor({ model.ui.value.journal }) { it.isNotEmpty() }.single()
        assertEquals(JournalLevel.Warning, entry.level)

        hints = { throw SecurityException("no access") }
        model.run()
        assertEquals(PhoneHints(), assertNotNull(waitFor({ model.ui.value }) { !it.running && it.hints != null }.hints))
    }
}

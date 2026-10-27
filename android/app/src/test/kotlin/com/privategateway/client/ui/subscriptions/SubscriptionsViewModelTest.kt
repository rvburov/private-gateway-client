package com.privategateway.client.ui.subscriptions

import android.content.Context
import androidx.datastore.preferences.core.PreferenceDataStoreFactory
import androidx.lifecycle.viewModelScope
import androidx.room.Room
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.privategateway.client.FakeBox
import com.privategateway.client.Fixtures
import com.privategateway.client.R
import com.privategateway.client.core.subscriptions.SubscriptionSource
import com.privategateway.client.data.AppDatabase
import com.privategateway.client.data.ServerRepository
import com.privategateway.client.data.SettingsRepository
import com.privategateway.client.subscriptions.SubscriptionUpdater
import com.privategateway.client.ui.Message
import com.privategateway.client.vpn.TunnelControl
import com.privategateway.client.vpn.TunnelMonitor
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.asCoroutineDispatcher
import kotlinx.coroutines.async
import kotlinx.coroutines.cancel
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.job
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.test.resetMain
import kotlinx.coroutines.test.setMain
import kotlinx.coroutines.withTimeout
import org.junit.After
import org.junit.Before
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import org.junit.runner.RunWith
import java.net.URI
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.Executors
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

@OptIn(ExperimentalCoroutinesApi::class)
@RunWith(AndroidJUnit4::class)
class SubscriptionsViewModelTest {
    @get:Rule
    val folder = TemporaryFolder()

    private val context = ApplicationProvider.getApplicationContext<Context>()
    private val database = Room.inMemoryDatabaseBuilder(context, AppDatabase::class.java).build()
    private val scope = CoroutineScope(Dispatchers.IO + SupervisorJob())
    private val servers = ServerRepository(database, FakeBox())
    private lateinit var settings: SettingsRepository
    private lateinit var model: SubscriptionsViewModel
    private val monitor = TunnelMonitor()
    private val control = FakeControl()
    private val messages = CopyOnWriteArrayList<Message>()

    // Главный поток — настоящий и один, как у Android: продолжения модели идут через него по очереди. С
    // UnconfinedTestDispatcher они шли прямо в потоках Room и DataStore, и снятие выбора после удаления группы иногда не
    // доходило до хранилища за 10 секунд (прогоны 358 и 360).
    private val main = Executors.newSingleThreadExecutor { Thread(it, "test-main") }.asCoroutineDispatcher()

    private class FakeControl : TunnelControl {
        @Volatile var disconnects = 0

        override fun connect() = Unit

        override fun disconnect() {
            disconnects++
        }
    }

    @Before
    fun create() {
        Dispatchers.setMain(main)
        settings = SettingsRepository(PreferenceDataStoreFactory.create(scope = scope) { folder.root.resolve("settings.preferences_pb") })
        // Сеть не нужна: с «только через VPN» без защиты загрузка не начинается.
        val source = SubscriptionSource { error("в сеть без защиты не ходим") }
        model = SubscriptionsViewModel(servers, settings, SubscriptionUpdater(source, servers, settings, monitor), monitor, control)
        scope.async { model.messages.collect { messages += it } }
    }

    @After
    fun close() {
        // Сначала — работа модели, до конца: ее продолжения идут через Dispatchers.Main, и resetMain() рядом с ними падает
        // («Dispatchers.Main is used concurrently with setting it» — отмена хранилища будила их из потока IO). Потом —
        // хранилище и база.
        runBlocking { model.viewModelScope.coroutineContext.job.cancelAndJoin() }
        scope.cancel()
        database.close()
        Dispatchers.resetMain()
        main.close()
    }

    private fun <T> wait(block: suspend () -> T): T = runBlocking { withTimeout(10_000) { block() } }

    private fun waitMessage(): Message = wait {
        while (messages.isEmpty()) {
            kotlinx.coroutines.delay(10)
        }

        messages.last()
    }

    private fun group(): String {
        wait { servers.add(Fixtures.servers().distinctBy { Fixtures.identity(it) }.take(2), "Провайдер", URI("https://example.com/sub/aaaa1111bbbb2222")) }
        return wait { model.ui.first { it.groups.isNotEmpty() } }.groups.single().id
    }

    @Test
    fun `a bad new address is refused and nothing changes`() {
        val id = group()
        val item = model.ui.value.groups.single()
        assertFalse(model.edit(item, "Новое", "ftp://example.com/sub"))
        assertEquals("Провайдер", wait { servers.groups.first() }.single { it.id == id }.name)
    }

    @Test
    fun `a new address is saved masked and checked at once`() {
        val id = group()
        val item = model.ui.value.groups.single()
        assertTrue(model.edit(item, " Новое ", "https://example.org/new/cccc3333dddd4444"))
        val edited = wait { servers.groups.first { list -> list.single().name == "Новое" } }.single()
        assertEquals("https://example.org/new/••••", edited.maskedUrl)
        assertEquals(id, edited.id)
        // Проверка загрузкой сразу — без защиты ее не будет, и окно это скажет.
        assertEquals(R.string.subs_blocked_off, waitMessage().text)
    }

    @Test
    fun `removing the group in use disconnects and clears the choice`() {
        group()
        val selected = wait { servers.servers.first { it.size == 2 } }.first().id
        wait { settings.select(selected) }

        model.remove(model.ui.value.groups.single().id, "Провайдер", inUse = true)
        assertEquals(1, control.disconnects)
        assertTrue(wait { servers.servers.first { it.isEmpty() } }.isEmpty())
        assertNull(wait { settings.selectedServerId.first { it == null } })
        val message = waitMessage()
        assertEquals(R.string.subs_removed, message.text)
        assertEquals(listOf("Провайдер"), message.args.toList())
    }
}

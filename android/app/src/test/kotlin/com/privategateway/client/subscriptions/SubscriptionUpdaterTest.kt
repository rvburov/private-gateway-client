package com.privategateway.client.subscriptions

import android.content.Context
import androidx.datastore.preferences.core.PreferenceDataStoreFactory
import androidx.room.Room
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.privategateway.client.FakeBox
import com.privategateway.client.Fixtures
import com.privategateway.client.core.links.LinkWriter
import com.privategateway.client.core.servers.ServerDefinition
import com.privategateway.client.core.settings.SubscriptionSettings
import com.privategateway.client.core.subscriptions.DownloadFailure
import com.privategateway.client.core.subscriptions.FailureReason
import com.privategateway.client.core.subscriptions.SubscriptionContent
import com.privategateway.client.core.subscriptions.SubscriptionDownloadException
import com.privategateway.client.core.subscriptions.SubscriptionSource
import com.privategateway.client.core.subscriptions.UpdateBlock
import com.privategateway.client.core.vpn.TunnelState
import com.privategateway.client.core.vpn.TunnelStatus
import com.privategateway.client.data.AppDatabase
import com.privategateway.client.data.ServerRepository
import com.privategateway.client.data.SettingsRepository
import com.privategateway.client.vpn.TunnelMonitor
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
import java.net.URI
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.atomic.AtomicInteger
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

@RunWith(AndroidJUnit4::class)
class SubscriptionUpdaterTest {
    @get:Rule
    val folder = TemporaryFolder()

    private val context = ApplicationProvider.getApplicationContext<Context>()
    private val database = Room.inMemoryDatabaseBuilder(context, AppDatabase::class.java).build()
    private val scope = CoroutineScope(Dispatchers.IO + SupervisorJob())
    private val servers = ServerRepository(database, FakeBox())
    private val settings by lazy { SettingsRepository(PreferenceDataStoreFactory.create(scope = scope) { folder.root.resolve("settings.preferences_pb") }) }
    private val monitor = TunnelMonitor()
    private val source = FakeSource()
    private val updater by lazy { SubscriptionUpdater(source, servers, settings, monitor) }
    private val catalog = Fixtures.servers().distinctBy { Fixtures.identity(it) }
    private val url = URI("https://example.com/sub/0123456789abcdef0123456789abcdef")

    /** Подписка без сети: заданный ответ или неудача; [gate] держит загрузку, пока тест не отпустит. */
    private class FakeSource : SubscriptionSource {
        @Volatile var content: SubscriptionContent? = null

        @Volatile var failure: SubscriptionDownloadException? = null

        @Volatile var gate: CompletableDeferred<Unit>? = null
        val calls = AtomicInteger()

        override fun download(url: URI): SubscriptionContent {
            calls.incrementAndGet()
            gate?.let { runBlocking { it.await() } }
            failure?.let { throw it }
            return requireNotNull(content)
        }
    }

    @After
    fun close() {
        scope.cancel()
        database.close()
    }

    private fun <T> wait(block: suspend () -> T): T = runBlocking { withTimeout(10_000) { block() } }

    private fun serve(list: List<ServerDefinition>) {
        source.content = SubscriptionContent(list.joinToString("\n") { LinkWriter.write(it) }, "Провайдер")
    }

    /** Подписка из трех серверов, выбран второй; номер подписки и номера серверов. */
    private fun subscribe(): Pair<String, List<String>> {
        wait { servers.add(catalog.take(3), "Провайдер", url) }
        val group = wait { servers.groups.first() }.single().id
        val ids = wait { servers.servers.first { it.size == 3 } }.map { it.id }
        wait { settings.select(ids[1]) }
        return group to ids
    }

    private fun connect() = monitor.set(TunnelStatus(TunnelState.Connected, serverId = "x"))

    @Test
    fun `only via vpn nothing is downloaded while protection is off`() {
        val (group, _) = subscribe()
        serve(catalog.take(3))
        assertEquals(UpdateOutcome.Blocked(UpdateBlock.ProtectionOff), wait { updater.update(group) })
        assertEquals(0, source.calls.get(), "адрес подписки не ушел в сеть мимо туннеля")

        wait { settings.setSubscriptions(SubscriptionSettings(onlyViaVpn = false)) }
        assertEquals(UpdateOutcome.Updated(0, 0), wait { updater.update(group) }, "настройку выключили — грузится напрямую")
        assertEquals(1, source.calls.get())
    }

    @Test
    fun `update under protection replaces the list and keeps the selected server marked`() {
        val (group, ids) = subscribe()
        connect()
        val notices = CopyOnWriteArrayList<SubscriptionNotice>()
        val listening = scope.async { updater.notices.collect { notices += it } }
        // Пришли первый и новый четвертый; третий ушел, второй ушел, но выбран — остается с пометкой.
        serve(listOf(catalog[0], catalog[3]))

        assertEquals(UpdateOutcome.Updated(1, 1), wait { updater.update(group) })
        val after = wait { servers.servers.first() }
        assertEquals(setOf(ids[0], ids[1]), after.map { it.id }.filter { it in ids }.toSet())
        assertNotNull(after.single { it.id == ids[1] }.missingSince)
        val subscription = wait { servers.groups.first() }.single()
        assertNotNull(subscription.updatedAt)
        assertNull(subscription.failure)
        wait { while (notices.isEmpty()) kotlinx.coroutines.delay(10) }
        assertEquals(listOf<SubscriptionNotice>(SubscriptionNotice.Gone(catalog[1].name, "Провайдер")), notices.toList())
        listening.cancel()
    }

    @Test
    fun `failure keeps the old list and remembers the reason with its code`() {
        val (group, ids) = subscribe()
        connect()
        source.failure = SubscriptionDownloadException(DownloadFailure.Status, 403)

        assertEquals(UpdateOutcome.Failed(FailureReason.Forbidden(403)), wait { updater.update(group) })
        assertEquals(ids, wait { servers.servers.first() }.map { it.id }, "прошлый список оставлен")
        val subscription = wait { servers.groups.first() }.single()
        assertEquals(DownloadFailure.Status, subscription.failure)
        assertEquals(403, subscription.status)
        assertNotNull(subscription.lastAttemptAt)
    }

    @Test
    fun `scheduled failure is reported and a tap is not`() {
        val (group, _) = subscribe()
        connect()
        source.failure = SubscriptionDownloadException(DownloadFailure.Timeout)
        val notices = CopyOnWriteArrayList<SubscriptionNotice>()
        val listening = scope.async { updater.notices.collect { notices += it } }

        wait { updater.update(group) }
        wait { kotlinx.coroutines.delay(100) }
        assertTrue(notices.isEmpty(), "кнопкой — итог на экране, без сообщения «не обновились»")

        // Пора: успех и попытка — давно.
        wait { database.subscriptions().update(database.subscriptions().byId(group)!!.copy(updatedAt = 0, lastAttemptAt = 0)) }
        wait { updater.updateDue() }
        wait { while (notices.isEmpty()) kotlinx.coroutines.delay(10) }
        assertEquals(listOf<SubscriptionNotice>(SubscriptionNotice.Stale("Провайдер", FailureReason.Timeout)), notices.toList())
        listening.cancel()
    }

    @Test
    fun `not due means not downloaded`() {
        subscribe()
        connect()
        serve(catalog.take(3))
        wait { updater.updateDue() }
        assertEquals(0, source.calls.get(), "подписку только что добавили — обновлять рано")
    }

    @Test
    fun `selected server notices follow the setting`() {
        val (group, _) = subscribe()
        connect()
        wait { settings.setSubscriptions(SubscriptionSettings(notifySelected = false)) }
        val notices = CopyOnWriteArrayList<SubscriptionNotice>()
        val listening = scope.async { updater.notices.collect { notices += it } }
        serve(listOf(catalog[0]))

        wait { updater.update(group) }
        wait { kotlinx.coroutines.delay(100) }
        assertTrue(notices.isEmpty())
        listening.cancel()
    }

    @Test
    fun `a second tap while updating is not counted`() {
        val (group, _) = subscribe()
        connect()
        serve(catalog.take(3))
        val gate = CompletableDeferred<Unit>()
        source.gate = gate
        val first = scope.async { updater.update(group) }
        wait { updater.busy.first { group in it } }

        assertEquals(UpdateOutcome.Blocked(UpdateBlock.Busy), wait { updater.update(group) })
        gate.complete(Unit)
        assertEquals(UpdateOutcome.Updated(0, 0), wait { first.await() })
        assertTrue(updater.busy.value.isEmpty())
        assertEquals(1, source.calls.get())
    }
}

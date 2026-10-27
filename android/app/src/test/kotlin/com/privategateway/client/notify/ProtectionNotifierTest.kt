package com.privategateway.client.notify

import android.Manifest
import android.app.Application
import android.app.NotificationManager
import android.content.Context
import androidx.datastore.preferences.core.PreferenceDataStoreFactory
import androidx.room.Room
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.privategateway.client.FakeBox
import com.privategateway.client.PrivateGatewayApp
import com.privategateway.client.R
import com.privategateway.client.core.notify.Notice
import com.privategateway.client.core.notify.NoticeAction
import com.privategateway.client.core.notify.NoticeKind
import com.privategateway.client.core.notify.NotificationLevel
import com.privategateway.client.core.notify.NotificationPolicy
import com.privategateway.client.core.subscriptions.FailureReason
import com.privategateway.client.core.subscriptions.SelectedChange
import com.privategateway.client.core.subscriptions.SubscriptionSource
import com.privategateway.client.core.vpn.TunnelErrors
import com.privategateway.client.core.vpn.TunnelState
import com.privategateway.client.core.vpn.TunnelStatus
import com.privategateway.client.data.AppDatabase
import com.privategateway.client.data.ServerRepository
import com.privategateway.client.data.SettingsRepository
import com.privategateway.client.subscriptions.SubscriptionUpdater
import com.privategateway.client.vpn.TunnelMonitor
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.After
import org.junit.Before
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import org.junit.runner.RunWith
import org.robolectric.Shadows.shadowOf
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertTrue

/** Уведомления по смене состояния туннеля (доска 22): в шторку, только при скрытом окне, по уровню, прошедшее убирается. */
@RunWith(AndroidJUnit4::class)
class ProtectionNotifierTest {
    @get:Rule
    val folder = TemporaryFolder()

    private val context = ApplicationProvider.getApplicationContext<Context>()
    private val database = Room.inMemoryDatabaseBuilder(context, AppDatabase::class.java).build()
    private val scope = CoroutineScope(Dispatchers.IO + SupervisorJob())
    private val servers = ServerRepository(database, FakeBox())
    private val monitor = TunnelMonitor()
    private val window = AppWindow()
    private val manager = context.getSystemService(NotificationManager::class.java)
    private lateinit var settings: SettingsRepository
    private lateinit var notifier: ProtectionNotifier

    @Before
    fun create() {
        shadowOf(context as Application).grantPermissions(Manifest.permission.POST_NOTIFICATIONS)
        PrivateGatewayApp.createChannels(context)
        settings = SettingsRepository(PreferenceDataStoreFactory.create(scope = scope) { folder.root.resolve("settings.preferences_pb") })
        val updater = SubscriptionUpdater(SubscriptionSource { error("в сеть не ходим") }, servers, settings, monitor)
        monitor.set(TunnelStatus(TunnelState.Connected, serverId = "nl"))
        notifier = ProtectionNotifier(context, monitor, servers, settings, updater, window)
        notifier.start()
    }

    @After
    fun close() {
        scope.cancel()
        database.close()
    }

    private fun shown(): List<String> = shadowOf(manager).allNotifications.map { shadowOf(it).contentTitle.toString() }

    private fun waitFor(condition: () -> Boolean) = runBlocking {
        withTimeout(10_000) {
            while (!condition()) {
                delay(20)
            }
        }
    }

    /** Ничего не пришло: подождать, сколько хватило бы уведомлению, и проверить. */
    private fun settle() = runBlocking { delay(300) }

    @Test
    fun `drop goes to the shade with buttons and goes away when protection is back`() {
        monitor.set(TunnelStatus(TunnelState.Reconnecting, serverId = "nl", attempt = 1))
        waitFor { shown().isNotEmpty() }
        val lost = shadowOf(manager).allNotifications.single()
        assertEquals(context.getString(R.string.notify_lost_title), shadowOf(lost).contentTitle.toString())
        assertEquals(PrivateGatewayApp.CHANNEL_ALERTS, lost.channelId, "важное — каналом сбоев")
        assertEquals(
            listOf(context.getString(R.string.notify_action_changeServer), context.getString(R.string.notify_action_open)),
            lost.actions.map { it.title.toString() },
        )

        // «Только важные» (по умолчанию): «Подключено» не показывается, а «Соединение потеряно» убирается.
        monitor.set(TunnelStatus(TunnelState.Connected, serverId = "nl"))
        waitFor { shown().isEmpty() }
    }

    @Test
    fun `window on the screen keeps the shade empty`() {
        window.set(true)
        monitor.set(TunnelStatus(TunnelState.Reconnecting, serverId = "nl", attempt = 1))
        settle()
        assertTrue(shown().isEmpty(), "окно показывает «Переподключение» само")
    }

    @Test
    fun `level off keeps the shade empty and all shows a manual disconnect`() {
        runBlocking { settings.setNotifications(NotificationLevel.Off) }
        monitor.set(TunnelStatus(TunnelState.Error, serverId = "nl", error = TunnelErrors.CORE_FAILED))
        settle()
        assertTrue(shown().isEmpty())

        runBlocking { settings.setNotifications(NotificationLevel.All) }
        monitor.set(TunnelStatus(TunnelState.Off))
        waitFor { shown().isNotEmpty() }
        val off = shadowOf(manager).allNotifications.single()
        assertEquals(context.getString(R.string.notify_off_title), shadowOf(off).contentTitle.toString())
        assertEquals(PrivateGatewayApp.CHANNEL_EVENTS, off.channelId, "обычное — каналом событий")
    }

    @Test
    fun `failure text has the reason and the code`() {
        monitor.set(TunnelStatus(TunnelState.Error, serverId = "nl", error = TunnelErrors.CORE_FAILED))
        waitFor { shown().isNotEmpty() }
        val text = shadowOf(shadowOf(manager).allNotifications.single()).contentText.toString()
        assertTrue(text.contains(context.getString(R.string.error_PGC_SVC_002) + " · " + TunnelErrors.CORE_FAILED), text)
    }

    @Test
    fun `every text the policy gives has a resource and the arguments`() {
        val statuses = TunnelState.entries.flatMap { state ->
            listOf(null, TunnelErrors.SERVER_NOT_RESPONDING, TunnelErrors.CORE_FAILED).map { TunnelStatus(state, error = it) }
        }
        val notices = mutableListOf<Notice>()
        for (before in statuses) {
            for (after in statuses) {
                for (lockdown in listOf(false, true)) {
                    NotificationPolicy.forChange(before, after, "Нидерланды 01", lockdown)?.let { notices += it }
                }
            }
        }

        notices += NotificationPolicy.forSelected(SelectedChange.Missing, "Германия 02", "Основная")!!
        notices += NotificationPolicy.forSelected(SelectedChange.Changed, "Германия 02", "Основная")!!
        notices += NotificationPolicy.forStale("Основная", FailureReason.Forbidden(403))
        assertEquals(NoticeKind.entries.toSet(), notices.map { it.kind }.toSet(), "перебор дал все виды")
        for (notice in notices) {
            val text = assertNotNull(notifier.text(notice), notice.textKey)
            assertTrue(!text.contains("{") && !text.contains("%"), "подстановки заполнены: $text")
        }

        val stale = notifier.text(NotificationPolicy.forStale("Основная", FailureReason.Forbidden(403)))!!
        assertTrue(stale.contains("Основная") && stale.contains("403"), stale)
        NoticeAction.entries.forEach { context.getString(NoticeTexts.action(it)) }
    }
}

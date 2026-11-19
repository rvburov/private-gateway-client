package com.privategateway.client.updates

import android.Manifest
import android.app.Application
import android.app.NotificationManager
import android.content.Context
import androidx.datastore.preferences.core.PreferenceDataStoreFactory
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.privategateway.client.MainActivity
import com.privategateway.client.PrivateGatewayApp
import com.privategateway.client.R
import com.privategateway.client.core.notify.NotificationLevel
import com.privategateway.client.data.SettingsRepository
import com.privategateway.client.notify.AppWindow
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.job
import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Before
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import org.junit.runner.RunWith
import org.robolectric.Shadows.shadowOf
import kotlin.test.assertEquals

/** Уведомление «Доступно обновление»: один раз о версии, только при «Все», не при открытом окне; нажатие — «О программе». */
@RunWith(AndroidJUnit4::class)
class UpdateNotifierTest {
    @get:Rule
    val folder = TemporaryFolder()

    private val context = ApplicationProvider.getApplicationContext<Context>()
    private val scope = CoroutineScope(Dispatchers.IO + SupervisorJob())
    private val window = AppWindow()
    private val manager = context.getSystemService(NotificationManager::class.java)
    private lateinit var settings: SettingsRepository
    private lateinit var notifier: UpdateNotifier

    @Before
    fun create() {
        shadowOf(context as Application).grantPermissions(Manifest.permission.POST_NOTIFICATIONS)
        PrivateGatewayApp.createChannels(context)
        settings = SettingsRepository(PreferenceDataStoreFactory.create(scope = scope) { folder.root.resolve("settings.preferences_pb") })
        notifier = UpdateNotifier(context, settings, window)
    }

    @After
    fun close() = runBlocking { scope.coroutineContext.job.cancelAndJoin() }

    private fun shown(): List<String> = shadowOf(manager).allNotifications.map { shadowOf(it).contentTitle.toString() }

    @Test
    fun `new version is told once and the tap opens about`() = runBlocking {
        settings.setNotifications(NotificationLevel.All)
        notifier.available("0.3.2")
        notifier.available("0.3.2")
        assertEquals(listOf(context.getString(R.string.notify_update_title, "0.3.2")), shown())
        val notification = shadowOf(manager).allNotifications.single()
        assertEquals(PrivateGatewayApp.CHANNEL_EVENTS, notification.channelId)
        assertEquals(context.getString(R.string.android_notify_update_text), shadowOf(notification).contentText.toString())
        val tap = shadowOf(notification.contentIntent).savedIntent
        assertEquals(MainActivity.TAB_ABOUT, tap.getStringExtra(MainActivity.EXTRA_TAB))

        notifier.available("0.3.3")
        assertEquals(listOf(context.getString(R.string.notify_update_title, "0.3.3")), shown(), "о следующей версии — снова, вместо прежнего")
    }

    @Test
    fun `open window waits and only important notifications stay quiet for good`() = runBlocking {
        settings.setNotifications(NotificationLevel.All)
        window.set(true)
        notifier.available("0.3.2")
        assertEquals(emptyList(), shown(), "окно на экране — карточка и метка видны и так")
        window.set(false)
        notifier.available("0.3.2")
        assertEquals(1, shown().size, "окно закрыли — сказать можно")

        settings.setNotifications(NotificationLevel.ImportantOnly)
        notifier.available("0.3.3")
        notifier.available("0.3.3")
        assertEquals(1, shown().size, "обновление — обычное событие: «Только важные» его не показывает")
    }
}

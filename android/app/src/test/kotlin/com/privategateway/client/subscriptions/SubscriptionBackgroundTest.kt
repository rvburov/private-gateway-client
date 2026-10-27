package com.privategateway.client.subscriptions

import android.content.Context
import androidx.datastore.preferences.core.PreferenceDataStoreFactory
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.work.NetworkType
import androidx.work.WorkInfo
import androidx.work.WorkManager
import androidx.work.testing.WorkManagerTestInitHelper
import com.privategateway.client.core.settings.SubscriptionSettings
import com.privategateway.client.core.subscriptions.RefreshInterval
import com.privategateway.client.data.SettingsRepository
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
import java.util.concurrent.TimeUnit
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull

/** Фоновая проверка подписок (решение 443): задача WorkManager стоит, только пока автообновление не ждет защиты. */
@RunWith(AndroidJUnit4::class)
class SubscriptionBackgroundTest {
    @get:Rule
    val folder = TemporaryFolder()

    private val context = ApplicationProvider.getApplicationContext<Context>()
    private val scope = CoroutineScope(Dispatchers.IO + SupervisorJob())

    @Before
    fun create() {
        WorkManagerTestInitHelper.initializeTestWorkManager(context)
    }

    @After
    fun close() {
        scope.cancel()
    }

    private fun work(): WorkInfo? = WorkManager.getInstance(context).getWorkInfosForUniqueWork(SubscriptionWork.NAME).get().singleOrNull()

    private fun waitFor(condition: () -> Boolean) = runBlocking {
        withTimeout(10_000) {
            while (!condition()) {
                delay(20)
            }
        }
    }

    @Test
    fun `scheduled work runs every hour on any network and goes away when not wanted`() {
        SubscriptionBackground.schedule(context, true)
        val info = assertNotNull(work())
        assertEquals(WorkInfo.State.ENQUEUED, info.state)
        assertEquals(NetworkType.CONNECTED, info.constraints.requiredNetworkType, "без сети проверять нечего")
        assertEquals(TimeUnit.HOURS.toMillis(1), info.periodicityInfo?.repeatIntervalMillis)

        SubscriptionBackground.schedule(context, true)
        assertEquals(info.id, work()?.id, "повтор не сбивает расписание — та же задача")

        SubscriptionBackground.schedule(context, false)
        assertEquals(WorkInfo.State.CANCELLED, work()?.state)
    }

    @Test
    fun `work follows the subscription settings`() {
        val settings = SettingsRepository(PreferenceDataStoreFactory.create(scope = scope) { folder.root.resolve("settings.preferences_pb") })
        SubscriptionBackground(context, settings).start()

        // По умолчанию — только через VPN: без защиты в фоне грузить нечего.
        runBlocking { delay(300) }
        assertNull(work())

        runBlocking { settings.setSubscriptions(SubscriptionSettings(onlyViaVpn = false)) }
        waitFor { work()?.state == WorkInfo.State.ENQUEUED }

        runBlocking { settings.setSubscriptions(SubscriptionSettings(RefreshInterval.Off, onlyViaVpn = false)) }
        waitFor { work()?.state == WorkInfo.State.CANCELLED }
    }
}

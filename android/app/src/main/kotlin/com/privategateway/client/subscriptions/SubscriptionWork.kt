package com.privategateway.client.subscriptions

import android.content.Context
import androidx.work.Constraints
import androidx.work.CoroutineWorker
import androidx.work.ExistingPeriodicWorkPolicy
import androidx.work.NetworkType
import androidx.work.PeriodicWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.WorkerParameters
import com.privategateway.client.data.SettingsRepository
import com.privategateway.client.di.AppEntryPoint
import dagger.hilt.android.EntryPointAccessors
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.launch
import java.util.concurrent.TimeUnit
import javax.inject.Inject
import javax.inject.Singleton

/**
 * Проверка подписок в фоне, когда процесс приложения не живет (решение 443): WorkManager раз в час при сети спрашивает
 * [SubscriptionUpdater.updateDue] — то же правило «пора», что и в процессе. Неудача — то же уведомление «не обновились».
 */
class SubscriptionWork(context: Context, params: WorkerParameters) : CoroutineWorker(context, params) {
    override suspend fun doWork(): Result {
        EntryPointAccessors.fromApplication(applicationContext, AppEntryPoint::class.java).updater().updateDue()
        return Result.success()
    }

    companion object {
        const val NAME = "subscriptions"
        const val EVERY_HOURS = 1L
    }
}

/**
 * Ставит и снимает [SubscriptionWork] по настройкам «Профилей и подписок»: нужна только при автообновлении и выключенном
 * «Обновлять только через VPN» (SubscriptionSettings.background).
 */
@Singleton
class SubscriptionBackground @Inject constructor(
    @param:ApplicationContext private val context: Context,
    private val settings: SettingsRepository,
) {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private var started = false

    @Synchronized
    fun start() {
        if (started) {
            return
        }

        started = true
        scope.launch {
            settings.subscriptions.map { it.background }.distinctUntilChanged().collect { schedule(context, it) }
        }
    }

    companion object {
        fun schedule(context: Context, wanted: Boolean) {
            val work = WorkManager.getInstance(context)
            if (!wanted) {
                work.cancelUniqueWork(SubscriptionWork.NAME)
                return
            }

            val request = PeriodicWorkRequestBuilder<SubscriptionWork>(SubscriptionWork.EVERY_HOURS, TimeUnit.HOURS)
                .setConstraints(Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build())
                .build()
            work.enqueueUniquePeriodicWork(SubscriptionWork.NAME, ExistingPeriodicWorkPolicy.KEEP, request)
        }
    }
}

package com.privategateway.client.updates

import android.Manifest
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.content.ContextCompat
import androidx.work.Constraints
import androidx.work.CoroutineWorker
import androidx.work.ExistingPeriodicWorkPolicy
import androidx.work.NetworkType
import androidx.work.PeriodicWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.WorkerParameters
import com.privategateway.client.MainActivity
import com.privategateway.client.PrivateGatewayApp
import com.privategateway.client.R
import com.privategateway.client.core.notify.NotificationLevel
import com.privategateway.client.data.SettingsRepository
import com.privategateway.client.di.AppEntryPoint
import com.privategateway.client.notify.AppWindow
import dagger.hilt.android.EntryPointAccessors
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.flow.first
import java.util.concurrent.TimeUnit
import javax.inject.Inject

/** Куда сказать о новой версии, найденной без пользователя (фоновая проверка). */
interface UpdateAlerts {
    /** Нашлась версия [version], о которой еще не говорили. */
    suspend fun available(version: String)

    companion object {
        /** Тесты, где уведомление не важно. */
        val NONE: UpdateAlerts = object : UpdateAlerts {
            override suspend fun available(version: String) = Unit
        }
    }
}

/**
 * Уведомление «Доступно обновление X» (как notify.update.* десктопа): обычное событие — только при уровне «Все», канал
 * «События», без звука; окно на экране — не нужно, карточка «О программе» и отметка в «Настройках» видны и так. Нажатие —
 * «Настройки» → «О программе», где «Скачать и установить». О каждой версии — один раз.
 */
class UpdateNotifier @Inject constructor(
    @param:ApplicationContext private val context: Context,
    private val settings: SettingsRepository,
    private val window: AppWindow,
) : UpdateAlerts {
    override suspend fun available(version: String) {
        // Окно на экране — сказать позже: карточку «О программе» могли и не открыть.
        if (window.visible.value || settings.updateNotified.first() == version) {
            return
        }

        settings.setUpdateNotified(version)
        if (settings.notifications.first() != NotificationLevel.All) {
            return
        }

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) {
            return
        }

        val open = PendingIntent.getActivity(
            context,
            ID,
            Intent(context, MainActivity::class.java)
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP)
                .putExtra(MainActivity.EXTRA_TAB, MainActivity.TAB_ABOUT),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
        val title = context.getString(R.string.notify_update_title, version)
        val notification = NotificationCompat.Builder(context, PrivateGatewayApp.CHANNEL_EVENTS)
            .setSmallIcon(R.drawable.pg_notification)
            .setContentTitle(title)
            .setContentText(context.getString(R.string.android_notify_update_text))
            .setContentIntent(open)
            .setAutoCancel(true)
            .setCategory(NotificationCompat.CATEGORY_RECOMMENDATION)
            .build()
        NotificationManagerCompat.from(context).notify(TAG, ID, notification)
    }

    companion object {
        const val TAG = "update"
        const val ID = 11
    }
}

/**
 * Проверка обновления в фоне, когда окно не открывают (решение 731): WorkManager раз в 12 часов при сети — то же правило
 * «пора» и та же раскатка по номеру установки, что при показе окна ([AppUpdates.backgroundCheck]).
 */
class UpdateWork(context: Context, params: WorkerParameters) : CoroutineWorker(context, params) {
    override suspend fun doWork(): Result {
        EntryPointAccessors.fromApplication(applicationContext, AppEntryPoint::class.java).updates().backgroundCheck()
        return Result.success()
    }

    companion object {
        const val NAME = "updates"
        const val EVERY_HOURS = 12L

        fun schedule(context: Context) {
            val request = PeriodicWorkRequestBuilder<UpdateWork>(EVERY_HOURS, TimeUnit.HOURS)
                .setConstraints(Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build())
                .build()
            WorkManager.getInstance(context).enqueueUniquePeriodicWork(NAME, ExistingPeriodicWorkPolicy.KEEP, request)
        }
    }
}

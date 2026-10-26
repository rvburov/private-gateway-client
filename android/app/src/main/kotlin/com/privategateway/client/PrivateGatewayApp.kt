package com.privategateway.client

import android.app.Application
import androidx.core.app.NotificationChannelCompat
import androidx.core.app.NotificationManagerCompat
import com.privategateway.client.di.AppEntryPoint
import dagger.hilt.android.EntryPointAccessors
import dagger.hilt.android.HiltAndroidApp

@HiltAndroidApp
class PrivateGatewayApp : Application() {
    override fun onCreate() {
        super.onCreate()

        // Канал постоянного уведомления туннеля: без звука, только состояние и «Отключить». Сбои — отдельным каналом со
        // звуком: защита не включилась, и пользователь должен это заметить.
        val tunnel = NotificationChannelCompat.Builder(CHANNEL_TUNNEL, NotificationManagerCompat.IMPORTANCE_LOW)
            .setName(getString(R.string.android_notify_channel))
            .setDescription(getString(R.string.android_notify_channel_text))
            .setShowBadge(false)
            .build()
        val alerts = NotificationChannelCompat.Builder(CHANNEL_ALERTS, NotificationManagerCompat.IMPORTANCE_DEFAULT)
            .setName(getString(R.string.android_notify_alerts))
            .setDescription(getString(R.string.android_notify_alerts_text))
            .build()
        NotificationManagerCompat.from(this).createNotificationChannelsCompat(listOf(tunnel, alerts))

        // Подписки обновляются в процессе приложения: на переходе в «Защищено» и по расписанию (решение 434).
        EntryPointAccessors.fromApplication(this, AppEntryPoint::class.java).updater().start()
    }

    companion object {
        const val CHANNEL_TUNNEL = "tunnel"
        const val CHANNEL_ALERTS = "alerts"
    }
}

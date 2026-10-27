package com.privategateway.client

import android.app.Application
import android.content.Context
import androidx.core.app.NotificationChannelCompat
import androidx.core.app.NotificationManagerCompat
import com.privategateway.client.di.AppEntryPoint
import dagger.hilt.android.EntryPointAccessors
import dagger.hilt.android.HiltAndroidApp

@HiltAndroidApp
class PrivateGatewayApp : Application() {
    override fun onCreate() {
        super.onCreate()

        createChannels(this)

        // Подписки обновляются в процессе приложения: на переходе в «Защищено» и по расписанию (решение 434); уведомления
        // слушают туннель и подписки там же — и без окна.
        val entry = EntryPointAccessors.fromApplication(this, AppEntryPoint::class.java)
        entry.updater().start()
        entry.notifier().start()
    }

    companion object {
        /** Каналы уведомлений; повторное создание ничего не меняет (настройки пользователя у каналов остаются). */
        fun createChannels(context: Context) {
            // Канал постоянного уведомления туннеля: без звука, только состояние и «Отключить». Сбои — отдельным каналом со
            // звуком: защита не включилась, и пользователь должен это заметить.
            val tunnel = NotificationChannelCompat.Builder(CHANNEL_TUNNEL, NotificationManagerCompat.IMPORTANCE_LOW)
                .setName(context.getString(R.string.android_notify_channel))
                .setDescription(context.getString(R.string.android_notify_channel_text))
                .setShowBadge(false)
                .build()
            val alerts = NotificationChannelCompat.Builder(CHANNEL_ALERTS, NotificationManagerCompat.IMPORTANCE_DEFAULT)
                .setName(context.getString(R.string.android_notify_alerts))
                .setDescription(context.getString(R.string.android_notify_alerts_text))
                .build()
            // Обычные события (доска 22): подключено, отключено, сервер подписки пропал или изменился — тише сбоев.
            val events = NotificationChannelCompat.Builder(CHANNEL_EVENTS, NotificationManagerCompat.IMPORTANCE_LOW)
                .setName(context.getString(R.string.android_notify_events))
                .setDescription(context.getString(R.string.android_notify_events_text))
                .build()
            NotificationManagerCompat.from(context).createNotificationChannelsCompat(listOf(tunnel, alerts, events))
        }

        const val CHANNEL_TUNNEL = "tunnel"
        const val CHANNEL_ALERTS = "alerts"
        const val CHANNEL_EVENTS = "events"
    }
}

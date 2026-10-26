package com.privategateway.client

import android.app.Application
import androidx.core.app.NotificationChannelCompat
import androidx.core.app.NotificationManagerCompat
import dagger.hilt.android.HiltAndroidApp

@HiltAndroidApp
class PrivateGatewayApp : Application() {
    override fun onCreate() {
        super.onCreate()

        // Канал постоянного уведомления туннеля: без звука, только состояние и «Отключить».
        val channel = NotificationChannelCompat.Builder(CHANNEL_TUNNEL, NotificationManagerCompat.IMPORTANCE_LOW)
            .setName(getString(R.string.android_notify_channel))
            .setDescription(getString(R.string.android_notify_channel_text))
            .setShowBadge(false)
            .build()
        NotificationManagerCompat.from(this).createNotificationChannel(channel)
    }

    companion object {
        const val CHANNEL_TUNNEL = "tunnel"
    }
}

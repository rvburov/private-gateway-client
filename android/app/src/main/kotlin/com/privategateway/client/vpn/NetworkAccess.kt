package com.privategateway.client.vpn

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.os.Build
import dagger.hilt.android.qualifiers.ApplicationContext
import javax.inject.Inject
import javax.inject.Singleton

/**
 * Закрыта ли сеть приложению самим Android (решение 445): сеть по умолчанию приложения и ее блокировка
 * (`onBlockedStatusChanged`, Android 10+). Без туннеля при «Блокировать подключения без VPN» Android закрывает сеть всем
 * приложениям, и нашему тоже, — окно говорит об этом, а замер задержки не выдает все серверы за неответившие. Под защитой
 * сеть приложения — туннель, она открыта. Android 9 не сообщает — считается открытой.
 */
@Singleton
class NetworkAccess @Inject constructor(
    @param:ApplicationContext private val context: Context,
    private val monitor: TunnelMonitor,
) {
    private var started = false

    @Synchronized
    fun start() {
        if (started || Build.VERSION.SDK_INT < Build.VERSION_CODES.Q) {
            return
        }

        started = true
        context.getSystemService(ConnectivityManager::class.java).registerDefaultNetworkCallback(
            object : ConnectivityManager.NetworkCallback() {
                override fun onBlockedStatusChanged(network: Network, blocked: Boolean) {
                    monitor.setBlocked(blocked)
                }

                override fun onLost(network: Network) {
                    // Сети нет вовсе — это не блок Android: окно и так покажет, что связи нет.
                    monitor.setBlocked(false)
                }
            },
        )
    }
}

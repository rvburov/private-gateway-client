package com.privategateway.client.vpn

import android.app.Notification
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.net.ConnectivityManager
import android.net.VpnService
import android.os.Build
import android.os.ParcelFileDescriptor
import android.os.SystemClock
import android.util.Log
import androidx.core.app.NotificationCompat
import androidx.core.app.ServiceCompat
import androidx.core.content.ContextCompat
import com.privategateway.client.MainActivity
import com.privategateway.client.PrivateGatewayApp
import com.privategateway.client.R
import com.privategateway.client.core.text.SecretMasker
import com.privategateway.client.core.vpn.CoreDns
import com.privategateway.client.core.vpn.TunPlan
import com.privategateway.client.core.xray.CoreSettings
import com.privategateway.client.core.xray.XrayConfig
import com.privategateway.client.data.ServerRepository
import com.privategateway.client.data.SettingsRepository
import dagger.hilt.android.AndroidEntryPoint
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import javax.inject.Inject

/**
 * Туннель защиты: системный VPN Android. Весь трафик телефона идет в карту TUN ([TunPlan]), ее читает ядро Xray (вход
 * tun с дескриптором карты) и отправляет через выбранный сервер; сокеты ядра выведены из туннеля (protect). Сервер —
 * только выбранный пользователем: другой сам не выбирается. Запускают окно (кнопка включения), уведомление
 * («Отключить») и система — «Постоянная VPN».
 */
@AndroidEntryPoint
class GatewayVpnService : VpnService() {
    @Inject lateinit var servers: ServerRepository

    @Inject lateinit var settings: SettingsRepository

    @Inject lateinit var engine: CoreEngine

    @Inject lateinit var monitor: TunnelMonitor

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private val lock = Mutex()
    private var work: Job? = null
    private var tun: ParcelFileDescriptor? = null
    private var coreRunning = false

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_DISCONNECT) {
            disconnect()
            return START_NOT_STICKY
        }

        // Окно, «Постоянная VPN» (действие android.net.VpnService) и перезапуск системой — подключение к выбранному.
        connect()
        return START_STICKY
    }

    override fun onRevoke() {
        // Разрешение отозвано: VPN другой программы занял место или пользователь отключил нас в настройках Android.
        disconnect()
    }

    override fun onDestroy() {
        work?.cancel()
        stopCore()
        if (monitor.status.value.state != TunnelState.Error) {
            monitor.set(TunnelStatus())
        }

        scope.cancel()
        super.onDestroy()
    }

    private fun connect() {
        foreground(notification(getString(R.string.status_connecting_title), null))
        work?.cancel()
        work = scope.launch {
            lock.withLock {
                stopCore()
                val error = start()
                if (error != null) {
                    stopCore()
                    monitor.set(TunnelStatus(state = TunnelState.Error, error = error))
                    ServiceCompat.stopForeground(this@GatewayVpnService, ServiceCompat.STOP_FOREGROUND_REMOVE)
                    stopSelf()
                }
            }
        }
    }

    /** Поднять туннель и ядро; удалось — null, иначе код ошибки. */
    private suspend fun start(): String? {
        val id = settings.selectedServerId.first() ?: return TunnelErrors.NO_SERVER
        monitor.set(TunnelStatus(state = TunnelState.Connecting, serverId = id))
        val server = servers.load(id) ?: return TunnelErrors.NO_SERVER
        val core = CoreSettings()
        val plan = TunPlan.of(core)

        // DNS сети — до туннеля: потом сеть по умолчанию — уже он сам.
        val dns = CoreDns.endpoint(networkDns())
        val builder = Builder()
            .setSession(getString(R.string.app_title))
            .setMtu(plan.mtu)
            .setConfigureIntent(openApp())
        plan.addresses.forEach { builder.addAddress(it.address, it.length) }
        plan.routes.forEach { builder.addRoute(it.address, it.length) }
        plan.dnsServers.forEach { builder.addDnsServer(it) }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            // Платная ли сеть — как у сети под туннелем, а не «всегда платная», как считает Android для VPN по умолчанию.
            builder.setMetered(false)
        }

        val descriptor = establish(builder) ?: return TunnelErrors.OTHER_VPN
        tun = descriptor

        val config = XrayConfig.build(server, core, descriptor.fd)
        val json = config.json ?: return TunnelErrors.UNSUPPORTED
        val failure = engine.start(json, { socket -> protect(socket) }, dns)
        if (failure != null) {
            // Текст ядра — в журнал Android через SecretMasker: ссылок, UUID и ключей в журнале нет.
            Log.w(TAG, "core: " + SecretMasker.mask(failure))
            return TunnelErrors.CORE_FAILED
        }

        coreRunning = true
        val alwaysOn = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) isAlwaysOn else null
        val lockdown = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) isLockdownEnabled else null
        monitor.set(
            TunnelStatus(
                state = TunnelState.Connected,
                serverId = id,
                since = SystemClock.elapsedRealtime(),
                alwaysOn = alwaysOn,
                lockdown = lockdown,
            ),
        )
        foreground(notification(getString(R.string.status_connected_title) + " · " + server.name, getString(R.string.status_connected_sub)))
        return null
    }

    /** Карта туннеля; null — Android не дал: нет разрешения (VPN другой программы занял место) или отказ системы. */
    private fun establish(builder: Builder): ParcelFileDescriptor? = try {
        builder.establish()
    } catch (e: IllegalStateException) {
        Log.w(TAG, "establish: " + SecretMasker.mask(e.message))
        null
    } catch (e: SecurityException) {
        Log.w(TAG, "establish: " + SecretMasker.mask(e.message))
        null
    }

    private fun disconnect() {
        work?.cancel()
        scope.launch {
            lock.withLock {
                monitor.update { it.copy(state = TunnelState.Stopping) }
                stopCore()
                monitor.set(TunnelStatus())
                ServiceCompat.stopForeground(this@GatewayVpnService, ServiceCompat.STOP_FOREGROUND_REMOVE)
                stopSelf()
            }
        }
    }

    /** Ядро — до карты: ядро читает свою копию дескриптора, а карту закрывает только приложение. */
    private fun stopCore() {
        if (coreRunning) {
            engine.stop()
            coreRunning = false
        }

        tun?.close()
        tun = null
    }

    private fun networkDns(): List<String> {
        val connectivity = getSystemService(ConnectivityManager::class.java) ?: return emptyList()
        val properties = connectivity.getLinkProperties(connectivity.activeNetwork) ?: return emptyList()
        return properties.dnsServers.mapNotNull { it.hostAddress }
    }

    private fun foreground(notification: Notification) {
        val type = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE else 0
        ServiceCompat.startForeground(this, NOTIFICATION_ID, notification, type)
    }

    private fun notification(title: String, text: String?): Notification =
        NotificationCompat.Builder(this, PrivateGatewayApp.CHANNEL_TUNNEL)
            .setSmallIcon(R.drawable.pg_notification)
            .setContentTitle(title)
            .setContentText(text)
            .setContentIntent(openApp())
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setCategory(NotificationCompat.CATEGORY_SERVICE)
            .setVisibility(NotificationCompat.VISIBILITY_PUBLIC)
            .addAction(0, getString(R.string.android_notify_disconnect), disconnectIntent(this))
            .build()

    private fun openApp(): PendingIntent = PendingIntent.getActivity(
        this,
        0,
        Intent(this, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP),
        PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
    )

    companion object {
        private const val TAG = "PrivateGateway"
        private const val NOTIFICATION_ID = 1
        const val ACTION_CONNECT = "com.privategateway.client.CONNECT"
        const val ACTION_DISCONNECT = "com.privategateway.client.DISCONNECT"

        /** Подключить: разрешение на VPN (VpnService.prepare) окно уже получило. */
        fun connect(context: Context) {
            ContextCompat.startForegroundService(context, Intent(context, GatewayVpnService::class.java).setAction(ACTION_CONNECT))
        }

        fun disconnect(context: Context) {
            context.startService(Intent(context, GatewayVpnService::class.java).setAction(ACTION_DISCONNECT))
        }

        private fun disconnectIntent(context: Context): PendingIntent = PendingIntent.getService(
            context,
            1,
            Intent(context, GatewayVpnService::class.java).setAction(ACTION_DISCONNECT),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
    }
}

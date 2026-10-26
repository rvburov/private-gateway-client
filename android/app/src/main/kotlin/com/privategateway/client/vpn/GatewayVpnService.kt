package com.privategateway.client.vpn

import android.Manifest
import android.app.Notification
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.content.pm.ServiceInfo
import android.net.ConnectivityManager
import android.net.LinkProperties
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import android.net.VpnService
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.os.ParcelFileDescriptor
import android.os.SystemClock
import android.util.Log
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.app.ServiceCompat
import androidx.core.content.ContextCompat
import com.privategateway.client.MainActivity
import com.privategateway.client.PrivateGatewayApp
import com.privategateway.client.R
import com.privategateway.client.core.servers.ServerFacts
import com.privategateway.client.core.settings.TunnelSettings
import com.privategateway.client.core.text.SecretMasker
import com.privategateway.client.core.vpn.CoreDnsFollower
import com.privategateway.client.core.vpn.TunPlan
import com.privategateway.client.core.xray.XrayConfig
import com.privategateway.client.data.ServerRepository
import com.privategateway.client.data.SettingsRepository
import com.privategateway.client.ui.Texts
import dagger.hilt.android.AndroidEntryPoint
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import javax.inject.Inject

/**
 * Туннель защиты: системный VPN Android. Весь трафик телефона идет в карту TUN ([TunPlan]), ее читает ядро Xray (вход
 * tun с дескриптором карты) и отправляет через выбранный сервер; сокеты ядра выведены из туннеля (protect). Сервер —
 * только выбранный пользователем: другой сам не выбирается. Запускают окно (кнопка включения), уведомление
 * («Отключить») и система — «Постоянная VPN». Поле [tun], ядро и слежение за сетью меняются только под [lock].
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
    private var dns: CoreDnsFollower? = null
    private var underlying: ConnectivityManager.NetworkCallback? = null

    /** DNS сети под туннелем — последнее, что сообщил Android; ядру его передает [followNetwork] под замком. */
    @Volatile
    private var networkDns: List<String>? = null

    /** Номер последнего запуска: остановка по старому не гасит службу, если после него пришел новый запуск. */
    @Volatile
    private var lastStart = 0

    /** Служба уничтожена: подключение, которое шло в это время, снимает за собой туннель и ядро само. */
    @Volatile
    private var destroyed = false

    /** Настройки туннеля, с которыми он поднят; null — туннеля нет. */
    private val applied = MutableStateFlow<TunnelSettings?>(null)

    override fun onCreate() {
        super.onCreate()
        // Настройки сменились под защитой — переподключение без окна утечки (как ProtectionSync десктопа): после тишины,
        // чтобы несколько нажатий подряд дали одно переподключение; то, что ядра не меняет, переподключения не требует.
        scope.launch {
            settings.tunnel.combine(applied) { wanted, running -> wanted to running }.collectLatest { (wanted, running) ->
                if (running != null && running.core() != wanted.core()) {
                    delay(SETTINGS_QUIET_MS)
                    connect(lastStart)
                }
            }
        }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        lastStart = startId
        if (intent?.action == ACTION_DISCONNECT) {
            disconnect(startId)
            return START_NOT_STICKY
        }

        // Окно, «Постоянная VPN» (действие android.net.VpnService) и перезапуск системой — подключение к выбранному.
        connect(startId)
        return START_STICKY
    }

    override fun onRevoke() {
        // Разрешение отозвано: VPN другой программы занял место или пользователь отключил нас в настройках Android.
        disconnect(lastStart)
    }

    override fun onDestroy() {
        destroyed = true
        work?.cancel()
        // establish() и запуск ядра не прерываются: если подключение держит замок, туннель и ядро снимет оно само.
        if (lock.tryLock()) {
            try {
                stopCore()
            } finally {
                lock.unlock()
            }
        }

        if (monitor.status.value.state != TunnelState.Error) {
            monitor.set(TunnelStatus())
        }

        scope.cancel()
        super.onDestroy()
    }

    private fun connect(startId: Int) {
        foreground(notification(getString(R.string.status_connecting_title), null))
        work?.cancel()
        work = scope.launch {
            lock.withLock {
                val error = try {
                    start()
                } catch (e: CancellationException) {
                    throw e
                } catch (e: Exception) {
                    // Сбой Android или ядра — не авария всего приложения: ошибка с кодом, текст — в журнал через SecretMasker.
                    Log.w(TAG, "connect: " + SecretMasker.mask(e.toString()))
                    TunnelErrors.CORE_FAILED
                }

                if (destroyed) {
                    stopCore()
                    monitor.set(if (error != null) TunnelStatus(state = TunnelState.Error, error = error) else TunnelStatus())
                } else if (error != null) {
                    stopCore()
                    monitor.set(TunnelStatus(state = TunnelState.Error, error = error))
                    ServiceCompat.stopForeground(this@GatewayVpnService, ServiceCompat.STOP_FOREGROUND_REMOVE)
                    // Запуск «Постоянной VPN» не виден в окне: без уведомления пользователь думал бы, что защищен.
                    notifyFailure(error)
                    stopSelfResult(startId)
                }
            }
        }
    }

    /** Поднять туннель и ядро; удалось — null, иначе код ошибки. */
    private suspend fun start(): String? {
        val id = settings.selectedServerId.first() ?: return TunnelErrors.NO_SERVER
        monitor.set(TunnelStatus(state = TunnelState.Connecting, serverId = id))
        val server = servers.load(id) ?: return TunnelErrors.NO_SERVER
        // Название для уведомления — свое, если есть. Читается до карты и ядра: после них точек ожидания нет — отмена не
        // оставит туннель без хозяина.
        val name = servers.servers.first().firstOrNull { it.id == id }?.name ?: server.name
        val tunnel = settings.tunnel.first()
        val core = tunnel.core()
        val plan = TunPlan.of(core)

        // DNS сети — до туннеля: потом сеть по умолчанию — уже он сам. Дальше DNS ведет слежение за сетью.
        val follower = CoreDnsFollower(activeNetworkDns())
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

        // Переподключение без окна утечки: новая карта поднимается, пока прежняя еще держит трафик (Android меняет одну на
        // другую сам), и только потом останавливается прежнее ядро. Пока новое не запущено, пакеты ждут в карте, а не
        // уходят мимо туннеля.
        val descriptor = establish(builder) ?: return TunnelErrors.OTHER_VPN
        stopCore()
        tun = descriptor

        val config = XrayConfig.build(server, core, descriptor.fd)
        val json = config.json ?: return TunnelErrors.UNSUPPORTED
        // Ядро считается запущенным и при отказе: остановка снимет то, что успело запуститься (DNS, контроллер сокетов).
        coreRunning = true
        val failure = engine.start(json, { socket -> protect(socket) }, follower.endpoint)
        if (failure != null) {
            // Текст ядра — в журнал Android через SecretMasker: ссылок, UUID и ключей в журнале нет.
            Log.w(TAG, "core: " + SecretMasker.mask(failure))
            return TunnelErrors.CORE_FAILED
        }

        dns = follower
        applied.value = tunnel
        watchNetwork()
        if (destroyed) {
            return null
        }

        val alwaysOn = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) isAlwaysOn else null
        val lockdown = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) isLockdownEnabled else null
        val since = SystemClock.elapsedRealtime()
        monitor.set(
            TunnelStatus(
                state = TunnelState.Connected,
                serverId = id,
                since = since,
                alwaysOn = alwaysOn,
                lockdown = lockdown,
            ),
        )
        // Доска 26: «IP скрыт · сервер», шифруется ли трафик, «в сети» с таймером, «Отключить» и «Сменить сервер».
        val encrypted = ServerFacts.isEncrypted(server.protocol, server.security.kind)
        foreground(
            notification(
                getString(R.string.status_connected_title) + " · " + name,
                getString(if (encrypted) R.string.status_connected_sub else R.string.status_connected_subPlain),
                onlineSince = System.currentTimeMillis() - (SystemClock.elapsedRealtime() - since),
            ),
        )
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

    private fun disconnect(startId: Int) {
        work?.cancel()
        scope.launch {
            lock.withLock {
                monitor.update { it.copy(state = TunnelState.Stopping) }
                stopCore()
                monitor.set(TunnelStatus())
                ServiceCompat.stopForeground(this@GatewayVpnService, ServiceCompat.STOP_FOREGROUND_REMOVE)
                stopSelfResult(startId)
            }
        }
    }

    /** Ядро — до карты: ядро читает свою копию дескриптора, а карту закрывает только приложение. */
    private fun stopCore() {
        underlying?.let { callback -> runCatching { getSystemService(ConnectivityManager::class.java)?.unregisterNetworkCallback(callback) } }
        underlying = null
        applied.value = null
        dns = null
        networkDns = null
        if (coreRunning) {
            engine.stop()
            coreRunning = false
        }

        tun?.close()
        tun = null
    }

    private fun activeNetworkDns(): List<String> {
        val connectivity = getSystemService(ConnectivityManager::class.java) ?: return emptyList()
        val properties = connectivity.getLinkProperties(connectivity.activeNetwork) ?: return emptyList()
        return properties.dnsServers.mapNotNull { it.hostAddress }
    }

    /**
     * Следить за сетью под туннелем: лучшая сеть с интернетом, кроме VPN, — та, по которой уходит трафик ядра. Сменилась
     * (Wi-Fi ↔ мобильная) или сменила DNS — ядру DNS новой сети. Сеть под туннелем Android выбирает сам (сеть по умолчанию).
     */
    private fun watchNetwork() {
        val connectivity = getSystemService(ConnectivityManager::class.java) ?: return
        val request = NetworkRequest.Builder()
            .addCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
            .addCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN)
            .build()
        val callback = object : ConnectivityManager.NetworkCallback() {
            // С Android 8 за onAvailable всегда идет onLinkPropertiesChanged: новая сеть приходит сюда же.
            override fun onLinkPropertiesChanged(network: Network, linkProperties: LinkProperties) {
                networkDns = linkProperties.dnsServers.mapNotNull { it.hostAddress }
                scope.launch { lock.withLock { followNetwork() } }
            }
        }

        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
                connectivity.registerBestMatchingNetworkCallback(request, callback, Handler(Looper.getMainLooper()))
            } else {
                // До Android 12 лучшую подходящую сеть дает запрос сети: ее же Android делает сетью по умолчанию.
                connectivity.requestNetwork(request, callback)
            }

            underlying = callback
        } catch (e: RuntimeException) {
            Log.w(TAG, "network callback: " + SecretMasker.mask(e.message))
        }
    }

    /** Под замком: DNS последней сети — ядру, если он изменился. */
    private fun followNetwork() {
        val follower = dns ?: return
        val endpoint = networkDns?.let { follower.network(it) } ?: return
        engine.changeDns(endpoint)?.let { Log.w(TAG, "core dns: " + SecretMasker.mask(it)) }
    }

    private fun foreground(notification: Notification) {
        val type = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE else 0
        ServiceCompat.startForeground(this, NOTIFICATION_ID, notification, type)
    }

    /** Постоянное уведомление туннеля; [onlineSince] — когда защита включилась (время эпохи): «в сети» с таймером. */
    private fun notification(title: String, text: String?, onlineSince: Long? = null): Notification {
        val builder = NotificationCompat.Builder(this, PrivateGatewayApp.CHANNEL_TUNNEL)
            .setSmallIcon(R.drawable.pg_notification)
            .setContentTitle(title)
            .setContentText(text)
            .setContentIntent(openApp())
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setCategory(NotificationCompat.CATEGORY_SERVICE)
            .setVisibility(NotificationCompat.VISIBILITY_PUBLIC)
            .addAction(0, getString(R.string.android_notify_disconnect), disconnectIntent(this))
        if (onlineSince != null) {
            builder.setSubText(getString(R.string.status_online))
                .setWhen(onlineSince)
                .setShowWhen(true)
                .setUsesChronometer(true)
                .addAction(0, getString(R.string.notify_action_changeServer), openApp(MainActivity.TAB_SERVERS))
        }

        return builder.build()
    }

    /** Защита не включилась: уведомление с причиной; при «Блокировать подключения без VPN» интернета нет вовсе. */
    private fun notifyFailure(error: String) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) {
            return
        }

        val blocked = Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q && isLockdownEnabled
        // Причина и код, как у десктопа: «Ядро не запустилось · PGC-SVC-002. Трафик идет напрямую.»
        val reason = getString(Texts.error(error)) + " · " + error
        val text = getString(if (blocked) R.string.notify_failed_blocked else R.string.notify_failed_direct, "", "", reason)
        val notification = NotificationCompat.Builder(this, PrivateGatewayApp.CHANNEL_ALERTS)
            .setSmallIcon(R.drawable.pg_notification)
            .setContentTitle(getString(R.string.notify_failed_title))
            .setContentText(text)
            .setStyle(NotificationCompat.BigTextStyle().bigText(text))
            .setContentIntent(openApp())
            .setAutoCancel(true)
            .setCategory(NotificationCompat.CATEGORY_ERROR)
            .setVisibility(NotificationCompat.VISIBILITY_PUBLIC)
            .build()
        NotificationManagerCompat.from(this).notify(FAILURE_NOTIFICATION_ID, notification)
    }

    /** Открыть окно; [tab] — сразу на разделе (MainActivity.TAB_*). У каждого раздела свой PendingIntent. */
    private fun openApp(tab: String? = null): PendingIntent = PendingIntent.getActivity(
        this,
        if (tab == null) 0 else 1,
        Intent(this, MainActivity::class.java)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP)
            .apply { tab?.let { putExtra(MainActivity.EXTRA_TAB, it) } },
        PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
    )

    companion object {
        private const val TAG = "PrivateGateway"
        private const val NOTIFICATION_ID = 1
        private const val FAILURE_NOTIFICATION_ID = 2
        private const val SETTINGS_QUIET_MS = 700L
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

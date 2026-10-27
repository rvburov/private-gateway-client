package com.privategateway.client.vpn

import android.app.Notification
import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
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
import android.os.PowerManager
import android.os.SystemClock
import android.util.Log
import androidx.core.app.NotificationCompat
import androidx.core.app.ServiceCompat
import androidx.core.content.ContextCompat
import com.privategateway.client.MainActivity
import com.privategateway.client.PrivateGatewayApp
import com.privategateway.client.R
import com.privategateway.client.core.servers.ServerFacts
import com.privategateway.client.core.settings.TunnelSettings
import com.privategateway.client.core.text.SecretMasker
import com.privategateway.client.core.vpn.CoreDnsFollower
import com.privategateway.client.core.vpn.GuardEvents
import com.privategateway.client.core.vpn.SystemVpn
import com.privategateway.client.core.vpn.TunPlan
import com.privategateway.client.core.vpn.TunnelErrors
import com.privategateway.client.core.vpn.TunnelGuard
import com.privategateway.client.core.vpn.TunnelProbe
import com.privategateway.client.core.vpn.TunnelState
import com.privategateway.client.core.vpn.TunnelStatus
import com.privategateway.client.core.xray.XrayConfig
import com.privategateway.client.data.ServerRepository
import com.privategateway.client.data.SettingsRepository
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
import kotlinx.coroutines.withContext
import javax.inject.Inject

/**
 * Туннель защиты: системный VPN Android. Весь трафик телефона идет в карту TUN ([TunPlan]), ее читает ядро Xray (вход
 * tun с дескриптором карты) и отправляет через выбранный сервер; сокеты ядра выведены из туннеля (protect). Сервер —
 * только выбранный пользователем: другой сам не выбирается. Запускают окно (кнопка включения), уведомление
 * («Отключить») и система — «Постоянная VPN». Поле [tun], ядро и слежение за сетью меняются только под [lock]. Пока
 * защита включена, [TunnelGuard] пробует связь через туннель и переподключает к тому же серверу; карта туннеля все это
 * время держит трафик.
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

    /** Поднятый туннель: сервер, его название для уведомления, шифруется ли трафик. */
    private class Up(val id: String, val name: String, val encrypted: Boolean)

    @Volatile
    private var up: Up? = null

    /** Наблюдение за связью и переподключение, пока защита включена. */
    private var guard: Job? = null

    @Volatile
    private var guardian: TunnelGuard? = null

    /**
     * Сеть под туннелем, о которой Android сообщил последней. Не сбрасывается с ядром: подписка на сеть ставится заново на
     * каждую попытку, и Android сразу присылает текущую сеть — это не смена сети и не повод для попытки без паузы.
     */
    @Volatile
    private var lastNetwork: Network? = null

    /** Телефон вышел из глубокого сна (Doze) — проба связи сразу, а не через минуту. */
    private val wake = object : BroadcastReceiver() {
        override fun onReceive(context: Context, intent: Intent) {
            if (getSystemService(PowerManager::class.java)?.isDeviceIdleMode == false) {
                guardian?.nudge()
            }
        }
    }

    override fun onCreate() {
        super.onCreate()
        ContextCompat.registerReceiver(this, wake, IntentFilter(PowerManager.ACTION_DEVICE_IDLE_MODE_CHANGED), ContextCompat.RECEIVER_NOT_EXPORTED)
        // Настройки сменились под защитой — переподключение без окна утечки (как ProtectionSync десктопа): после тишины,
        // чтобы несколько нажатий подряд дали одно переподключение; то, что ядра не меняет, переподключения не требует.
        scope.launch {
            settings.tunnel.combine(applied) { wanted, running -> wanted to running }.collectLatest { (wanted, running) ->
                if (running != null && running.needsReconnect(wanted)) {
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
        stopGuard()
        unregisterReceiver(wake)
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
        stopGuard()
        work = scope.launch {
            val connected = lock.withLock {
                val error = attempt(0)
                if (destroyed) {
                    stopCore()
                    monitor.set(if (error != null) TunnelStatus(state = TunnelState.Error, error = error) else TunnelStatus())
                    false
                } else if (error != null) {
                    fail(error, startId)
                    false
                } else {
                    showProtected()
                    true
                }
            }

            if (connected) {
                startGuard()
            }
        }
    }

    /** Подключение (0) или попытка переподключения [number] под замком: удалось — null, иначе код ошибки. */
    private suspend fun attempt(number: Int): String? = try {
        start(number)
    } catch (e: CancellationException) {
        throw e
    } catch (e: Exception) {
        // Сбой Android или ядра — не авария всего приложения: ошибка с кодом, текст — в журнал через SecretMasker.
        Log.w(TAG, "connect: " + SecretMasker.mask(e.toString()))
        TunnelErrors.CORE_FAILED
    }

    /** Под замком: защита не включилась или переподключение сдалось — «Ошибка» с кодом, ядра и карты нет. */
    private fun fail(error: String, startId: Int) {
        stopCore()
        up = null
        monitor.set(TunnelStatus(state = TunnelState.Error, error = error))
        // Уведомление со сбоем дает ProtectionNotifier по смене состояния: запуск «Постоянной VPN» не виден в окне.
        ServiceCompat.stopForeground(this, ServiceCompat.STOP_FOREGROUND_REMOVE)
        stopSelfResult(startId)
    }

    /** «Защищено» (доска 26): «IP скрыт · сервер», шифруется ли трафик, «в сети» с таймером, «Отключить» и «Сменить сервер». */
    private fun showProtected() {
        val current = up ?: return
        val alwaysOn = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) isAlwaysOn else null
        val lockdown = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) isLockdownEnabled else null
        val since = SystemClock.elapsedRealtime()
        monitor.set(TunnelStatus(state = TunnelState.Connected, serverId = current.id, since = since, alwaysOn = alwaysOn, lockdown = lockdown))
        // Без туннеля Android этих флагов не сообщает — окно покажет их «по последнему подключению» (решение 445).
        scope.launch { settings.setSystemVpn(SystemVpn(alwaysOn, lockdown)) }
        foreground(
            notification(
                getString(R.string.status_connected_title) + " · " + current.name,
                getString(if (current.encrypted) R.string.status_connected_sub else R.string.status_connected_subPlain),
                onlineSince = System.currentTimeMillis() - (SystemClock.elapsedRealtime() - since),
            ),
        )
    }

    /**
     * Наблюдение за связью (TunnelGuard): проба через туннель, переподключение к тому же серверу с паузами по политике
     * «Переподключение», при отказе — «Ошибка» с кодом. Сеть сменилась ([watchNetwork]) или телефон проснулся — сразу.
     */
    private fun startGuard() {
        val guardian = TunnelGuard(
            clock = SystemClock::elapsedRealtime,
            policy = { settings.reconnect.first() },
            probe = { withContext(Dispatchers.IO) { TunnelProbe.answers() } },
            restart = { number -> lock.withLock { attempt(number) } },
            events = object : GuardEvents {
                override suspend fun reconnecting(attempt: Int) {
                    monitor.update { it.copy(state = TunnelState.Reconnecting, since = null, attempt = attempt) }
                    foreground(notification(getString(R.string.status_reconnecting_title), up?.name))
                }

                override suspend fun protectedAgain() {
                    lock.withLock { showProtected() }
                }

                override suspend fun gaveUp(error: String) {
                    Log.w(TAG, "reconnect: $error")
                    lock.withLock { fail(error, lastStart) }
                }
            },
        )
        this.guardian = guardian
        guard = scope.launch { guardian.run() }
    }

    private fun stopGuard() {
        guard?.cancel()
        guard = null
        guardian = null
    }

    /** Поднять туннель и ядро; [attempt] — номер попытки переподключения (0 — подключение). Удалось — null, иначе код ошибки. */
    private suspend fun start(attempt: Int): String? {
        val id = settings.selectedServerId.first() ?: return TunnelErrors.NO_SERVER
        if (attempt == 0) {
            monitor.set(TunnelStatus(state = TunnelState.Connecting, serverId = id))
        }

        val server = servers.load(id) ?: return TunnelErrors.NO_SERVER
        // Название для уведомления — свое, если есть. Читается до карты и ядра: после них точек ожидания нет — отмена не
        // оставит туннель без хозяина.
        val name = servers.servers.first().firstOrNull { it.id == id }?.displayName ?: server.name
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
        up = Up(id, name, ServerFacts.isEncrypted(server.protocol, server.security.kind))
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
        stopGuard()
        scope.launch {
            lock.withLock {
                monitor.update { it.copy(state = TunnelState.Stopping) }
                stopCore()
                up = null
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
                // Сеть сменилась (Wi-Fi ↔ мобильная) — проба связи сразу; во время переподключения — попытка сразу.
                val previous = lastNetwork
                lastNetwork = network
                if (previous != null && previous != network) {
                    guardian?.nudge()
                }
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

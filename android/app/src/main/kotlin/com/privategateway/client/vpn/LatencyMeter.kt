package com.privategateway.client.vpn

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.util.Log
import java.io.FileDescriptor
import com.privategateway.client.core.text.SecretMasker
import com.privategateway.client.core.servers.TransportParameterRules
import com.privategateway.client.core.vpn.CoreDns
import com.privategateway.client.core.vpn.TunnelState
import com.privategateway.client.core.vpn.TunnelStatus
import com.privategateway.client.core.xray.PingBatch
import com.privategateway.client.core.xray.PingTarget
import com.privategateway.client.core.xray.XrayResponse
import com.privategateway.client.data.ServerRepository
import com.privategateway.client.data.SettingsRepository
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.withContext
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlin.coroutines.resume
import javax.inject.Inject
import javax.inject.Singleton

/** DNS текущей сети телефона — для адресов серверов при замере (без туннеля); в тестах подделка. */
fun interface NetworkDns {
    fun current(): List<String>

    /** Идентичность физической сети, чтобы результат Wi-Fi не попал в список после перехода на мобильную сеть. */
    fun identity(): String = current().joinToString(",")
}

@Singleton
class SystemNetworkDns @Inject constructor(@param:ApplicationContext private val context: Context) : NetworkDns {
    override fun current(): List<String> {
        val connectivity = context.getSystemService(ConnectivityManager::class.java) ?: return emptyList()
        val properties = connectivity.getLinkProperties(underlying(connectivity)) ?: return emptyList()
        return properties.dnsServers.mapNotNull { it.hostAddress }
    }

    override fun identity(): String {
        val connectivity = context.getSystemService(ConnectivityManager::class.java) ?: return "none"
        val network = underlying(connectivity) ?: return "none"
        val properties = connectivity.getLinkProperties(network)
        return "$network:${properties?.interfaceName}:${properties?.dnsServers}:${properties?.linkAddresses}"
    }

    /** После protect сокет привязывается к той же физической сети, чей DNS и идентичность указаны в замере. */
    fun bind(socket: FileDescriptor): Boolean = runCatching {
        val connectivity = context.getSystemService(ConnectivityManager::class.java) ?: return false
        val network = underlying(connectivity) ?: return false
        network.bindSocket(socket)
        true
    }.getOrDefault(false)

    /** DNS туннеля нельзя отправлять в защищенном от туннеля сокете: выбирается именно физическая сеть. */
    @Suppress("DEPRECATION")
    private fun underlying(connectivity: ConnectivityManager): Network? {
        val active = connectivity.activeNetwork
        if (active != null && connectivity.getNetworkCapabilities(active)?.hasTransport(NetworkCapabilities.TRANSPORT_VPN) == false) return active
        return connectivity.allNetworks.firstOrNull {
            val capabilities = connectivity.getNetworkCapabilities(it)
            capabilities?.hasTransport(NetworkCapabilities.TRANSPORT_VPN) == false && capabilities.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
        }
    }
}

/**
 * Итог замера: все порции замерены; подключение меняется; замер уже идет; Android закрыл
 * сеть приложению без туннеля («Блокировать подключения без VPN») — серверы не мерятся, их прежние задержки остаются.
 */
enum class MeasureResult {
    Done,
    Busy,
    Running,
    Blocked,
}

data class LatencyProgress(val done: Int = 0, val total: Int = 0)

/**
 * Задержка серверов через настоящее ядро (решение 424): libXray `pingBatch` по 5 серверов, тот же выход, что у туннеля,
 * с текущими настройками. Ядро замера живет в отдельном процессе: DNS и сокеты действующего туннеля не меняются. Перед каждой
 * порцией состояние проверяется; порция, во время которой изменилось подключение, отбрасывается. Итоги пишутся порцией —
 * список обновляется по мере замера. Только по просьбе пользователя, как у десктопа.
 */
@Singleton
class LatencyMeter @Inject constructor(
    private val engine: LatencyWorker,
    private val servers: ServerRepository,
    private val settings: SettingsRepository,
    private val monitor: TunnelMonitor,
    private val network: NetworkDns,
) {
    private val running = Mutex()
    private val state = MutableStateFlow(false)
    private val workers = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val completed = MutableStateFlow(LatencyProgress())
    val progress = completed.asStateFlow()

    init {
        workers.launch {
            monitor.status.map { connectionIdentity(it) }.distinctUntilChanged().collect {
                servers.markLatencyStale(connectionId = it)
            }
        }
    }

    /** Сообщение Android о смене сети: без фонового опроса и без запуска новых сетевых проб. */
    fun networkChanged() = servers.markLatencyStale(networkId = network.identity())

    /** Идет ли замер — для кнопки «Обновить». */
    val measuring: StateFlow<Boolean> = state.asStateFlow()

    suspend fun measure(): MeasureResult {
        val session = monitor.status.value
        val networkId = network.identity()
        if (!stable(session)) {
            return MeasureResult.Busy
        }

        if (monitor.blocked.value && session.state != TunnelState.Connected) {
            return MeasureResult.Blocked
        }

        if (!running.tryLock()) {
            return MeasureResult.Running
        }

        state.value = true
        try {
            val targets = servers.servers.first().mapNotNull { item -> servers.load(item.id)?.let { PingTarget(item.id, it) } }
            val sensitive = targets.flatMap { TransportParameterRules.sensitiveExtraValues(it.server) }
            val definitions = targets.associate { it.id to it.server }
            val tunnel = settings.tunnel.first()
            val plan = PingBatch.plan(targets, tunnel.core(), timeoutSeconds = (tunnel.connection.probeTimeoutMs / 1000).toInt().coerceIn(5, 30))
            completed.value = LatencyProgress(plan.unsupported.size, targets.size)
            servers.setLatency(plan.unsupported, networkId, connectionIdentity(session), definitions)
            val dns = CoreDns.endpoint(network.current())
            for (chunk in plan.chunks) {
                if (!sameSession(session, networkId)) {
                    return MeasureResult.Busy
                }

                val response = request(chunk.request, dns)
                // Выбор сервера или сеть сменились: результат относится к старому пути и не должен затирать новый.
                if (!sameSession(session, networkId)) {
                    return MeasureResult.Busy
                }

                if (monitor.blocked.value && session.state != TunnelState.Connected) {
                    return MeasureResult.Blocked
                }

                val failure = XrayResponse.parse(response)
                if (!failure.success) {
                    Log.w(TAG, "pingBatch: " + SecretMasker.mask(failure.error ?: "нет ответа", sensitive))
                }

                chunk.errors(response).forEach { (id, error) -> Log.i(TAG, "pingBatch $id: " + SecretMasker.mask(error, sensitive)) }

                servers.setLatency(chunk.results(response), networkId, connectionIdentity(session), definitions)
                completed.value = completed.value.copy(done = completed.value.done + chunk.ids.size)
            }

            return MeasureResult.Done
        } finally {
            state.value = false
            running.unlock()
        }
    }

    private fun stable(status: TunnelStatus) = !status.offline && status.state in listOf(TunnelState.Off, TunnelState.Error, TunnelState.Connected)

    private suspend fun request(json: String, dns: String): String? = suspendCancellableCoroutine { continuation ->
        val job = workers.launch {
            val result = try {
                engine.ping(json, dns)
            } catch (cancelled: CancellationException) {
                continuation.cancel(cancelled)
                return@launch
            } catch (_: Exception) {
                null
            }
            if (continuation.isActive) continuation.resume(result)
        }
        continuation.invokeOnCancellation {
            engine.cancel()
            job.cancel()
        }
    }

    private fun sameSession(before: TunnelStatus, networkId: String): Boolean {
        val now = monitor.status.value
        return stable(now) && now.state == before.state && now.serverId == before.serverId && now.since == before.since && network.identity() == networkId
    }

    private companion object {
        const val TAG = "PrivateGateway"

        fun connectionIdentity(status: TunnelStatus): String = listOf(status.state, status.serverId, status.since, status.offline, status.localOnly).joinToString(":")
    }
}

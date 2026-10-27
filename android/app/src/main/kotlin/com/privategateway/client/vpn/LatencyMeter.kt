package com.privategateway.client.vpn

import android.content.Context
import android.net.ConnectivityManager
import android.util.Log
import com.privategateway.client.core.text.SecretMasker
import com.privategateway.client.core.vpn.CoreDns
import com.privategateway.client.core.vpn.TunnelState
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
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.withContext
import javax.inject.Inject
import javax.inject.Singleton

/** DNS текущей сети телефона — для адресов серверов при замере (без туннеля); в тестах подделка. */
fun interface NetworkDns {
    fun current(): List<String>
}

@Singleton
class SystemNetworkDns @Inject constructor(@param:ApplicationContext private val context: Context) : NetworkDns {
    override fun current(): List<String> {
        val connectivity = context.getSystemService(ConnectivityManager::class.java) ?: return emptyList()
        val properties = connectivity.getLinkProperties(connectivity.activeNetwork) ?: return emptyList()
        return properties.dnsServers.mapNotNull { it.hostAddress }
    }
}

/** Итог замера: все порции замерены; защита включена (или включилась во время замера); замер уже идет. */
enum class MeasureResult {
    Done,
    Busy,
    Running,
}

/**
 * Задержка серверов через настоящее ядро (решение 424): libXray `pingBatch` по 5 серверов, тот же выход, что у туннеля,
 * с текущими настройками. Только без защиты — libXray не меряет рядом с работающим ядром (решение 430). Перед каждой
 * порцией состояние проверяется; порция, во время которой началось подключение, отбрасывается. Итоги пишутся порцией —
 * список обновляется по мере замера. Только по просьбе пользователя, как у десктопа.
 */
@Singleton
class LatencyMeter @Inject constructor(
    private val engine: CoreEngine,
    private val servers: ServerRepository,
    private val settings: SettingsRepository,
    private val monitor: TunnelMonitor,
    private val network: NetworkDns,
) {
    private val running = Mutex()
    private val state = MutableStateFlow(false)

    /** Идет ли замер — для кнопки «Обновить». */
    val measuring: StateFlow<Boolean> = state.asStateFlow()

    suspend fun measure(): MeasureResult {
        if (!off()) {
            return MeasureResult.Busy
        }

        if (!running.tryLock()) {
            return MeasureResult.Running
        }

        state.value = true
        try {
            val targets = servers.servers.first().mapNotNull { item -> servers.load(item.id)?.let { PingTarget(item.id, it) } }
            val plan = PingBatch.plan(targets, settings.tunnel.first().core())
            servers.setLatency(plan.unsupported)
            val dns = CoreDns.endpoint(network.current())
            for (chunk in plan.chunks) {
                if (!off()) {
                    return MeasureResult.Busy
                }

                val response = withContext(Dispatchers.IO) { engine.ping(chunk.request, dns) }
                // Подключение началось во время порции: libXray мерил рядом с ядром туннеля — итогам не верить.
                if (!off()) {
                    return MeasureResult.Busy
                }

                val failure = XrayResponse.parse(response)
                if (!failure.success) {
                    Log.w(TAG, "pingBatch: " + SecretMasker.mask(failure.error ?: "нет ответа"))
                }

                chunk.errors(response).forEach { (id, error) -> Log.i(TAG, "pingBatch $id: " + SecretMasker.mask(error)) }

                servers.setLatency(chunk.results(response))
            }

            return MeasureResult.Done
        } finally {
            state.value = false
            running.unlock()
        }
    }

    private fun off() = monitor.status.value.state == TunnelState.Off

    private companion object {
        const val TAG = "PrivateGateway"
    }
}

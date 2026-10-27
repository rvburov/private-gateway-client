package com.privategateway.client.inspect

import androidx.annotation.StringRes
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.privategateway.client.R
import com.privategateway.client.core.inspect.AddressFailure
import com.privategateway.client.core.inspect.AddressInfo
import com.privategateway.client.core.inspect.PhoneNetwork
import com.privategateway.client.core.inspect.PhoneNetworks
import com.privategateway.client.core.inspect.ProbeKind
import com.privategateway.client.core.inspect.ProbeResult
import com.privategateway.client.core.inspect.ProbeRunner
import com.privategateway.client.core.inspect.ProbeTarget
import com.privategateway.client.core.inspect.PublicAddress
import com.privategateway.client.core.vpn.TunnelState
import com.privategateway.client.vpn.TunnelMonitor
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.ByteArrayOutputStream
import java.io.IOException
import java.net.HttpURLConnection
import java.net.InetAddress
import java.net.URI
import javax.inject.Inject

/** Запрос GET из процесса приложения: тело ответа 2xx или null; в тестах — подделка. */
fun interface WebFetch {
    suspend fun get(url: String): String?
}

/**
 * Запрос из процесса приложения — под защитой он идет через туннель (приложение из своего VPN не исключено), без нее —
 * напрямую, как у любой программы телефона. Без перенаправлений и кеша, 10 с на соединение и ответ, не больше 64 КБ.
 */
class DeviceWebFetch @Inject constructor() : WebFetch {
    override suspend fun get(url: String): String? = withContext(Dispatchers.IO) {
        try {
            val connection = URI(url).toURL().openConnection() as HttpURLConnection
            try {
                connection.connectTimeout = TIMEOUT_MS
                connection.readTimeout = TIMEOUT_MS
                connection.instanceFollowRedirects = false
                connection.useCaches = false
                if (connection.responseCode !in 200..299) {
                    null
                } else {
                    connection.inputStream.use { input ->
                        val body = ByteArrayOutputStream()
                        val buffer = ByteArray(8192)
                        while (body.size() < LIMIT) {
                            val read = input.read(buffer, 0, minOf(buffer.size, LIMIT - body.size()))
                            if (read < 0) {
                                break
                            }

                            body.write(buffer, 0, read)
                        }

                        body.toString(Charsets.UTF_8.name())
                    }
                }
            } finally {
                connection.disconnect()
            }
        } catch (_: IOException) {
            null
        }
    }

    private companion object {
        const val TIMEOUT_MS = 10_000
        const val LIMIT = 64 * 1024
    }
}

/** Внешний IP: идет ли замер, адрес, шел ли запрос через сервер, не удался ли; провайдер — по второй кнопке. */
data class PublicIpUi(
    val measuring: Boolean = false,
    val address: String? = null,
    val viaServer: Boolean = false,
    val failed: Boolean = false,
    val describing: Boolean = false,
    val info: AddressInfo? = null,
)

/** Замер: вид, узел и порт как ввел пользователь, идет ли, итог, ошибка ввода, шел ли через туннель. */
data class ProbeUi(
    val kind: ProbeKind = ProbeKind.Dns,
    val target: String = "",
    val port: String = "443",
    val running: Boolean = false,
    val result: ProbeResult? = null,
    @param:StringRes val error: Int? = null,
    val viaTunnel: Boolean = false,
)

data class InspectorUi(
    val networks: List<PhoneNetwork> = emptyList(),
    val publicIp: PublicIpUi = PublicIpUi(),
    val probe: ProbeUi = ProbeUi(),
)

/**
 * Инспектор сети Android (решение 448): сети телефона как их видит Android, внешний IP и его провайдер (RIPEstat — по
 * кнопке, как у десктопа), замеры DNS, TCP и TLS из процесса приложения. Ничего не пишет в журнал: адреса и узлы — на
 * экране и только.
 */
@HiltViewModel
class InspectorViewModel @Inject constructor(
    private val networks: PhoneNetworkSource,
    private val web: WebFetch,
    private val runner: ProbeRunner,
    private val monitor: TunnelMonitor,
) : ViewModel() {
    private val publicIp = MutableStateFlow(PublicIpUi())
    private val probe = MutableStateFlow(ProbeUi())

    val ui: StateFlow<InspectorUi> = combine(networks.networks, publicIp, probe) { list, ip, measure ->
        InspectorUi(PhoneNetworks.order(list), ip, measure)
    }.stateIn(viewModelScope, SharingStarted.Eagerly, InspectorUi())

    init {
        networks.start()
    }

    /** Внешний IP — тот, что видят сайты: под защитой — адрес выхода сервера, без нее — провайдера. */
    fun measureIp() {
        if (publicIp.value.measuring) {
            return
        }

        val viaServer = protected()
        publicIp.value = PublicIpUi(measuring = true)
        viewModelScope.launch {
            val address = PublicAddress.traceIp(web.get(PublicAddress.TRACE))
            publicIp.value = PublicIpUi(address = address, viaServer = viaServer, failed = address == null)
        }
    }

    /** Кто провайдер у измеренного адреса: частный — ответ сразу, без запроса; иначе два запроса к RIPEstat. */
    fun describe() {
        val current = publicIp.value
        val address = current.address ?: return
        if (current.describing) {
            return
        }

        publicIp.value = current.copy(describing = true, info = null)
        viewModelScope.launch {
            // Адрес — уже разобранный литерал: InetAddress не идет в DNS.
            val parsed = withContext(Dispatchers.Default) { InetAddress.getByName(address) }
            val info = if (!PublicAddress.isPublic(parsed)) {
                AddressInfo.failed(address, AddressFailure.Private)
            } else {
                val (overview, geo) = PublicAddress.requests(parsed).map { async { web.get(it) } }.map { it.await() }
                PublicAddress.parse(address, overview, geo)
            }

            publicIp.update { if (it.address == address) it.copy(describing = false, info = info) else it }
        }
    }

    fun kind(kind: ProbeKind) = probe.update { it.copy(kind = kind, error = null) }

    fun target(text: String) = probe.update { it.copy(target = text, error = null) }

    fun port(text: String) = probe.update { it.copy(port = text, error = null) }

    /** Замер по введенному: узел — имя или IP (правила десктопа), порт — у TCP и TLS. */
    fun run() {
        val current = probe.value
        if (current.running) {
            return
        }

        val target = ProbeTarget.normalize(current.target)
        val port = ProbeTarget.port(current.port)
        val error = when {
            target == null -> R.string.probe_error_target
            current.kind != ProbeKind.Dns && port == null -> R.string.probe_error_port
            else -> null
        }

        if (error != null || target == null) {
            probe.update { it.copy(error = error, result = null) }
            return
        }

        probe.update { it.copy(running = true, error = null, result = null, viaTunnel = protected()) }
        viewModelScope.launch {
            val result = try {
                withContext(Dispatchers.IO) {
                    when (current.kind) {
                        ProbeKind.Dns -> runner.dns(target)
                        ProbeKind.Tcp -> runner.tcp(target, port ?: 0)
                        ProbeKind.Tls -> runner.tls(target, port ?: 0)
                    }
                }
            } catch (e: CancellationException) {
                throw e
            } catch (_: Exception) {
                null
            }

            probe.update { it.copy(running = false, result = result, error = if (result == null) R.string.probe_summary_failed else null) }
        }
    }

    private fun protected() = monitor.status.value.state == TunnelState.Connected
}

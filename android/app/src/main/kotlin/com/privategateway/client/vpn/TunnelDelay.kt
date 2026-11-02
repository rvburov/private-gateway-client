package com.privategateway.client.vpn

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import com.privategateway.client.core.servers.ProxyProtocol
import com.privategateway.client.core.servers.TransportKind
import com.privategateway.client.core.vpn.TunnelState
import com.privategateway.client.core.xray.PingBatch
import com.privategateway.client.data.ServerRepository
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.IOException
import java.io.InputStream
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket
import java.net.URI
import javax.inject.Inject
import javax.net.ssl.HttpsURLConnection
import javax.net.ssl.SSLSocket
import javax.net.ssl.SSLSocketFactory
import kotlin.math.roundToInt

/**
 * «Задержка» на «Проверке» (решение 509, DelayBreakdown десктопа): до сервера, через сервер по открытому соединению и новое
 * соединение — медианы замеров, мс; не измерено — null. [measuredAt] — когда замер кончился (мс от эпохи).
 */
data class DelayBreakdown(val toServerMs: Int?, val throughServerMs: Int?, val newConnectionMs: Int?, val measuredAt: Long) {
    /** Сколько кругов до сервера уходит на новое соединение — по замерам, с одним знаком после запятой; не измерено — null. */
    val rounds: Double?
        get() {
            val one = toServerMs ?: return null
            val fresh = newConnectionMs ?: return null
            return if (one > 0) (fresh * 10.0 / one).roundToInt() / 10.0 else null
        }

    companion object {
        /** Медиана: середина по порядку, у четного числа — среднее двух средних (с округлением вверх); замеров нет — null. */
        fun median(samples: List<Int>): Int? {
            if (samples.isEmpty()) {
                return null
            }

            val sorted = samples.sorted()
            val middle = sorted.size / 2
            return if (sorted.size % 2 == 1) sorted[middle] else ((sorted[middle - 1] + sorted[middle]) / 2.0).roundToInt()
        }
    }
}

/** Замер задержки под защитой; защиты нет — null. В тестах — подделка. */
fun interface TunnelDelay {
    suspend fun measure(): DelayBreakdown?
}

/**
 * Задержка, как у десктопа (Gateway.Delay): «До сервера» — прямое соединение TCP с адресом сервера мимо туннеля (сокет на
 * сети под туннелем: владельцу VPN Android это разрешает), один круг по сети; «Через сервер» — запрос к проверочному адресу
 * через туннель и сервер по уже открытому соединению (первый запрос — прогрев, не в счет); «Новое соединение» — тот же запрос
 * каждый раз по новому соединению: соединение с сервером, его рукопожатие, TLS до сайта и сам запрос. Каждое — медиана трех.
 * Сервер по UDP (Hysteria2, TUIC, WireGuard, QUIC) — «До сервера» не меряется. Адрес сервера никуда не пишется.
 */
class DeviceTunnelDelay @Inject constructor(
    @param:ApplicationContext private val context: Context,
    private val servers: ServerRepository,
    private val monitor: TunnelMonitor,
) : TunnelDelay {
    override suspend fun measure(): DelayBreakdown? {
        val status = monitor.status.value
        val id = status.serverId
        if (status.state != TunnelState.Connected || id == null) {
            return null
        }

        val server = servers.load(id)
        return withContext(Dispatchers.IO) {
            val direct = server?.takeIf { it.protocol !in UDP_PROTOCOLS && it.transport.kind !in UDP_TRANSPORTS }
            val toServer = direct?.let { toServer(it.host.trim('[', ']'), it.port) }
            val target = URI(PingBatch.TARGET)
            DelayBreakdown(toServer, through(target, warm = true), through(target, warm = false), System.currentTimeMillis())
        }
    }

    /** Соединение TCP с сервером через сеть под туннелем; адрес ищет DNS этой сети, а не туннеля (там FakeDNS). */
    private fun toServer(host: String, port: Int): Int? {
        val network = underlying() ?: return null
        val address = resolve(network, host) ?: return null
        val samples = mutableListOf<Int>()
        repeat(SAMPLES) {
            try {
                network.socketFactory.createSocket().use { socket ->
                    socket.tcpNoDelay = true
                    val started = System.nanoTime()
                    socket.connect(InetSocketAddress(address, port), TIMEOUT_MS)
                    samples += millis(started)
                }
            } catch (_: IOException) {
                // Не ответил — замер не в счет.
            } catch (_: SecurityException) {
                // Android не дал сокет мимо туннеля — замер не в счет.
            }
        }

        return DelayBreakdown.median(samples)
    }

    /** Адрес сервера через DNS сети под туннелем; не нашелся — null. */
    private fun resolve(network: Network, host: String): InetAddress? = try {
        network.getAllByName(host).firstOrNull()
    } catch (_: IOException) {
        null
    } catch (_: SecurityException) {
        null
    }

    /** Сеть под туннелем с выходом в интернет: проверенная Android — раньше непроверенной. */
    private fun underlying(): Network? {
        val manager = context.getSystemService(ConnectivityManager::class.java) ?: return null
        @Suppress("DEPRECATION")
        val all = manager.allNetworks
        val physical = all.mapNotNull { network ->
            manager.getNetworkCapabilities(network)
                ?.takeIf { !it.hasTransport(NetworkCapabilities.TRANSPORT_VPN) && it.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET) }
                ?.let { network to it.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED) }
        }

        return (physical.firstOrNull { it.second } ?: physical.firstOrNull())?.first
    }

    /** Запрос к проверочному адресу через туннель: [warm] — по одному соединению, иначе каждый раз по новому. */
    private fun through(target: URI, warm: Boolean): Int? {
        val host = target.host ?: return null
        val path = target.rawPath?.takeIf { it.isNotEmpty() } ?: "/"
        val address = try {
            InetAddress.getByName(host)
        } catch (_: IOException) {
            return null
        }

        val samples = mutableListOf<Int>()
        try {
            if (warm) {
                open(address, host).use { socket ->
                    if (!exchange(socket, host, path)) {
                        return null
                    }

                    repeat(SAMPLES) {
                        val started = System.nanoTime()
                        if (exchange(socket, host, path)) {
                            samples += millis(started)
                        }
                    }
                }
            } else {
                repeat(SAMPLES) {
                    try {
                        val started = System.nanoTime()
                        open(address, host).use { socket ->
                            if (exchange(socket, host, path)) {
                                samples += millis(started)
                            }
                        }
                    } catch (_: IOException) {
                        // Не ответил — замер не в счет.
                    }
                }
            }
        } catch (_: IOException) {
            // Соединение оборвалось — в счет только то, что успели.
        }

        return DelayBreakdown.median(samples)
    }

    /** Соединение TLS с проверочным адресом; сертификат и имя проверяются, как у любой программы. */
    private fun open(address: InetAddress, host: String): SSLSocket {
        val plain = Socket()
        try {
            plain.tcpNoDelay = true
            plain.soTimeout = TIMEOUT_MS
            plain.connect(InetSocketAddress(address, HTTPS_PORT), TIMEOUT_MS)
            val tls = (SSLSocketFactory.getDefault() as SSLSocketFactory).createSocket(plain, host, HTTPS_PORT, true) as SSLSocket
            tls.soTimeout = TIMEOUT_MS
            tls.startHandshake()
            if (!HttpsURLConnection.getDefaultHostnameVerifier().verify(host, tls.session)) {
                tls.close()
                throw IOException("hostname")
            }

            return tls
        } catch (e: IOException) {
            plain.close()
            throw e
        }
    }

    /** Один запрос GET и заголовки ответа; тело (если есть) дочитывается, чтобы соединение годилось для следующего. */
    private fun exchange(socket: Socket, host: String, path: String): Boolean {
        val request = "GET $path HTTP/1.1\r\nHost: $host\r\nUser-Agent: PrivateGateway\r\nAccept: */*\r\nConnection: keep-alive\r\n\r\n"
        val output = socket.getOutputStream()
        output.write(request.toByteArray(Charsets.US_ASCII))
        output.flush()
        val input = socket.getInputStream()
        val head = StringBuilder()
        while (!head.endsWith("\r\n\r\n")) {
            val next = input.read()
            if (next < 0 || head.length > HEAD_LIMIT) {
                return false
            }

            head.append(next.toChar())
        }

        val text = head.toString()
        val status = text.substringAfter(' ', "").take(3).toIntOrNull() ?: return false
        val length = CONTENT_LENGTH.find(text)?.groupValues?.get(1)?.toLongOrNull() ?: 0L
        return status in 100..599 && skip(input, length)
    }

    private fun skip(input: InputStream, length: Long): Boolean {
        var left = length
        while (left > 0) {
            if (input.read() < 0) {
                return false
            }

            left--
        }

        return true
    }

    private fun millis(started: Long): Int = ((System.nanoTime() - started) / 1_000_000.0).roundToInt().coerceAtLeast(1)

    private companion object {
        /** Сколько замеров на каждое число; на экране — медиана. */
        const val SAMPLES = 3
        const val TIMEOUT_MS = 5_000
        const val HTTPS_PORT = 443
        const val HEAD_LIMIT = 16 * 1024
        val CONTENT_LENGTH = Regex("""(?im)^content-length:\s*(\d+)""")
        val UDP_PROTOCOLS = setOf(ProxyProtocol.Hysteria2, ProxyProtocol.Tuic, ProxyProtocol.WireGuard)
        val UDP_TRANSPORTS = setOf(TransportKind.Quic, TransportKind.Udp)
    }
}

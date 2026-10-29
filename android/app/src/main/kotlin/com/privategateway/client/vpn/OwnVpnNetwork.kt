package com.privategateway.client.vpn

import android.content.Context
import android.net.ConnectivityManager
import android.net.LinkProperties
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import android.net.VpnService
import android.os.Build
import android.os.Process
import com.privategateway.client.core.subscriptions.SubscriptionRequest
import com.privategateway.client.core.vpn.TunPlan
import com.privategateway.client.core.vpn.VpnNetworkIdentity
import com.privategateway.client.core.vpn.VpnNetworkSelection
import com.privategateway.client.core.xray.SubscriptionProxy
import com.privategateway.client.core.subscriptions.SubscriptionDownloadException
import com.privategateway.client.core.subscriptions.DownloadFailure
import dagger.hilt.android.qualifiers.ApplicationContext
import java.io.IOException
import java.net.InetAddress
import javax.net.SocketFactory
import javax.inject.Inject
import javax.inject.Singleton
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.filterNotNull
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.withTimeoutOrNull

/**
 * Сеть и отдельный вход подписок данного запуска ядра. Ее handle закрепляет сокет к HTTP-прокси; имя подписки
 * разрешает сервер, не телефон. Исчезновение карты не переводит сокет на Wi-Fi/чужую VPN, а смена ядра отменяет запросы.
 */
@Singleton
class OwnVpnNetwork @Inject constructor(@param:ApplicationContext private val context: Context) {
    private val manager get() = context.getSystemService(ConnectivityManager::class.java)
    private val lock = Any()
    private val current = MutableStateFlow<Network?>(null)
    private var generation = 0L
    private var callback: ConnectivityManager.NetworkCallback? = null
    private var proxy: SubscriptionProxy? = null
    private var privateDns = false
    private val requests = HashSet<SubscriptionRequest>()

    data class Before(val networks: Set<Long>, val previousOwn: Long?)

    // Разовый снимок нужен для запрета старой чужой VPN до establish, а изменения после него ведет callback.
    @Suppress("DEPRECATION")
    private fun snapshotNetworks(): Array<Network> = manager.allNetworks

    fun beforeEstablish(): Before = synchronized(lock) {
        Before(snapshotNetworks().map { it.networkHandle }.toSet(), current.value?.networkHandle)
    }

    fun established(before: Before, plan: TunPlan, endpoint: SubscriptionProxy, hasPrivateDns: Boolean) {
        synchronized(lock) { proxy = endpoint; privateDns = hasPrivateDns }
        val selection = VpnNetworkSelection(before.networks, before.previousOwn, Process.myUid(), plan.addresses.map { InetAddress.getByName(it.address).hostAddress.orEmpty().substringBefore('%') }.toSet())
        val expectedGeneration = synchronized(lock) { generation }
        val listener = object : ConnectivityManager.NetworkCallback() {
            override fun onCapabilitiesChanged(network: Network, capabilities: NetworkCapabilities) = consider(network)

            override fun onLinkPropertiesChanged(network: Network, properties: LinkProperties) = consider(network)

            override fun onLost(network: Network) {
                synchronized(lock) {
                    if (expectedGeneration == generation && current.value == network) {
                        current.value = null
                        requests.toList().forEach { it.cancel() }
                    }
                }
            }

            private fun consider(network: Network) {
                val capabilities = manager.getNetworkCapabilities(network) ?: return
                val properties = manager.getLinkProperties(network) ?: return
                val facts = VpnNetworkIdentity(
                    network.networkHandle,
                    capabilities.hasTransport(NetworkCapabilities.TRANSPORT_VPN),
                    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) capabilities.ownerUid else null,
                    properties.interfaceName,
                    properties.linkAddresses.map { it.address.hostAddress.orEmpty().substringBefore('%') }.toSet(),
                )
                if (selection.accepts(facts, VpnService.prepare(context) == null)) {
                    synchronized(lock) {
                        if (expectedGeneration == generation) current.value = network
                    }
                }
            }
        }
        val request = NetworkRequest.Builder().removeCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN)
            .addTransportType(NetworkCapabilities.TRANSPORT_VPN).build()
        // clear не должен успеть снять еще не зарегистрированный callback и оставить его висеть после уничтожения службы.
        synchronized(lock) {
            if (expectedGeneration != generation) return
            callback = listener
            manager.registerNetworkCallback(request, listener)
        }
        // Сообщения приходят асинхронно; уже появившуюся карту тоже проверяем через те же правила.
        snapshotNetworks().forEach { network ->
            manager.getLinkProperties(network)?.let { listener.onLinkPropertiesChanged(network, it) }
        }
    }

    fun clear() {
        val previous = synchronized(lock) {
            generation++
            current.value = null
            proxy = null
            privateDns = false
            requests.toList().forEach { it.cancel() }
            requests.clear()
            callback.also { callback = null }
        }
        previous?.let { runCatching { manager.unregisterNetworkCallback(it) } }
    }

    class Captured(val proxy: SubscriptionProxy, val sockets: SocketFactory, val valid: () -> Unit)

    /** Дождаться регистрации нашей карты. Ни чужая, ни прежняя неизвестная VPN запасным путем не становятся. */
    suspend fun capture(request: SubscriptionRequest): Captured {
        val expectedGeneration = synchronized(lock) {
            if (privateDns) throw SubscriptionDownloadException(DownloadFailure.PrivateDns)
            generation
        }
        val network = withTimeoutOrNull(5_000) { current.filterNotNull().first() } ?: throw IOException("Сеть VPN не готова")
        val endpoint = synchronized(lock) {
            if (expectedGeneration != generation || current.value != network) throw IOException("Сеть VPN изменилась")
            if (privateDns) throw SubscriptionDownloadException(DownloadFailure.PrivateDns)
            requests.add(request)
            proxy ?: throw IOException("Вход подписок не готов")
        }
        return Captured(endpoint, network.socketFactory) {
            synchronized(lock) {
                if (expectedGeneration != generation || current.value != network || VpnService.prepare(context) != null) {
                    throw IOException("Сеть VPN изменилась")
                }
            }
        }
    }

    fun release(request: SubscriptionRequest) = synchronized(lock) { requests.remove(request) }
}

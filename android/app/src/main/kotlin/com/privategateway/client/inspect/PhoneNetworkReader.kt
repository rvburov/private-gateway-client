package com.privategateway.client.inspect

import android.content.Context
import android.net.ConnectivityManager
import android.net.LinkProperties
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import android.os.Build
import com.privategateway.client.core.inspect.AddressText
import com.privategateway.client.core.inspect.PhoneNetwork
import com.privategateway.client.core.inspect.PhoneNetworkKind
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import java.util.concurrent.ConcurrentHashMap
import javax.inject.Inject
import javax.inject.Singleton

/** Сети телефона для инспектора; в тестах — подделка. */
interface PhoneNetworkSource {
    val networks: StateFlow<List<PhoneNetwork>>

    fun start()
}

/**
 * Сети телефона, как их видит Android (решение 448): все, что Android сообщает приложению (и VPN — свой или чужой), с
 * адресами, шлюзами, DNS и признаками; сеть по умолчанию приложения отмечена. Обновляется сама по сообщениям Android,
 * без опроса. Только чтение.
 */
@Singleton
class PhoneNetworkReader @Inject constructor(@param:ApplicationContext private val context: Context) : PhoneNetworkSource {
    private val known = ConcurrentHashMap<Network, Pair<NetworkCapabilities?, LinkProperties?>>()
    private val state = MutableStateFlow<List<PhoneNetwork>>(emptyList())

    @Volatile
    private var default: Network? = null
    private var started = false

    override val networks: StateFlow<List<PhoneNetwork>> = state.asStateFlow()

    @Synchronized
    override fun start() {
        if (started) {
            return
        }

        started = true
        val manager = context.getSystemService(ConnectivityManager::class.java)
        // Запрос без «не VPN» — видны и туннели.
        val all = NetworkRequest.Builder().removeCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN).build()
        manager.registerNetworkCallback(
            all,
            object : ConnectivityManager.NetworkCallback() {
                override fun onCapabilitiesChanged(network: Network, capabilities: NetworkCapabilities) {
                    known[network] = capabilities to known[network]?.second
                    publish()
                }

                override fun onLinkPropertiesChanged(network: Network, properties: LinkProperties) {
                    known[network] = known[network]?.first to properties
                    publish()
                }

                override fun onLost(network: Network) {
                    known.remove(network)
                    publish()
                }
            },
        )
        manager.registerDefaultNetworkCallback(
            object : ConnectivityManager.NetworkCallback() {
                override fun onAvailable(network: Network) {
                    default = network
                    publish()
                }

                override fun onLost(network: Network) {
                    if (default == network) {
                        default = null
                    }

                    publish()
                }
            },
        )
    }

    private fun publish() {
        state.value = known.map { (network, facts) -> describe(network == default, facts.first, facts.second) }
    }

    private fun describe(isDefault: Boolean, capabilities: NetworkCapabilities?, properties: LinkProperties?): PhoneNetwork {
        val kind = when {
            capabilities == null -> PhoneNetworkKind.Other
            capabilities.hasTransport(NetworkCapabilities.TRANSPORT_VPN) -> PhoneNetworkKind.Vpn
            capabilities.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) -> PhoneNetworkKind.Wifi
            capabilities.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR) -> PhoneNetworkKind.Cellular
            capabilities.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET) -> PhoneNetworkKind.Ethernet
            else -> PhoneNetworkKind.Other
        }

        val gateways = properties?.routes.orEmpty()
            .filter { it.isDefaultRoute }
            .mapNotNull { route -> route.gateway?.takeUnless { it.isAnyLocalAddress } }
            .map(AddressText::of)
            .distinct()
        return PhoneNetwork(
            kind = kind,
            name = properties?.interfaceName,
            isDefault = isDefault,
            addresses = properties?.linkAddresses.orEmpty().map { AddressText.of(it.address) + "/" + it.prefixLength },
            gateways = gateways,
            dns = properties?.dnsServers.orEmpty().map(AddressText::of),
            privateDns = properties?.takeIf { it.isPrivateDnsActive }?.let { it.privateDnsServerName.orEmpty() },
            mtu = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) properties?.mtu?.takeIf { it > 0 } else null,
            metered = capabilities?.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_METERED) == false,
            validated = capabilities?.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED) == true,
            captive = capabilities?.hasCapability(NetworkCapabilities.NET_CAPABILITY_CAPTIVE_PORTAL) == true,
        )
    }
}

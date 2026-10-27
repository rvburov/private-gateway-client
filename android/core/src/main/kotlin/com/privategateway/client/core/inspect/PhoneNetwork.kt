package com.privategateway.client.core.inspect

/** Вид сети телефона — как адаптеры десктопа (`AdapterKind`), только то, что называет Android. */
enum class PhoneNetworkKind {
    Wifi,
    Cellular,
    Ethernet,

    /** VPN — свой туннель или чужой. */
    Vpn,
    Other,
}

/**
 * Сеть телефона, как ее видит Android: вид, интерфейс, сеть ли это по умолчанию для приложения, адреса с префиксами,
 * шлюзы, DNS, «Частный DNS» (null — выключен, пустая строка — автоматически, иначе имя сервера), MTU (Android 10+),
 * платная ли, проверил ли Android выход в интернет, нужен ли вход в сеть Wi-Fi. Только чтение: инспектор ничего не меняет.
 */
data class PhoneNetwork(
    val kind: PhoneNetworkKind,
    val name: String?,
    val isDefault: Boolean,
    val addresses: List<String>,
    val gateways: List<String>,
    val dns: List<String>,
    val privateDns: String?,
    val mtu: Int?,
    val metered: Boolean,
    val validated: Boolean,
    val captive: Boolean,
)

object PhoneNetworks {
    /** Порядок на экране: сеть по умолчанию первой, затем VPN, Wi-Fi, сотовая, кабель и прочие; внутри — по имени. */
    fun order(networks: List<PhoneNetwork>): List<PhoneNetwork> = networks.sortedWith(
        compareByDescending<PhoneNetwork> { it.isDefault }
            .thenBy { rank.indexOf(it.kind) }
            .thenBy { it.name.orEmpty() },
    )

    private val rank = listOf(PhoneNetworkKind.Vpn, PhoneNetworkKind.Wifi, PhoneNetworkKind.Cellular, PhoneNetworkKind.Ethernet, PhoneNetworkKind.Other)
}

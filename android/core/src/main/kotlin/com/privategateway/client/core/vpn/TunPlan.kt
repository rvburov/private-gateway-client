package com.privategateway.client.core.vpn

import com.privategateway.client.core.xray.CoreSettings
import com.privategateway.client.core.xray.XrayConfig

/** Адрес с длиной префикса: «172.19.0.1/30». */
data class Prefix(val address: String, val length: Int) {
    override fun toString() = "$address/$length"
}

/**
 * Что получает VpnService.Builder перед establish(): адреса карты туннеля, маршруты, DNS и MTU. Весь интернет — в
 * туннель (0.0.0.0/0); DNS — адрес внутри туннеля, его запросы ядро отвечает через сервер (XrayConfig: порт 53 → выход
 * DNS). IPv6 — только с настройкой «IPv6 через сервер»: без адреса и маршрута IPv6 Android сам закрывает IPv6 для всех
 * программ (allowFamily не вызывается) — программа сразу получает «сеть недоступна» и уходит на IPv4, мимо туннеля IPv6
 * не идет.
 */
data class TunPlan(val addresses: List<Prefix>, val routes: List<Prefix>, val dnsServers: List<String>, val mtu: Int) {
    companion object {
        /** Свой адрес IPv6 туннеля — из частного диапазона fd00::/8 (ULA), в интернете его нет. */
        const val TUN_ADDRESS6 = "fd66:7067:6763::1"
        const val TUN_PREFIX6 = 126

        fun of(settings: CoreSettings): TunPlan {
            val addresses = mutableListOf(Prefix(XrayConfig.TUN_ADDRESS, XrayConfig.TUN_PREFIX))
            val routes = mutableListOf(Prefix("0.0.0.0", 0))
            if (settings.ipv6) {
                addresses.add(Prefix(TUN_ADDRESS6, TUN_PREFIX6))
                routes.add(Prefix("::", 0))
            }

            return TunPlan(addresses, routes, listOf(XrayConfig.TUN_DNS), settings.mtu)
        }
    }
}

/**
 * DNS для ядра до туннеля (libXray setDNS): им ядро находит адрес самого сервера, сокет защищен от туннеля. Берется DNS
 * текущей сети телефона (как у десктопа — разрешитель системы); IPv4 — первым; сети без DNS — запасной [FALLBACK].
 * Адреса туннеля и петли (127.0.0.0/8, ::1) не годятся: запрос ушел бы в туннель, которого еще нет, — круг.
 */
object CoreDns {
    const val FALLBACK = "1.1.1.1:53"

    fun endpoint(networkDns: List<String>): String {
        val usable = networkDns.map { it.substringBefore('%') }.filter { usable(it) }
        usable.firstOrNull { !it.contains(':') }?.let { return "$it:53" }
        usable.firstOrNull()?.let { return "[$it]:53" }
        return FALLBACK
    }

    private fun usable(address: String): Boolean {
        if (address.isEmpty() || address == XrayConfig.TUN_DNS || address == "::1" || address.startsWith("127.")) {
            return false
        }

        return address.contains(':') || address.split('.').let { parts -> parts.size == 4 && parts.all { it.toIntOrNull() in 0..255 } }
    }
}

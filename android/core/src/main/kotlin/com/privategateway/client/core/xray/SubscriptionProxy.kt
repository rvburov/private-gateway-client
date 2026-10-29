package com.privategateway.client.core.xray

import com.privategateway.client.core.servers.ProxyProtocol
import java.net.URI

/** Вход только для подписок данного запуска ядра. Пароль живет в памяти и не попадает в toString или журнал. */
class SubscriptionProxy(val port: Int, val user: String, val password: String) {
    init { require(port in 1..65535 && user.isNotEmpty() && password.isNotEmpty()) }

    companion object {
        // WireGuard сам разрешает имя адресата. Учитываем и особое правило lanDns:53, и все localNetworks, которые
        // allowLan отправляет напрямую (включая DoH на частном/особом IP). Неизвестное имя не проверяем по сети.
        fun hasPrivateDns(protocol: ProxyProtocol, settings: CoreSettings): Boolean {
            if (protocol != ProxyProtocol.WireGuard) return false
            if (!settings.lanDns.isNullOrEmpty()) return true
            if (!settings.allowLan) return false
            val text = settings.dns.trim()
            XrayConfig.localAddress(text)?.let { return it }
            val uri = runCatching { URI(text) }.getOrNull() ?: return true
            if (!uri.scheme.equals("https", ignoreCase = true)) return true
            val host = uri.host ?: return true
            return XrayConfig.localAddress(host) ?: true
        }
    }
}

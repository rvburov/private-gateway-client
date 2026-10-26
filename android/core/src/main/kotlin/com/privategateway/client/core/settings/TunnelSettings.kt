package com.privategateway.client.core.settings

import com.privategateway.client.core.xray.CoreSettings
import com.privategateway.client.core.xray.XrayConfig

/** Куда уходят DNS-запросы в туннеле — выбор десктопа («Защита и утечки», доска 5). */
enum class DnsChoice {
    /** DNS по умолчанию ядра — DoH Cloudflare по адресу. */
    Profile,
    CloudflareDoh,
    Quad9Doh,

    /** Свой адрес: IP сервера DNS или DoH по IP (https://IP/…). */
    Custom,
}

/**
 * Настройки туннеля Android: DNS, IPv6 через сервер, локальная сеть. По умолчанию — как решено для защиты: локальная сеть
 * закрыта, IPv6 закрывает сам Android (решение 397), DNS — DoH по адресу.
 */
data class TunnelSettings(
    val dns: DnsChoice = DnsChoice.Profile,
    val customDns: String? = null,
    val ipv6: Boolean = false,
    val allowLan: Boolean = false,
) {
    /** Сервер DNS для ядра; свой адрес не годится — DNS по умолчанию (экран настроек такой адрес не сохраняет). */
    val dnsServer: String
        get() = when (dns) {
            DnsChoice.Profile, DnsChoice.CloudflareDoh -> XrayConfig.DEFAULT_DNS
            DnsChoice.Quad9Doh -> QUAD9_DOH
            DnsChoice.Custom -> customDns?.trim()?.takeIf { validCustomDns(it) } ?: XrayConfig.DEFAULT_DNS
        }

    /**
     * Свой DNS в частной сети (адрес, как у Pi-hole на 192.168.x.x): Xray спрашивает его напрямую, мимо сервера — через
     * сервер частный адрес не достать. Только свой адрес IP: DoH идет по порту 443, а не 53. Иначе null.
     */
    val lanDns: String?
        get() = if (dns == DnsChoice.Custom) customDns?.trim()?.takeIf { PrivateNetwork.contains(it) } else null

    fun core(): CoreSettings = CoreSettings(dns = dnsServer, allowLan = allowLan, ipv6 = ipv6, lanDns = lanDns)

    /**
     * Нужно ли переподключиться, чтобы [next] вошли в работу, — как `ProtectionSync.Affects` десктопа: только то, что
     * получает ядро. Неверный свой адрес равен «Из профиля» (ядро получает DNS по умолчанию), и ради него переподключаться
     * не нужно.
     */
    fun needsReconnect(next: TunnelSettings): Boolean = core() != next.core()

    companion object {
        const val QUAD9_DOH = "https://9.9.9.9/dns-query"

        /**
         * Свой DNS: IPv4 или IPv6 адрес либо DoH по адресу («https://IP/путь»). Имя хоста не годится: чтобы найти сам
         * DNS-сервер, ядру пришлось бы спросить имя мимо туннеля.
         */
        fun validCustomDns(value: String): Boolean {
            val text = value.trim()
            if (IpAddress.parse(text) != null) {
                return true
            }

            if (!text.startsWith("https://", ignoreCase = true)) {
                return false
            }

            // IPv6 — в скобках («https://[2001:db8::1]/…»), за адресом — разве что порт.
            val authority = text.substring("https://".length).substringBefore('/')
            val bracket = authority.startsWith("[")
            val end = if (bracket) authority.indexOf(']') else authority.indexOf(':').takeIf { it >= 0 } ?: authority.length
            if (end < 0) {
                return false
            }

            val host = authority.substring(if (bracket) 1 else 0, end)
            val rest = authority.substring(if (bracket) end + 1 else end)
            return port(rest) && IpAddress.parse(host)?.let { bracket == (it.size == 16) } == true
        }

        /** За адресом — ничего или «:порт» от 1 до 65535. */
        private fun port(rest: String) =
            rest.isEmpty() || (rest.startsWith(":") && rest.substring(1).let { it.length in 1..5 && it.all(Char::isDigit) && it.toInt() in 1..65535 })
    }
}

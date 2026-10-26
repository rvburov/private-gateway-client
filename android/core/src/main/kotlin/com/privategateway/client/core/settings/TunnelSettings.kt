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

    fun core(): CoreSettings = CoreSettings(dns = dnsServer, allowLan = allowLan, ipv6 = ipv6)

    companion object {
        const val QUAD9_DOH = "https://9.9.9.9/dns-query"

        private val ipv4 = Regex("""^((25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.){3}(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)$""")

        /**
         * Свой DNS: IPv4 или IPv6 адрес либо DoH по адресу («https://IP/путь»). Имя хоста не годится: чтобы найти сам
         * DNS-сервер, ядру пришлось бы спросить имя мимо туннеля.
         */
        fun validCustomDns(value: String): Boolean {
            val text = value.trim()
            if (ip(text)) {
                return true
            }

            if (!text.startsWith("https://", ignoreCase = true)) {
                return false
            }

            val authority = text.substring("https://".length).substringBefore('/')
            val host = if (authority.startsWith("[")) authority.substringAfter('[').substringBefore(']') else authority.substringBefore(':')
            return ip(host)
        }

        private fun ip(text: String) = ipv4.matches(text) || (text.contains(':') && text.all { it.isDigit() || it in 'a'..'f' || it in 'A'..'F' || it == ':' || it == '.' })
    }
}

package com.privategateway.client.core.xray

import com.privategateway.client.core.servers.ProxyProtocol
import org.junit.jupiter.params.ParameterizedTest
import org.junit.jupiter.params.provider.CsvSource
import org.junit.jupiter.api.Test
import kotlin.test.assertFalse
import kotlin.test.assertTrue
import kotlin.test.assertEquals

class SubscriptionProxyTest {
    @ParameterizedTest
    @CsvSource(
        "0.0.0.53, true", "9.255.255.255, false", "10.0.0.0, true", "10.255.255.255, true", "11.0.0.0, false",
        "100.63.255.255, false", "100.64.0.0, true", "100.127.255.255, true", "100.128.0.0, false",
        "127.0.0.1, true", "192.0.2.53, true", "198.18.0.53, true", "198.19.255.255, true", "198.20.0.0, false",
        "224.0.0.53, true", "255.255.255.255, true", "1.1.1.1, false",
        "::, true", "::1, true", "fbff::53, false", "fc00::53, true", "fdff::53, true", "fe00::53, false",
        "fe7f::53, false", "fe80::53, true", "febf::53, true", "fec0::53, false", "ff00::53, true",
        "::ffff:127.0.0.53, true", "::ffff:100.64.0.53, true", "::ffff:1.1.1.1, false", "2606:4700:4700::1111, false",
        "https://192.168.1.53/dns-query, true", "https://127.0.0.1:8443/dns-query, true",
        "https://[::ffff:100.64.0.53]/dns-query, true", "https://[fd00::53]/dns-query, true",
        "https://1.1.1.1/dns-query, false", "https://[2606:4700:4700::1111]/dns-query, false",
        "dns.example.invalid, true", "https://dns.example.invalid/dns-query, true", "https://[broken]/dns-query, true",
    )
    fun `wireguard rejects dns that lan routing can send directly without resolving unknown names`(dns: String, blocked: Boolean) {
        assertEquals(blocked, SubscriptionProxy.hasPrivateDns(ProxyProtocol.WireGuard, CoreSettings(dns = dns, allowLan = true)), dns)
        assertFalse(SubscriptionProxy.hasPrivateDns(ProxyProtocol.Vless, CoreSettings(dns = dns, allowLan = true)), "VLESS отправляет имя серверу")
    }

    @Test
    fun `a specific lan dns route matters even while general lan access is off`() {
        assertTrue(SubscriptionProxy.hasPrivateDns(ProxyProtocol.WireGuard, CoreSettings(lanDns = "192.168.1.53")))
        assertFalse(SubscriptionProxy.hasPrivateDns(ProxyProtocol.WireGuard, CoreSettings(dns = "https://192.168.1.53/dns-query", allowLan = false)), "без LAN этот DoH идет в блок, не напрямую")
        assertFalse(SubscriptionProxy.hasPrivateDns(ProxyProtocol.WireGuard, CoreSettings()), "публичный DoH через сервер остается доступен")
        assertFalse(SubscriptionProxy.hasPrivateDns(ProxyProtocol.WireGuard, CoreSettings(lanDns = "")), "пустой lanDns не создает прямого правила")
    }
}

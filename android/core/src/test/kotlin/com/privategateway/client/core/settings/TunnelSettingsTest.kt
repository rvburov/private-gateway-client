package com.privategateway.client.core.settings

import com.privategateway.client.core.xray.XrayConfig
import org.junit.jupiter.api.Test
import org.junit.jupiter.params.ParameterizedTest
import org.junit.jupiter.params.provider.ValueSource
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

class TunnelSettingsTest {
    @Test
    fun `defaults close the local network and ipv6 and use doh by address`() {
        val core = TunnelSettings().core()
        assertEquals(XrayConfig.DEFAULT_DNS, core.dns)
        assertFalse(core.allowLan)
        assertFalse(core.ipv6)
    }

    @Test
    fun `choices map to core settings`() {
        assertEquals(TunnelSettings.QUAD9_DOH, TunnelSettings(dns = DnsChoice.Quad9Doh).core().dns)
        assertEquals("192.0.2.53", TunnelSettings(dns = DnsChoice.Custom, customDns = " 192.0.2.53 ").core().dns)
        assertEquals(XrayConfig.DEFAULT_DNS, TunnelSettings(dns = DnsChoice.Custom, customDns = "dns.example.com").core().dns)
        assertTrue(TunnelSettings(ipv6 = true, allowLan = true).core().let { it.ipv6 && it.allowLan })
    }

    @ParameterizedTest
    @ValueSource(
        strings = [
            "192.0.2.53", "2001:db8::53", "https://198.51.100.1/dns-query", "https://[2001:db8::1]/dns-query", "https://203.0.113.5:8443/q",
            " 2001:DB8::53 ", "::ffff:192.0.2.53", "HTTPS://198.51.100.1/dns-query", "https://198.51.100.1", "https://[2001:db8::1]:443/q",
            "https://203.0.113.5:65535/q",
        ],
    )
    fun `custom dns by address is accepted`(value: String) {
        assertTrue(TunnelSettings.validCustomDns(value))
    }

    @ParameterizedTest
    @ValueSource(
        strings = [
            "dns.example.com", "https://dns.example.com/dns-query", "http://192.0.2.53/dns-query", "256.1.1.1", "", "tls://192.0.2.53",
            "::::", "1::2::3", ":::", "1.2", "1:2:3:4:5:6:7", "fe80::1%wlan0", "https://[::::]/dns-query", "https://[1::2::3]/q",
            "https://[2001:db8::1/dns-query", "https://[2001:db8::1]x/q", "https://2001:db8::1/dns-query", "https://[192.0.2.53]/q",
            "https://203.0.113.5:/q", "https://203.0.113.5:0/q", "https://203.0.113.5:65536/q", "https://203.0.113.5:84a3/q",
            "https://user@203.0.113.5/q", "https://",
        ],
    )
    fun `custom dns by name or bad address is refused`(value: String) {
        assertFalse(TunnelSettings.validCustomDns(value))
    }

    @Test
    fun `own dns in a private network is only a custom ip address`() {
        val custom = TunnelSettings(dns = DnsChoice.Custom)
        assertEquals("192.168.1.53", custom.copy(customDns = " 192.168.1.53 ").lanDns)
        assertEquals("fd00::53", custom.copy(customDns = "fd00::53").lanDns)
        assertEquals("192.168.1.53", custom.copy(customDns = "192.168.1.53").core().lanDns, "ядро получает адрес для правила")
        assertNull(custom.copy(customDns = "9.9.9.9").lanDns, "адрес в интернете — через сервер")
        assertNull(custom.copy(customDns = "https://192.168.1.53/dns-query").lanDns, "DoH — по порту 443")
        assertNull(custom.copy(customDns = "192.168.1").lanDns, "не адрес")
        assertNull(custom.lanDns)
        assertNull(TunnelSettings(dns = DnsChoice.CloudflareDoh, customDns = "192.168.1.53").lanDns, "свой адрес не выбран")
        assertNull(TunnelSettings(dns = DnsChoice.Profile, customDns = "192.168.1.53").core().lanDns)
    }

    @Test
    fun `only what the core gets needs a reconnect`() {
        val base = TunnelSettings()
        listOf(
            base.copy(dns = DnsChoice.Quad9Doh),
            base.copy(dns = DnsChoice.Custom, customDns = "192.0.2.53"),
            base.copy(ipv6 = true),
            base.copy(allowLan = true),
        ).forEach { assertTrue(base.needsReconnect(it), "ядро получает: $it") }

        val own = TunnelSettings(dns = DnsChoice.Custom, customDns = "192.0.2.53")
        assertTrue(own.needsReconnect(own.copy(customDns = "198.51.100.53")), "другой свой адрес")
        assertTrue(own.copy(customDns = "192.168.1.53").needsReconnect(own.copy(customDns = "192.168.1.54")), "другой свой адрес в частной сети")

        assertFalse(base.needsReconnect(base))
        assertFalse(base.needsReconnect(base.copy(dns = DnsChoice.CloudflareDoh)), "«Из профиля» — тот же DoH Cloudflare")
        assertFalse(base.needsReconnect(base.copy(dns = DnsChoice.Custom, customDns = "::::")), "неверный свой адрес равен «Из профиля»")
        assertFalse(base.needsReconnect(base.copy(dns = DnsChoice.Custom)), "своего адреса нет — DNS по умолчанию")
        assertFalse(base.needsReconnect(base.copy(customDns = "192.0.2.53")), "свой адрес не выбран")
        assertFalse(own.needsReconnect(own.copy(customDns = " 192.0.2.53 ")), "тот же адрес с пробелами")
    }
}

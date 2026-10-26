package com.privategateway.client.core.settings

import com.privategateway.client.core.xray.XrayConfig
import org.junit.jupiter.api.Test
import org.junit.jupiter.params.ParameterizedTest
import org.junit.jupiter.params.provider.ValueSource
import kotlin.test.assertEquals
import kotlin.test.assertFalse
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
    @ValueSource(strings = ["192.0.2.53", "2001:db8::53", "https://198.51.100.1/dns-query", "https://[2001:db8::1]/dns-query", "https://203.0.113.5:8443/q"])
    fun `custom dns by address is accepted`(value: String) {
        assertTrue(TunnelSettings.validCustomDns(value))
    }

    @ParameterizedTest
    @ValueSource(strings = ["dns.example.com", "https://dns.example.com/dns-query", "http://192.0.2.53/dns-query", "256.1.1.1", "", "tls://192.0.2.53"])
    fun `custom dns by name or bad address is refused`(value: String) {
        assertFalse(TunnelSettings.validCustomDns(value))
    }
}

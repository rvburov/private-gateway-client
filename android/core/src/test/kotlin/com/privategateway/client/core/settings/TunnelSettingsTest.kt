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

    @Test
    fun `connection settings reach the core and the tunnel plan`() {
        val defaults = TunnelSettings()
        assertEquals(CoreLogLevel.Warning, defaults.core().logLevel)
        assertEquals(ConnectionSettings.AUTO_MTU, defaults.core().mtu)
        assertTrue(defaults.core().sniffing)
        assertNull(defaults.core().fingerprint, "«Из профиля»")
        assertFalse(defaults.core().mux)
        assertFalse(defaults.core().fragment)
        assertFalse(defaults.plan().allowBypass, "строгие маршруты по умолчанию включены")

        val chosen = TunnelSettings(
            connection = ConnectionSettings(
                coreLog = CoreLogLevel.Info,
                mtu = 1420,
                strictRoutes = false,
                sniffing = false,
                fingerprint = TlsFingerprint.Safari,
                mux = true,
                tlsFragment = true,
            ),
        )
        val core = chosen.core()
        assertEquals(CoreLogLevel.Info, core.logLevel)
        assertEquals(1420, core.mtu)
        assertEquals(1420, chosen.plan().mtu, "карта туннеля и вход TUN — с одним MTU")
        assertFalse(core.sniffing)
        assertEquals("safari", core.fingerprint)
        assertTrue(core.mux && core.fragment)
        assertTrue(chosen.plan().allowBypass)
    }

    @Test
    fun `routing reaches the core and the tunnel plan only in the split mode`() {
        val apps = listOf(BypassApp("org.example.game", "Игра"), BypassApp("org.example.call", "Звонки", enabled = false))
        val rules = listOf(SiteRule("example.com", RuleAction.Direct), SiteRule("ads.example.org", RuleAction.Block))
        val all = TunnelSettings(routing = RoutingSettings(RoutingMode.AllTraffic, apps, rules))
        assertEquals(emptyList(), all.plan().disallowedApps, "в режиме «Весь трафик» мимо VPN не идет ни одна программа")
        assertEquals(listOf(SiteRule("ads.example.org", RuleAction.Block)), all.core().rules)

        val split = all.copy(routing = all.routing.copy(mode = RoutingMode.Split))
        assertEquals(listOf("org.example.game"), split.plan().disallowedApps, "только включенные")
        assertEquals(rules, split.core().rules)
    }

    @Test
    fun `what the core or the plan gets needs a reconnect and the rest does not`() {
        val base = TunnelSettings()
        listOf(
            base.copy(connection = ConnectionSettings(coreLog = CoreLogLevel.Debug)),
            base.copy(connection = ConnectionSettings(mtu = 1280)),
            base.copy(connection = ConnectionSettings(strictRoutes = false)),
            base.copy(connection = ConnectionSettings(sniffing = false)),
            base.copy(connection = ConnectionSettings(fingerprint = TlsFingerprint.Random)),
            base.copy(connection = ConnectionSettings(mux = true)),
            base.copy(connection = ConnectionSettings(tlsFragment = true)),
            base.copy(routing = RoutingSettings(rules = listOf(SiteRule("ads.example.org", RuleAction.Block)))),
        ).forEach { assertTrue(base.needsReconnect(it), "ядро или карта получают: $it") }

        assertFalse(base.needsReconnect(base.copy(connection = ConnectionSettings(connectTimeoutSeconds = 30))), "таймаут — со следующей пробы")
        assertFalse(base.needsReconnect(base.copy(connection = ConnectionSettings(mtu = 1500))), "1500 — то же, что «Авто»")
        assertFalse(base.needsReconnect(base.copy(routing = RoutingSettings(RoutingMode.Split))), "раздельный режим без исключений ничего не меняет")

        val apps = RoutingSettings(apps = listOf(BypassApp("org.example.game", "Игра")), rules = listOf(SiteRule("example.com", RuleAction.Direct)))
        assertFalse(base.needsReconnect(base.copy(routing = apps)), "в режиме «Весь трафик» программы и «Напрямую» молчат")
        val split = base.copy(routing = apps.copy(mode = RoutingMode.Split))
        assertTrue(base.needsReconnect(split))
        assertTrue(split.needsReconnect(split.copy(routing = split.routing.setApp(split.routing.apps.single(), split.routing.apps.single().copy(enabled = false)))))
    }

    @Test
    fun `stored connection settings fall back to the defaults`() {
        assertEquals(ConnectionSettings(), ConnectionSettings.read(null, null, null, null, null, null, null, null))
        assertEquals(
            ConnectionSettings(CoreLogLevel.Debug, 1420, false, false, TlsFingerprint.Firefox, true, true, 20),
            ConnectionSettings.read("Debug", 1420, false, false, "Firefox", true, true, 20),
        )
        // Значения из будущей версии или испорченные — по умолчанию, а не авария и не MTU, который режет пакеты.
        val odd = ConnectionSettings.read("Trace", 9000, null, null, "Edge", null, null, 0)
        assertEquals(CoreLogLevel.Warning, odd.coreLog)
        assertNull(odd.mtu)
        assertEquals(TlsFingerprint.Profile, odd.fingerprint)
        assertEquals(ConnectionSettings.DEFAULT_TIMEOUT_SECONDS, odd.connectTimeoutSeconds)
    }

    @Test
    fun `custom mtu is a number from 1280 to 1500 and timeout feeds the probe`() {
        assertEquals(1280, ConnectionSettings.parseMtu("1280"))
        assertEquals(1500, ConnectionSettings.parseMtu(" 1500 "))
        assertEquals(1400, ConnectionSettings.parseMtu("1400"))
        listOf("", "1279", "1501", "9000", "14a0", "-1300", "1300.5", "01300", "١٤٠٠").forEach { assertNull(ConnectionSettings.parseMtu(it), it) }
        assertEquals(ConnectionSettings.AUTO_MTU, ConnectionSettings(mtu = 9000).tunMtu, "недопустимый MTU — «Авто»")

        assertEquals(10_000L, ConnectionSettings().probeTimeoutMs)
        assertEquals(30_000L, ConnectionSettings(connectTimeoutSeconds = 30).probeTimeoutMs)
        assertEquals(10_000L, ConnectionSettings(connectTimeoutSeconds = 0).probeTimeoutMs)
        assertEquals(listOf(10, 5, 20, 30), ConnectionSettings.TIMEOUT_CHOICES, "как у десктопа: по умолчанию первым")
        assertEquals(listOf(null, 1500, 1420, 1280), ConnectionSettings.MTU_CHOICES)
    }
}

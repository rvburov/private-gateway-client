package com.privategateway.client.core.settings

import org.junit.jupiter.api.Test
import org.junit.jupiter.params.ParameterizedTest
import org.junit.jupiter.params.provider.CsvSource
import org.junit.jupiter.params.provider.ValueSource
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * «Маршрутизация» телефона (доска 13): шаблоны сайтов — как `SitePattern` десктопа, но без geosite; правила и программы
 * мимо VPN действуют только там, где их выполняет ядро или Android; хранилище переживает испорченный текст.
 */
class RoutingSettingsTest {
    @ParameterizedTest
    @CsvSource(
        "example.com, example.com",
        "' Example.COM ', example.com",
        "*.local, *.local",
        "*.Example.org, *.example.org",
        "https://www.example.com/path?q=1, www.example.com",
        "http://example.org:8080, example.org",
        "sub-1.example.com, sub-1.example.com",
        "203.0.113.5, 203.0.113.5",
        "198.51.100.77/24, 198.51.100.0/24",
        "2001:DB8::1, 2001:db8::1",
        "2001:db8:0:0:0:0:0:1, 2001:db8::1",
        "2001:db8:1:2:3:4:5:6/32, 2001:db8::/32",
        "2001:db8:0:1:1:1:1:1, 2001:db8:0:1:1:1:1:1",
        "192.0.2.1/32, 192.0.2.1/32",
    )
    fun `site pattern is normalized like on the desktop`(text: String, expected: String) {
        assertEquals(expected, SitePattern.normalize(text))
    }

    @ParameterizedTest
    @ValueSource(
        strings = [
            "", "   ", "localhost", "example", "-example.com", "example-.com", "exa mple.com", "example..com", "example.com.", "*.", "*",
            "ex_ample.com", "пример.рф", "192.168.1", "1.2", "256.1.1.1", "010.0.0.1", "198.51.100.0/0", "198.51.100.0/33",
            "2001:db8::/129", "198.51.100.0/x", "fe80::1%wlan0", "example.123", "geosite:google", "GEOSITE:category-ads-all",
            "https://", "geoip:ru",
        ],
    )
    fun `site pattern refuses what the phone core cannot match`(text: String) {
        assertNull(SitePattern.normalize(text), text)
    }

    @Test
    fun `too long name is refused and addresses are told apart from names`() {
        val label = "a".repeat(63)
        val long = listOf(label, label, label, label.take(61)).joinToString(".")
        assertEquals(253, long.length)
        assertEquals(long, SitePattern.normalize(long))
        assertNull(SitePattern.normalize("$long.x"), "длиннее 253 знаков")
        assertNull(SitePattern.normalize("${"a".repeat(64)}.com"), "метка длиннее 63")

        assertTrue(SitePattern.isAddress("203.0.113.0/24"))
        assertTrue(SitePattern.isAddress("2001:db8::1"))
        assertFalse(SitePattern.isAddress("example.com"))
        assertFalse(SitePattern.isAddress("*.local"))
        assertEquals("domain:example.com", SitePattern.domainCondition("example.com"))
        assertEquals("domain:local", SitePattern.domainCondition("*.local"))
    }

    @Test
    fun `apps and direct rules act only in the split mode`() {
        val apps = listOf(BypassApp("org.example.game", "Игра"), BypassApp("org.example.call", "Звонки", enabled = false), BypassApp("org.example.game", "Игра"))
        val rules = listOf(SiteRule("example.com", RuleAction.Direct), SiteRule("ads.example.org", RuleAction.Block), SiteRule("example.org", RuleAction.Proxy))
        val all = RoutingSettings(RoutingMode.AllTraffic, apps, rules)
        assertFalse(all.split)
        assertEquals(emptyList(), all.disallowedApps)
        assertEquals(rules.drop(1), all.coreRules, "«Напрямую» вне раздельного режима не действует, остальное — по порядку")
        assertTrue(all.hasIdleRules)

        val split = all.copy(mode = RoutingMode.Split)
        assertEquals(listOf("org.example.game"), split.disallowedApps, "только включенные, без повторов")
        assertEquals(rules, split.coreRules)
        assertFalse(split.hasIdleRules)
        assertFalse(RoutingSettings(rules = rules.drop(1)).hasIdleRules, "без «Напрямую» молчащих правил нет")
        assertEquals(RoutingSettings(), RoutingSettings(RoutingMode.AllTraffic, emptyList(), emptyList()), "по умолчанию весь трафик через VPN")
    }

    @Test
    fun `rule for the same site replaces the old one and an edited rule keeps its place`() {
        val first = SiteRule("example.com", RuleAction.Direct)
        val second = SiteRule("ads.example.org", RuleAction.Block)
        var routing = RoutingSettings().saveRule(null, first).saveRule(null, second)
        assertEquals(listOf(first, second), routing.rules, "новое правило — в конец")

        routing = routing.saveRule(null, SiteRule("example.com", RuleAction.Block))
        assertEquals(listOf(second, SiteRule("example.com", RuleAction.Block)), routing.rules, "правило для того же сайта заменяется")

        val edited = SiteRule("ads.example.net", RuleAction.Proxy)
        routing = routing.saveRule(second, edited)
        assertEquals(listOf(edited, SiteRule("example.com", RuleAction.Block)), routing.rules, "измененное — на своем месте")

        // Измененное правило получило шаблон другого — то уходит, а измененное остается на своем месте.
        routing = routing.saveRule(edited, SiteRule("example.com", RuleAction.Direct))
        assertEquals(listOf(SiteRule("example.com", RuleAction.Direct)), routing.rules)
        assertEquals(emptyList(), routing.removeRule(SiteRule("example.com", RuleAction.Direct)).rules)
    }

    @Test
    fun `app already in the list is turned on instead of a second row`() {
        var routing = RoutingSettings().addApp("org.example.game", "Игра").addApp("org.example.bank", "Банк")
        assertEquals(listOf(BypassApp("org.example.game", "Игра"), BypassApp("org.example.bank", "Банк")), routing.apps)

        routing = routing.setApp(routing.apps[0], routing.apps[0].copy(enabled = false))
        assertFalse(routing.apps[0].enabled)
        routing = routing.addApp("org.example.game", "Игра 2")
        assertEquals(listOf(BypassApp("org.example.game", "Игра"), BypassApp("org.example.bank", "Банк")), routing.apps, "включена, строка одна")

        routing = routing.setApp(routing.apps[1], null)
        assertEquals(listOf(BypassApp("org.example.game", "Игра")), routing.apps, "убрана из списка")
    }

    @Test
    fun `routing survives the store as json`() {
        val routing = RoutingSettings(
            RoutingMode.Split,
            listOf(BypassApp("org.example.game", "Игра «Тест» \"1\""), BypassApp("org.example.call", "Звонки", enabled = false)),
            listOf(SiteRule("example.com", RuleAction.Direct), SiteRule("203.0.113.0/24", RuleAction.Block), SiteRule("*.local", RuleAction.Proxy)),
        )
        assertEquals(routing, RoutingSettings.read(routing.mode.name, routing.appsJson(), routing.rulesJson()))
        assertEquals(RoutingSettings(), RoutingSettings.read(null, null, null))
    }

    @Test
    fun `broken store text is skipped instead of a crash or a rule the core refuses`() {
        assertEquals(RoutingSettings(), RoutingSettings.read("Everything", "not json", "{"))
        assertEquals(RoutingSettings(), RoutingSettings.read(null, """{"package":"org.example.game"}""", "\"example.com\""))

        val read = RoutingSettings.read(
            "Split",
            """[{"package":"org.example.game","name":"Игра"},{"package":"bad package","name":"X"},{"name":"Без пакета"},{"package":"org.example.call","name":" ","enabled":false}]""",
            """[{"pattern":"Example.com","action":"Direct"},{"pattern":"geosite:google","action":"Block"},{"pattern":"example.org","action":"Bypass"},{"action":"Block"}]""",
        )
        assertEquals(listOf(BypassApp("org.example.game", "Игра"), BypassApp("org.example.call", "org.example.call", enabled = false)), read.apps)
        assertEquals(listOf(SiteRule("example.com", RuleAction.Direct)), read.rules, "geosite и неизвестное действие пропущены")
    }
}

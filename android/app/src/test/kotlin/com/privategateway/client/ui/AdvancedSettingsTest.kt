package com.privategateway.client.ui

import android.content.Context
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.assertIsNotSelected
import androidx.compose.ui.test.assertIsOff
import androidx.compose.ui.test.assertIsOn
import androidx.compose.ui.test.assertIsSelected
import androidx.compose.ui.test.assertTextEquals
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performScrollTo
import androidx.compose.ui.test.performTextClearance
import androidx.compose.ui.test.performTextInput
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.privategateway.client.R
import com.privategateway.client.core.settings.BypassApp
import com.privategateway.client.core.settings.ConnectionSettings
import com.privategateway.client.core.settings.CoreLogLevel
import com.privategateway.client.core.settings.RoutingMode
import com.privategateway.client.core.settings.RoutingSettings
import com.privategateway.client.core.settings.RuleAction
import com.privategateway.client.core.settings.SiteRule
import com.privategateway.client.core.settings.TlsFingerprint
import com.privategateway.client.core.settings.TunnelSettings
import com.privategateway.client.core.vpn.ReconnectPolicy
import com.privategateway.client.data.PhoneApp
import com.privategateway.client.ui.settings.Behavior
import com.privategateway.client.ui.settings.CoreScreen
import com.privategateway.client.ui.settings.RoutingScreen
import com.privategateway.client.ui.settings.SettingsScreen
import com.privategateway.client.ui.settings.withoutGeosite
import com.privategateway.client.ui.theme.LocalPgToast
import com.privategateway.client.ui.theme.PgTheme
import com.privategateway.client.ui.theme.PgToast
import com.privategateway.client.ui.theme.PgToastState
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.annotation.Config
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/**
 * «Подключение и ядро» (доска 6) и «Маршрутизация» (доска 13) без устройства: что показано, что выбирается и сохраняется в
 * настройки туннеля (а оттуда — в конфиг ядра и карту VPN), чего у телефона нет, листы «Свой MTU» и правила, выбор программ
 * мимо VPN и «Локальная сеть» → «Защита и утечки».
 */
@RunWith(AndroidJUnit4::class)
@Config(qualifiers = "w384dp-h832dp")
class AdvancedSettingsTest {
    @get:Rule
    val compose = createComposeRule()

    private val context = ApplicationProvider.getApplicationContext<Context>()
    private val toast = PgToastState()

    private fun text(id: Int, vararg args: Any) = context.getString(id, *args)

    /** Экран с подсказками окна сверху — как в AppScreen. */
    @Composable
    private fun WithToast(content: @Composable () -> Unit) {
        PgTheme {
            CompositionLocalProvider(LocalPgToast provides toast) {
                Box(Modifier.fillMaxSize()) {
                    content()
                    PgToast(toast, Modifier.align(Alignment.TopCenter))
                }
            }
        }
    }

    @Test
    fun `core page shows the phone core and only what the phone does`() {
        var core by mutableStateOf<String?>(null)
        var loads = 0
        compose.setContent { PgTheme { CoreScreen(core, { loads++ }, TunnelSettings(), {}, Behavior(), {}) {} } }
        compose.waitForIdle()

        assertEquals(1, loads, "версия ядра читается при входе на страницу")
        compose.onNodeWithText(text(R.string.core_group_core).uppercase()).assertIsDisplayed()
        compose.onNodeWithText(text(R.string.core_xray_text)).assertIsDisplayed()
        // Версия еще не прочитана — «—», как в подписи «Настроек»; прочитана — она сама.
        compose.onNodeWithTag("core.xray.version").assertTextEquals("—")
        core = "26.10.21"
        compose.onNodeWithTag("core.xray.version").assertTextEquals("26.10.21")
        // Hysteria2 и TUIC телефон не подключает: второго ядра у него нет, подписи десктопа про sing-box — тоже.
        compose.onNodeWithText(text(R.string.core_second_title)).assertIsDisplayed()
        compose.onNodeWithTag("core.second.version").assertTextEquals(text(R.string.core_version_none))
        compose.onNodeWithText(text(R.string.core_second_text)).assertDoesNotExist()
        val second = text(R.string.android_card_core_none)
        compose.onNodeWithText(second.substring(0, 1).uppercase() + second.substring(1)).assertIsDisplayed()

        // По умолчанию — как у десктопа.
        compose.onNodeWithTag("core.log").assertTextEquals(text(R.string.core_log_warning))
        compose.onNodeWithTag("core.mtu").performScrollTo().assertTextEquals(text(R.string.core_mtu_auto))
        compose.onNodeWithTag("core.strict").performScrollTo().assertIsOn()
        compose.onNodeWithTag("core.sniff").performScrollTo().assertIsOn()
        compose.onNodeWithTag("core.fp").performScrollTo().assertTextEquals(text(R.string.core_fp_profile))
        compose.onNodeWithTag("core.mux").performScrollTo().assertIsOff()
        compose.onNodeWithTag("core.frag").performScrollTo().assertIsOff()
        compose.onNodeWithTag("core.timeout").performScrollTo().assertTextEquals(text(R.string.core_timeout_value, "10"))
        compose.onNodeWithTag("reconnect").performScrollTo().assertTextEquals(text(R.string.core_reconnect_backoff))

        // Стека TUN и локального прокси у телефона нет — ни работающих, ни выключенных строк.
        compose.onNodeWithText(text(R.string.core_stack_title)).assertDoesNotExist()
        compose.onNodeWithText(text(R.string.core_group_proxy).uppercase()).assertDoesNotExist()
        compose.onNodeWithText(text(R.string.core_port_title)).assertDoesNotExist()
        compose.onNodeWithText(text(R.string.core_systemProxy_title)).assertDoesNotExist()
        compose.onNodeWithText(text(R.string.core_lanProxy_title)).assertDoesNotExist()
    }

    @Test
    fun `core settings change by menus and switches`() {
        var tunnel by mutableStateOf(TunnelSettings())
        var behavior by mutableStateOf(Behavior())
        compose.setContent { PgTheme { CoreScreen("26.10.21", {}, tunnel, { tunnel = it }, behavior, { behavior = it }) {} } }

        compose.onNodeWithTag("core.log").performClick()
        compose.onNodeWithTag("core.log.Warning").assertIsSelected()
        compose.onNodeWithTag("core.log.Info").performClick()
        compose.onNodeWithTag("core.log.menu").assertDoesNotExist()
        compose.onNodeWithTag("core.log").assertTextEquals(text(R.string.core_log_info))

        compose.onNodeWithTag("core.strict").performScrollTo().performClick()
        compose.onNodeWithTag("core.sniff").performScrollTo().performClick()
        compose.onNodeWithTag("core.fp").performScrollTo().performClick()
        compose.onNodeWithTag("core.fp.Profile").assertIsSelected()
        compose.onNodeWithTag("core.fp.Chrome").performClick()
        compose.onNodeWithTag("core.mux").performScrollTo().performClick()
        compose.onNodeWithTag("core.frag").performScrollTo().performClick()
        compose.onNodeWithTag("core.timeout").performScrollTo().performClick()
        compose.onNodeWithTag("core.timeout.10").assertIsSelected()
        compose.onNodeWithTag("core.timeout.30").performClick()
        compose.onNodeWithTag("reconnect").performScrollTo().performClick()
        compose.onNodeWithTag("reconnect.UpTo5Minutes").performClick()

        val expected = ConnectionSettings(
            CoreLogLevel.Info,
            null,
            strictRoutes = false,
            sniffing = false,
            fingerprint = TlsFingerprint.Chrome,
            mux = true,
            tlsFragment = true,
            connectTimeoutSeconds = 30,
        )
        assertEquals(expected, tunnel.connection)
        assertEquals(TunnelSettings(connection = expected), tunnel, "остальные настройки туннеля не тронуты")
        assertEquals(ReconnectPolicy.UpTo5Minutes, behavior.reconnect, "переподключение — в поведении, не в туннеле")

        // Выбранное — в ядре и карте туннеля.
        val core = tunnel.core()
        assertEquals(CoreLogLevel.Info, core.logLevel)
        assertEquals("chrome", core.fingerprint)
        assertEquals(listOf(false, true, true), listOf(core.sniffing, core.mux, core.fragment))
        assertEquals(true, tunnel.plan().allowBypass)
        assertEquals(30_000L, tunnel.connection.probeTimeoutMs)

        compose.onNodeWithTag("core.fp").performScrollTo().assertTextEquals("chrome")
        compose.onNodeWithTag("core.timeout").performScrollTo().assertTextEquals(text(R.string.core_timeout_value, "30"))
        compose.onNodeWithTag("core.strict").performScrollTo().assertIsOff()
        compose.onNodeWithTag("core.sniff").performScrollTo().assertIsOff()
        compose.onNodeWithTag("core.mux").performScrollTo().assertIsOn()
        compose.onNodeWithTag("core.frag").performScrollTo().assertIsOn()
        compose.onNodeWithTag("reconnect").performScrollTo().assertTextEquals(text(R.string.core_reconnect_5min))
    }

    @Test
    fun `mtu is chosen from the menu or saved from 1280 to 1500 on the sheet`() {
        var tunnel by mutableStateOf(TunnelSettings())
        compose.setContent { PgTheme { CoreScreen("26.10.21", {}, tunnel, { tunnel = it }, Behavior(), {}) {} } }

        compose.onNodeWithTag("core.mtu").performScrollTo().performClick()
        compose.onNodeWithTag("core.mtu.auto").assertIsSelected()
        compose.onNodeWithTag("core.mtu.custom").assertIsNotSelected()
        compose.onNodeWithTag("core.mtu.1420").performClick()
        assertEquals(1420, tunnel.connection.mtu)
        compose.onNodeWithTag("core.mtu").performScrollTo().assertTextEquals("1420")

        // «Свое…» открывает лист и само выбор не меняет.
        compose.onNodeWithTag("core.mtu").performClick()
        compose.onNodeWithTag("core.mtu.custom").performClick()
        compose.onNodeWithTag("core.mtu.sheet").assertIsDisplayed()
        compose.onNodeWithText(text(R.string.core_mtuCustom_title)).assertIsDisplayed()
        compose.onNodeWithText(text(R.string.core_mtuCustom_note)).assertIsDisplayed()
        assertEquals(1420, tunnel.connection.mtu)

        // Число вне 1280–1500 не сохраняется: лист говорит, какое нужно.
        compose.onNodeWithTag("core.mtu.text").performTextClearance()
        compose.onNodeWithTag("core.mtu.text").performTextInput("9000")
        compose.onNodeWithTag("core.mtu.save").performClick()
        compose.onNodeWithTag("core.mtu.bad", useUnmergedTree = true).assertTextEquals(text(R.string.core_mtuCustom_bad))
        assertEquals(1420, tunnel.connection.mtu)
        compose.onNodeWithTag("core.mtu.sheet").assertIsDisplayed()
        compose.onNodeWithTag("core.mtu.cancel").performClick()
        compose.onNodeWithTag("core.mtu.sheet").assertDoesNotExist()
        assertEquals(1420, tunnel.connection.mtu, "«Отмена» ничего не меняет")

        compose.onNodeWithTag("core.mtu").performScrollTo().performClick()
        compose.onNodeWithTag("core.mtu.custom").performClick()
        compose.onNodeWithTag("core.mtu.text").performTextClearance()
        compose.onNodeWithTag("core.mtu.text").performTextInput("1300")
        compose.onNodeWithTag("core.mtu.save").performClick()
        compose.onNodeWithTag("core.mtu.sheet").assertDoesNotExist()
        assertEquals(1300, tunnel.connection.mtu)
        assertEquals(1300, tunnel.core().mtu, "свое число — во входе TUN ядра")
        assertEquals(1300, tunnel.plan().mtu, "и в карте VPN")

        // Свое число выбрано — в меню отмечено «Свое…».
        compose.onNodeWithTag("core.mtu").performScrollTo().assertTextEquals("1300").performClick()
        compose.onNodeWithTag("core.mtu.custom").assertIsSelected()
        compose.onNodeWithTag("core.mtu.1420").assertIsNotSelected()
        compose.onNodeWithTag("core.mtu.auto").performClick()
        assertEquals(null, tunnel.connection.mtu)
    }

    @Test
    fun `routing mode switches without a question and split shows its blocks`() {
        var tunnel by mutableStateOf(TunnelSettings())
        compose.setContent { WithToast { RoutingScreen(tunnel, { tunnel = it }, {}, {}, { emptyList() }) } }

        // По умолчанию весь трафик через VPN: ни предупреждения, ни программ мимо VPN.
        compose.onNodeWithTag("routing.mode.AllTraffic").assertIsSelected()
        compose.onNodeWithTag("routing.mode.note").assertTextEquals(text(R.string.routing_mode_all_note, "Android"))
        compose.onNodeWithTag("routing.warning").assertDoesNotExist()
        compose.onNodeWithText(text(R.string.routing_apps_title).uppercase()).assertDoesNotExist()
        compose.onNodeWithTag("routing.apps.add").assertDoesNotExist()
        compose.onNodeWithTag("routing.rules.empty").performScrollTo().assertTextEquals(text(R.string.routing_rules_empty))

        compose.onNodeWithTag("routing.mode.Split").performClick()
        assertEquals(RoutingMode.Split, tunnel.routing.mode)
        compose.onNodeWithTag("routing.mode.Split").assertIsSelected()
        compose.onNodeWithTag("routing.mode.note").assertTextEquals(text(R.string.routing_mode_split_note))
        compose.onNodeWithTag("routing.warning").assertIsDisplayed()
        compose.onNodeWithText(text(R.string.routing_warning)).assertIsDisplayed()
        compose.onNodeWithText(text(R.string.routing_apps_title).uppercase()).performScrollTo().assertIsDisplayed()
        compose.onNodeWithTag("routing.apps.empty").performScrollTo().assertTextEquals(text(R.string.routing_apps_empty))
        compose.onNodeWithTag("routing.apps.add").performScrollTo().assertIsDisplayed()

        compose.onNodeWithTag("routing.mode.AllTraffic").performScrollTo().performClick()
        assertEquals(TunnelSettings(), tunnel)
        compose.onNodeWithTag("routing.warning").assertDoesNotExist()
    }

    @Test
    fun `site rules are added edited replaced and deleted on the sheet`() {
        var tunnel by mutableStateOf(TunnelSettings())
        compose.setContent { WithToast { RoutingScreen(tunnel, { tunnel = it }, {}, {}, { emptyList() }) } }

        // Новое правило в режиме «Весь трафик» — «Блокировать»: «Напрямую» там не действует.
        compose.onNodeWithTag("routing.rules.add").performScrollTo().performClick()
        compose.onNodeWithTag("routing.rule.sheet").assertIsDisplayed()
        compose.onNodeWithText(text(R.string.routing_rule_new)).assertIsDisplayed()
        compose.onNodeWithTag("routing.rule.action.Block").assertIsSelected()
        compose.onNodeWithTag("routing.rule.idle", useUnmergedTree = true).assertDoesNotExist()
        compose.onNodeWithTag("routing.rule.save").assertTextEquals(text(R.string.routing_rule_add))
        compose.onNodeWithTag("routing.rule.cancel").assertTextEquals(text(R.string.dialog_cancel))

        // Базы geosite на телефоне нет — подсказка его не предлагает, такой шаблон не сохраняется, как и не домен.
        compose.onNodeWithTag("routing.rule.hint", useUnmergedTree = true).assertTextEquals(withoutGeosite(text(R.string.routing_rule_hint)))
        compose.onNodeWithTag("routing.rule.pattern").performTextInput("geosite:google")
        compose.onNodeWithTag("routing.rule.save").performClick()
        compose.onNodeWithTag("routing.rule.bad", useUnmergedTree = true).assertTextEquals(withoutGeosite(text(R.string.routing_rule_bad)))
        compose.onNodeWithTag("routing.rule.hint", useUnmergedTree = true).assertDoesNotExist()
        assertEquals(RoutingSettings(), tunnel.routing)

        // Адрес с «https://» и путем сокращается до домена, строчными.
        compose.onNodeWithTag("routing.rule.pattern").performTextClearance()
        compose.onNodeWithTag("routing.rule.pattern").performTextInput("https://Ads.Example.org/banner")
        compose.onNodeWithTag("routing.rule.bad", useUnmergedTree = true).assertDoesNotExist()
        compose.onNodeWithTag("routing.rule.save").performClick()
        compose.onNodeWithTag("routing.rule.sheet").assertDoesNotExist()
        assertEquals(listOf(SiteRule("ads.example.org", RuleAction.Block)), tunnel.routing.rules)
        assertEquals(tunnel.routing.rules, tunnel.core().rules, "блок действует и в режиме «Весь трафик»")
        compose.onNodeWithTag("routing.rule.ads.example.org.action", useUnmergedTree = true).assertTextEquals(text(R.string.routing_action_block))

        // «Напрямую» в режиме «Весь трафик» — подпись в листе и над списком: правило пока не действует и в ядро не идет.
        compose.onNodeWithTag("routing.rules.add").performScrollTo().performClick()
        compose.onNodeWithTag("routing.rule.pattern").performTextInput("example.com")
        compose.onNodeWithTag("routing.rule.action.Direct").performClick()
        compose.onNodeWithTag("routing.rule.idle", useUnmergedTree = true).assertTextEquals(text(R.string.routing_rules_idle))
        compose.onNodeWithTag("routing.rule.save").performClick()
        compose.onNodeWithTag("routing.rules.idle").performScrollTo().assertIsDisplayed()
        assertEquals(listOf(SiteRule("ads.example.org", RuleAction.Block)), tunnel.core().rules)

        // Открытое правило — «Правило для сайта» с «Удалить» и «Сохранить»; измененное остается на своем месте.
        compose.onNodeWithTag("routing.rule.ads.example.org").performScrollTo().performClick()
        compose.onNodeWithText(text(R.string.routing_rule_edit)).assertIsDisplayed()
        compose.onNodeWithTag("routing.rule.delete").assertTextEquals(text(R.string.routing_rule_delete))
        compose.onNodeWithTag("routing.rule.save").assertTextEquals(text(R.string.routing_rule_save))
        compose.onNodeWithTag("routing.rule.action.Block").assertIsSelected()
        compose.onNodeWithTag("routing.rule.action.Proxy").performClick()
        compose.onNodeWithTag("routing.rule.save").performClick()
        assertEquals(listOf(SiteRule("ads.example.org", RuleAction.Proxy), SiteRule("example.com", RuleAction.Direct)), tunnel.routing.rules)

        // Правило для того же сайта заменяет прежнее — два правила на один сайт противоречили бы друг другу.
        compose.onNodeWithTag("routing.rules.add").performScrollTo().performClick()
        compose.onNodeWithTag("routing.rule.pattern").performTextInput("EXAMPLE.com")
        compose.onNodeWithTag("routing.rule.save").performClick()
        assertEquals(listOf(SiteRule("ads.example.org", RuleAction.Proxy), SiteRule("example.com", RuleAction.Block)), tunnel.routing.rules)
        compose.onNodeWithTag("routing.rules.idle").assertDoesNotExist()

        // «Удалить» убирает правило.
        compose.onNodeWithTag("routing.rule.example.com").performScrollTo().performClick()
        compose.onNodeWithTag("routing.rule.delete").performClick()
        compose.onNodeWithTag("routing.rule.sheet").assertDoesNotExist()
        assertEquals(listOf(SiteRule("ads.example.org", RuleAction.Proxy)), tunnel.routing.rules)
    }

    @Test
    fun `rule hint and error do not offer geosite the phone has no base for`() {
        assertEquals(
            "example.com — сайт и его поддомены, *.local — все имена в зоне, 203.0.113.0/24 — адреса подсети.",
            withoutGeosite("example.com — сайт и его поддомены, *.local — все имена в зоне, geosite:категория — готовый список, 203.0.113.0/24 — адреса подсети."),
        )
        assertEquals("Нужен домен, *.домен, адрес или подсеть", withoutGeosite("Нужен домен, *.домен, geosite:категория, адрес или подсеть"))
        // Тексты приложения на языке теста: geosite нет, остальные пункты на месте, пустых пунктов нет.
        for (id in listOf(R.string.routing_rule_hint, R.string.routing_rule_bad)) {
            val shown = withoutGeosite(text(id))
            assertFalse(shown.contains("geosite"), shown)
            assertFalse(shown.contains(", ,"), shown)
            assertTrue(shown.length < text(id).length, "пункт geosite убран: $shown")
        }

        assertTrue(withoutGeosite(text(R.string.routing_rule_hint)).contains("203.0.113.0/24"))
    }

    @Test
    fun `new rule in the split mode is direct and goes to the core`() {
        var tunnel by mutableStateOf(TunnelSettings(routing = RoutingSettings(RoutingMode.Split)))
        compose.setContent { WithToast { RoutingScreen(tunnel, { tunnel = it }, {}, {}, { emptyList() }) } }

        compose.onNodeWithTag("routing.rules.add").performScrollTo().performClick()
        compose.onNodeWithTag("routing.rule.action.Direct").assertIsSelected()
        compose.onNodeWithTag("routing.rule.idle", useUnmergedTree = true).assertDoesNotExist()
        compose.onNodeWithTag("routing.rule.pattern").performTextInput("198.51.100.77/24")
        compose.onNodeWithTag("routing.rule.save").performClick()
        assertEquals(listOf(SiteRule("198.51.100.0/24", RuleAction.Direct)), tunnel.core().rules, "подсеть — к адресу сети, «Напрямую» действует")
        compose.onNodeWithTag("routing.rules.idle").assertDoesNotExist()
    }

    @Test
    fun `apps are picked from the phone toggled and removed`() {
        var tunnel by mutableStateOf(TunnelSettings(routing = RoutingSettings(RoutingMode.Split)))
        val phone = listOf(PhoneApp("org.example.bank", "Банк"), PhoneApp("org.example.game", "Игра"))
        var loads = 0
        compose.setContent {
            WithToast {
                RoutingScreen(tunnel, { tunnel = it }, {}, {}, {
                    loads++
                    phone
                })
            }
        }

        compose.onNodeWithTag("routing.apps.add").performScrollTo().performClick()
        compose.onNodeWithTag("routing.apps.sheet").assertIsDisplayed()
        assertEquals(1, loads, "список программ читается, когда лист открыт")
        compose.onNodeWithTag("routing.apps.pick.org.example.bank").assertIsDisplayed()
        compose.onNodeWithTag("routing.apps.pick.org.example.game").assertIsDisplayed().performClick()
        compose.onNodeWithTag("routing.apps.sheet").assertDoesNotExist()
        assertEquals(listOf(BypassApp("org.example.game", "Игра")), tunnel.routing.apps)
        assertEquals(listOf("org.example.game"), tunnel.plan().disallowedApps, "программа уходит мимо карты туннеля")
        compose.onNodeWithTag("routing.app.org.example.game").performScrollTo().assertIsOn()
        compose.onNodeWithText("org.example.game", useUnmergedTree = true).assertIsDisplayed()

        // Выключить — программа остается в списке, но идет через VPN.
        compose.onNodeWithTag("routing.app.org.example.game").performClick()
        assertEquals(listOf(BypassApp("org.example.game", "Игра", enabled = false)), tunnel.routing.apps)
        assertEquals(emptyList(), tunnel.plan().disallowedApps)
        compose.onNodeWithTag("routing.app.org.example.game").assertIsOff()

        // Та же программа снова — включается, а не заводится второй строкой; подсказка говорит об этом.
        compose.onNodeWithTag("routing.apps.add").performScrollTo().performClick()
        compose.onNodeWithTag("routing.apps.pick.org.example.game").performClick()
        assertEquals(listOf(BypassApp("org.example.game", "Игра")), tunnel.routing.apps)
        compose.onNodeWithTag("toast").assertTextEquals(text(R.string.routing_apps_exists, "Игра"))

        // Корзина убирает программу из списка.
        compose.onNodeWithTag("routing.app.org.example.game.remove").performScrollTo().performClick()
        assertEquals(emptyList(), tunnel.routing.apps)
        compose.onNodeWithTag("routing.apps.empty").performScrollTo().assertIsDisplayed()

        // В режиме «Весь трафик» список сохраняется, но мимо VPN не идет ни одна программа.
        tunnel = tunnel.copy(routing = RoutingSettings(RoutingMode.AllTraffic, listOf(BypassApp("org.example.game", "Игра"))))
        compose.onNodeWithTag("routing.app.org.example.game").assertDoesNotExist()
        assertEquals(emptyList(), tunnel.plan().disallowedApps)
    }

    @Test
    fun `local network row shows the protection setting and leads there`() {
        var tunnel by mutableStateOf(TunnelSettings())
        compose.setContent { WithToast { SettingsScreen("26.10.21", {}, {}, tunnel, { tunnel = it }) } }

        compose.onNodeWithTag("settings.routing").performScrollTo().performClick()
        compose.onNodeWithText(text(R.string.routing_always_title).uppercase()).performScrollTo().assertIsDisplayed()
        compose.onNodeWithText(text(R.string.routing_server_text)).performScrollTo().assertIsDisplayed()
        compose.onNodeWithTag("routing.lan.state", useUnmergedTree = true).assertTextEquals(text(R.string.routing_lan_denied))
        tunnel = TunnelSettings(allowLan = true)
        compose.onNodeWithTag("routing.lan.state", useUnmergedTree = true).assertTextEquals(text(R.string.routing_lan_allowed))

        // Локальная сеть настраивается на одном экране — «Защита и утечки».
        compose.onNodeWithTag("routing.lan").performScrollTo().performClick()
        compose.onNodeWithTag("protection").assertIsDisplayed()
        compose.onNodeWithTag("lan").performScrollTo().assertIsOn()
    }
}

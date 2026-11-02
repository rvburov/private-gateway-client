package com.privategateway.client.ui

import android.content.Context
import androidx.compose.foundation.clickable
import androidx.compose.material3.Text
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.assertIsEnabled
import androidx.compose.ui.test.assertIsNotEnabled
import androidx.compose.ui.test.assertIsNotSelected
import androidx.compose.ui.test.assertIsOff
import androidx.compose.ui.test.assertIsOn
import androidx.compose.ui.test.assertIsSelected
import androidx.compose.ui.test.assertTextEquals
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performScrollTo
import androidx.compose.ui.test.performTextClearance
import androidx.compose.ui.test.performTextInput
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.privategateway.client.BuildConfig
import com.privategateway.client.R
import com.privategateway.client.core.notify.NotificationLevel
import com.privategateway.client.core.settings.DnsChoice
import com.privategateway.client.core.settings.TunnelSettings
import com.privategateway.client.core.vpn.ReconnectPolicy
import com.privategateway.client.core.vpn.SystemVpn
import com.privategateway.client.core.vpn.SystemVpnSource
import com.privategateway.client.core.vpn.SystemVpnView
import com.privategateway.client.ui.settings.Behavior
import com.privategateway.client.ui.settings.ProtectionScreen
import com.privategateway.client.ui.settings.SettingsPage
import com.privategateway.client.ui.settings.SettingsScreen
import com.privategateway.client.ui.theme.PgTheme
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.annotation.Config
import kotlin.test.assertEquals

/**
 * «Настройки» (доска 4) и «Защита и утечки» (доска 5) без устройства: что выбрано, что сохраняется, куда ведут строки, неверный
 * DNS не проходит, что сообщил Android о блоке без VPN.
 */
@RunWith(AndroidJUnit4::class)
@Config(qualifiers = "w384dp-h832dp")
class SettingsScreensTest {
    @get:Rule
    val compose = createComposeRule()

    private val context = ApplicationProvider.getApplicationContext<Context>()

    private fun text(id: Int) = context.getString(id)

    @Test
    fun `settings open protection and come back`() {
        var tunnel by mutableStateOf(TunnelSettings())
        compose.setContent { PgTheme { SettingsScreen("26.10.21", {}, {}, tunnel, onTunnel = { tunnel = it }) } }
        compose.onNodeWithTag("settings.protection").performScrollTo().performClick()
        compose.onNodeWithTag("protection").assertIsDisplayed()
        compose.onNodeWithText(text(R.string.prot_dns_title)).performScrollTo().assertIsDisplayed()
        compose.onNodeWithContentDescription(text(R.string.common_back)).performClick()
        compose.onNodeWithTag("settings.protection").performScrollTo().assertIsDisplayed()
    }

    @Test
    fun `advanced rows open existing pages and the check tab`() {
        var checks = 0
        compose.setContent {
            PgTheme {
                SettingsScreen("26.10.21", {}, {}, TunnelSettings(), {}, onCheck = { checks++ }) { back ->
                    Text("subscriptions", Modifier.testTag("subscriptions.page").clickable(onClick = back))
                }
            }
        }

        compose.onNodeWithText(text(R.string.settings_section_advanced).uppercase()).performScrollTo().assertIsDisplayed()
        compose.onNodeWithText(text(R.string.settings_adv_check_text)).performScrollTo().assertIsDisplayed()
        // «Проверка и логи» — вкладка островка, а не страница настроек.
        compose.onNodeWithTag("settings.check").performScrollTo().performClick()
        assertEquals(1, checks)
        compose.onNodeWithTag("settings").assertIsDisplayed()

        compose.onNodeWithTag("settings.profiles").performScrollTo().performClick()
        compose.onNodeWithTag("subscriptions.page").assertIsDisplayed().performClick()
        compose.onNodeWithTag("settings.licenses").performScrollTo().performClick()
        compose.onNodeWithTag("licenses").assertIsDisplayed()

        // Страниц «Подключение и ядро», «Маршрутизация», «Резервная копия» и «О программе» у телефона пока нет — и строк тоже.
        compose.onNodeWithContentDescription(text(R.string.common_back)).performClick()
        compose.onNodeWithText(text(R.string.settings_adv_core_title)).assertDoesNotExist()
        compose.onNodeWithText(text(R.string.settings_adv_routing_title)).assertDoesNotExist()
        compose.onNodeWithText(text(R.string.settings_adv_backup_title)).assertDoesNotExist()
        compose.onNodeWithText(text(R.string.settings_adv_about_title)).assertDoesNotExist()
    }

    @Test
    fun `kill switch row shows what android said and opens vpn settings`() {
        var system by mutableStateOf(SystemVpnView(SystemVpn(), SystemVpnSource.Unknown))
        var opened = 0
        compose.setContent { PgTheme { SettingsScreen("26.10.21", {}, { opened++ }, TunnelSettings(), {}, system = system) } }
        compose.onNodeWithText(text(R.string.settings_section_basic).uppercase()).assertIsDisplayed()
        compose.onNodeWithText(text(R.string.settings_ks_title)).assertIsDisplayed()
        // Значения — внутри нажимаемой строки: в общем дереве их текст слит в строку.
        compose.onNodeWithTag("settings.vpn.lockdown", useUnmergedTree = true).assertTextEquals(text(R.string.android_settings_vpn_unknown))
        compose.onNodeWithTag("settings.vpn.source", useUnmergedTree = true).assertTextEquals(text(R.string.android_settings_vpn_never))

        system = SystemVpnView(SystemVpn(alwaysOn = true, lockdown = false), SystemVpnSource.Now)
        compose.onNodeWithTag("settings.vpn.lockdown", useUnmergedTree = true).assertTextEquals(text(R.string.android_settings_vpn_off))
        compose.onNodeWithTag("settings.vpn.source", useUnmergedTree = true).assertTextEquals(text(R.string.android_settings_vpn_now))

        system = SystemVpnView(SystemVpn(alwaysOn = true, lockdown = true), SystemVpnSource.LastConnection)
        compose.onNodeWithTag("settings.vpn.lockdown", useUnmergedTree = true).assertTextEquals(text(R.string.android_settings_vpn_on))
        compose.onNodeWithTag("settings.vpn.source", useUnmergedTree = true).assertTextEquals(text(R.string.android_settings_vpn_last))

        // Переключателя нет: строка ведет в настройки VPN Android.
        compose.onNodeWithTag("settings.ks").performScrollTo().performClick()
        assertEquals(1, opened)
    }

    @Test
    fun `basic settings are chosen on the main page`() {
        var behavior by mutableStateOf(Behavior())
        compose.setContent { PgTheme { SettingsScreen("26.10.21", {}, {}, TunnelSettings(), {}, behavior, { behavior = it }) } }

        // По умолчанию — как у десктопа: «Только важные», «Экспоненц. до 60 с», без подключения при запуске.
        compose.onNodeWithTag("notify.ImportantOnly").performScrollTo().assertIsSelected()
        compose.onNodeWithTag("notify.note").assertTextEquals(text(R.string.settings_notifications_important_note))
        compose.onNodeWithTag("reconnect").performScrollTo().assertTextEquals(text(R.string.core_reconnect_backoff))
        compose.onNodeWithTag("settings.connectOnLaunch").performScrollTo().assertIsOff()

        compose.onNodeWithTag("notify.All").performScrollTo().performClick()
        compose.onNodeWithTag("notify.note").assertTextEquals(text(R.string.settings_notifications_all_note))
        compose.onNodeWithTag("reconnect").performScrollTo().performClick()
        compose.onNodeWithTag("reconnect.Backoff60Seconds").assertIsSelected()
        compose.onNodeWithTag("reconnect.Never").performClick()
        compose.onNodeWithTag("reconnect.menu").assertDoesNotExist()
        compose.onNodeWithTag("settings.connectOnLaunch").performScrollTo().performClick()
        assertEquals(Behavior(NotificationLevel.All, ReconnectPolicy.Never, connectOnLaunch = true), behavior)
        compose.onNodeWithTag("notify.All").performScrollTo().assertIsSelected()
        compose.onNodeWithTag("notify.ImportantOnly").assertIsNotSelected()
        compose.onNodeWithTag("reconnect").performScrollTo().assertTextEquals(text(R.string.core_reconnect_never))
        compose.onNodeWithTag("settings.connectOnLaunch").assertIsOn()

        // У «Выключены» подпись десктопа — про трей; у телефона трея нет, и подписи нет.
        compose.onNodeWithTag("notify.Off").performScrollTo().performClick()
        assertEquals(NotificationLevel.Off, behavior.notifications)
        compose.onNodeWithTag("notify.note").assertDoesNotExist()
        compose.onNodeWithText(text(R.string.settings_notifications_off_note)).assertDoesNotExist()
    }

    @Test
    fun `language is chosen in the app when android allows it`() {
        val chosen = mutableListOf<String>()
        var language by mutableStateOf<((String) -> Unit)?>({ chosen += it })
        compose.setContent { PgTheme { SettingsScreen("26.10.21", {}, {}, TunnelSettings(), {}, language = language) } }

        // Выбран тот язык, на котором приложение сейчас; тот же — ничего, другой — Android.
        val current = if (Texts.language(context.resources.configuration.locales) == "ru") "ru" else "en"
        val other = if (current == "ru") "en" else "ru"
        compose.onNodeWithTag("language.$current").performScrollTo().assertIsSelected().assertIsEnabled()
        compose.onNodeWithTag("language.$other").assertIsNotSelected()
        compose.onNodeWithTag("language.system").assertDoesNotExist()
        compose.onNodeWithTag("language.$current").performClick()
        compose.onNodeWithTag("language.$other").performClick()
        assertEquals(listOf(other), chosen)

        // До Android 13 язык приложения — только системный: сегменты недоступны, подпись говорит, где он меняется.
        language = null
        compose.onNodeWithTag("language.$other").performScrollTo().assertIsNotEnabled().performClick()
        compose.onNodeWithTag("language.system").performScrollTo().assertTextEquals(text(R.string.android_settings_language))
        assertEquals(listOf(other), chosen)
    }

    @Test
    fun `footer shows the app and core versions without a service`() {
        var core by mutableStateOf<String?>(null)
        compose.setContent { PgTheme { SettingsScreen(core, {}, {}, TunnelSettings(), {}) } }

        val name = text(R.string.about_name) + " " + BuildConfig.VERSION_NAME
        val coreWord = text(R.string.card_core).lowercase()
        compose.onNodeWithTag("settings.footer").performScrollTo().assertTextEquals("$name · $coreWord: Xray-core —")
        core = "26.10.21"
        compose.onNodeWithTag("settings.footer").performScrollTo().assertTextEquals("$name · $coreWord: Xray-core 26.10.21")
    }

    @Test
    fun `notification button opens profiles and subscriptions once`() {
        var requested by mutableStateOf<SettingsPage?>(SettingsPage.Subscriptions)
        compose.setContent {
            PgTheme {
                SettingsScreen("26.10.21", {}, {}, TunnelSettings(), {}, requested = requested, onRequested = { requested = null }) { back ->
                    Text("subscriptions", Modifier.testTag("subscriptions.page").clickable(onClick = back))
                }
            }
        }

        compose.onNodeWithTag("subscriptions.page").assertIsDisplayed()
        assertEquals(null, requested, "запрос снят — назад ведет в настройки, а не снова в подписки")
        compose.onNodeWithTag("subscriptions.page").performClick()
        compose.onNodeWithTag("settings.profiles").performScrollTo().assertIsDisplayed()
    }

    @Test
    fun `protection kill switch group shows what android said and opens vpn settings`() {
        var system by mutableStateOf(SystemVpnView(SystemVpn(), SystemVpnSource.Unknown))
        var opened = 0
        compose.setContent { PgTheme { ProtectionScreen(TunnelSettings(), {}, {}, system) { opened++ } } }

        compose.onNodeWithText(text(R.string.prot_group_ks).uppercase()).assertIsDisplayed()
        compose.onNodeWithText(text(R.string.prot_ks_title)).assertIsDisplayed()
        compose.onNodeWithTag("prot.ks.value", useUnmergedTree = true).assertTextEquals(text(R.string.android_settings_vpn_unknown))
        compose.onNodeWithTag("prot.alwaysOn.value", useUnmergedTree = true).assertTextEquals(text(R.string.android_settings_vpn_unknown))
        compose.onNodeWithTag("prot.ks.source").assertTextEquals(text(R.string.android_settings_vpn_never))

        system = SystemVpnView(SystemVpn(alwaysOn = true, lockdown = false), SystemVpnSource.Now)
        compose.onNodeWithTag("prot.ks.value", useUnmergedTree = true).assertTextEquals(text(R.string.android_settings_vpn_off))
        compose.onNodeWithTag("prot.alwaysOn.value", useUnmergedTree = true).assertTextEquals(text(R.string.android_settings_vpn_on))
        compose.onNodeWithTag("prot.ks.source").assertTextEquals(text(R.string.android_settings_vpn_now))

        system = SystemVpnView(SystemVpn(alwaysOn = false, lockdown = true), SystemVpnSource.LastConnection)
        compose.onNodeWithTag("prot.ks.value", useUnmergedTree = true).assertTextEquals(text(R.string.android_settings_vpn_on))
        compose.onNodeWithTag("prot.alwaysOn.value", useUnmergedTree = true).assertTextEquals(text(R.string.android_settings_vpn_off))
        compose.onNodeWithTag("prot.ks.source").assertTextEquals(text(R.string.android_settings_vpn_last))

        // Строки и кнопка ведут в настройки VPN Android: включить блок может только пользователь.
        compose.onNodeWithTag("prot.ks").performClick()
        compose.onNodeWithTag("prot.alwaysOn").performClick()
        compose.onNodeWithTag("prot.ks.open").performScrollTo().assertTextEquals(text(R.string.android_settings_vpn_open)).performClick()
        assertEquals(3, opened)

        // Строк, которых телефон не умеет, нет — ни как работающих, ни как выключенных.
        compose.onNodeWithText(text(R.string.prot_strict_title)).assertDoesNotExist()
        compose.onNodeWithText(text(R.string.prot_captive_title)).assertDoesNotExist()
        compose.onNodeWithText(text(R.string.prot_dnsBlock_title)).assertDoesNotExist()
        compose.onNodeWithText(text(R.string.prot_fakeDns_title)).assertDoesNotExist()
        compose.onNodeWithText(text(R.string.prot_leaks_title)).assertDoesNotExist()
        compose.onNodeWithText(text(R.string.prot_lanDisc_title)).assertDoesNotExist()
        compose.onNodeWithText(text(R.string.prot_otherVpn_title)).assertDoesNotExist()
    }

    @Test
    fun `protection shows the saved choice and changes it by tap`() {
        var tunnel by mutableStateOf(TunnelSettings())
        compose.setContent { PgTheme { ProtectionScreen(tunnel, { tunnel = it }, {}) } }

        // По умолчанию — защищенный выбор: DNS из профиля, только IPv4, локальная сеть закрыта.
        compose.onNodeWithTag("dns").performScrollTo().assertTextEquals(text(R.string.prot_dns_profile))
        compose.onNodeWithTag("addr").performScrollTo().assertTextEquals(text(R.string.prot_addr_only4))
        compose.onNodeWithTag("lan").performScrollTo().assertIsOff()

        compose.onNodeWithTag("dns").performScrollTo().performClick()
        compose.onNodeWithTag("dns.menu").assertExists()
        compose.onNodeWithTag("dns.Profile").assertIsSelected()
        compose.onNodeWithTag("dns.Custom").assertIsNotSelected()
        compose.onNodeWithTag("dns.Quad9Doh").assertIsNotSelected().performClick()
        compose.onNodeWithTag("dns.menu").assertDoesNotExist()

        // Порядок макета: «Предпочитать IPv4», потом «Только IPv4»; «Предпочитать IPv6» на телефоне ничего бы не меняло.
        compose.onNodeWithTag("addr").performScrollTo().performClick()
        compose.onNodeWithTag("addr.Ipv4Only").assertIsSelected()
        compose.onNodeWithText(text(R.string.prot_addr_ipv6)).assertDoesNotExist()
        compose.onNodeWithTag("addr.PreferIpv4").performClick()
        compose.onNodeWithTag("lan").performScrollTo().performClick()

        assertEquals(TunnelSettings(DnsChoice.Quad9Doh, ipv6 = true, allowLan = true), tunnel)
        compose.onNodeWithTag("dns").performScrollTo().assertTextEquals(text(R.string.prot_dns_quad9))
        compose.onNodeWithTag("addr").performScrollTo().assertTextEquals(text(R.string.prot_addr_ipv4))
        compose.onNodeWithTag("lan").performScrollTo().assertIsOn()
        compose.onNodeWithTag("dns").performScrollTo().performClick()
        compose.onNodeWithTag("dns.Quad9Doh").assertIsSelected()
        compose.onNodeWithTag("dns.Profile").assertIsNotSelected()
    }

    @Test
    fun `ipv6 outside the tunnel is always blocked`() {
        var tunnel by mutableStateOf(TunnelSettings())
        compose.setContent { PgTheme { ProtectionScreen(tunnel, { tunnel = it }, {}) } }

        // Без IPv6 через сервер Android закрывает IPv6 сам (решение 397), с ним IPv6 идет в туннель: выключить нечего.
        compose.onNodeWithTag("ipv6.block").performScrollTo().assertIsOn().assertIsNotEnabled().performClick()
        assertEquals(TunnelSettings(), tunnel)
        tunnel = TunnelSettings(ipv6 = true)
        compose.onNodeWithTag("ipv6.block").performScrollTo().assertIsOn().assertIsNotEnabled()
    }

    @Test
    fun `custom dns is saved only when it is an address`() {
        var tunnel by mutableStateOf(TunnelSettings())
        compose.setContent { PgTheme { ProtectionScreen(tunnel, { tunnel = it }, {}) } }

        compose.onNodeWithTag("dns").performScrollTo().performClick()
        compose.onNodeWithTag("dns.Custom").performClick()
        // «Свой адрес…» открывает лист и сам выбор не меняет.
        compose.onNodeWithTag("dns.sheet").assertIsDisplayed()
        compose.onNodeWithText(text(R.string.prot_dnsCustom_title)).assertIsDisplayed()
        compose.onNodeWithText(text(R.string.android_dnsCustom_note)).assertIsDisplayed()
        assertEquals(TunnelSettings(), tunnel)

        // Имя хоста — нет: чтобы найти сам DNS, ядру пришлось бы спросить имя мимо туннеля.
        compose.onNodeWithTag("dns.custom.text").performTextInput("https://dns.example.com/dns-query")
        compose.onNodeWithTag("dns.custom.save").performClick()
        compose.onNodeWithTag("dns.custom.bad", useUnmergedTree = true).assertTextEquals(text(R.string.prot_dnsCustom_bad))
        assertEquals(TunnelSettings(), tunnel, "неверный адрес не сохранен")
        compose.onNodeWithTag("dns.sheet").assertExists()

        // «Отмена» закрывает лист без сохранения.
        compose.onNodeWithTag("dns.custom.cancel").performClick()
        compose.onNodeWithTag("dns.sheet").assertDoesNotExist()
        assertEquals(TunnelSettings(), tunnel)

        compose.onNodeWithTag("dns").performScrollTo().performClick()
        compose.onNodeWithTag("dns.Custom").performClick()
        compose.onNodeWithTag("dns.custom.text").performTextClearance()
        compose.onNodeWithTag("dns.custom.text").performTextInput(" https://192.0.2.53/dns-query ")
        compose.onNodeWithTag("dns.custom.save").performClick()
        assertEquals(TunnelSettings(DnsChoice.Custom, "https://192.0.2.53/dns-query"), tunnel)
        compose.onNodeWithTag("dns.sheet").assertDoesNotExist()
        // Выбран свой адрес — на кнопке сам адрес, в меню отмечен «Свой адрес…».
        compose.onNodeWithTag("dns").performScrollTo().assertTextEquals("https://192.0.2.53/dns-query").performClick()
        compose.onNodeWithTag("dns.Custom").assertIsSelected()
    }
}

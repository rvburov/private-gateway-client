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
import androidx.compose.ui.test.assertIsSelected
import androidx.compose.ui.test.assertIsNotSelected
import androidx.compose.ui.test.assertIsOff
import androidx.compose.ui.test.assertIsOn
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

/** «Настройки» и «Защита и утечки» без устройства: что выбрано, что сохраняется, неверный DNS не проходит. */
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
        compose.onNodeWithTag("settings.protection").performClick()
        compose.onNodeWithTag("protection").assertIsDisplayed()
        compose.onNodeWithText(text(R.string.prot_dns_title)).assertIsDisplayed()
        compose.onNodeWithContentDescription(text(R.string.common_back)).performClick()
        compose.onNodeWithTag("settings.protection").assertIsDisplayed()
    }

    @Test
    fun `android vpn card shows what android said and when`() {
        var system by mutableStateOf(SystemVpnView(SystemVpn(), SystemVpnSource.Unknown))
        var opened = 0
        compose.setContent { PgTheme { SettingsScreen("26.10.21", {}, { opened++ }, TunnelSettings(), {}, system = system) } }
        compose.onNodeWithTag("settings.vpn.alwaysOn").performScrollTo().assertTextEquals(text(R.string.android_settings_vpn_unknown))
        compose.onNodeWithTag("settings.vpn.source").performScrollTo().assertTextEquals(text(R.string.android_settings_vpn_never))

        system = SystemVpnView(SystemVpn(alwaysOn = true, lockdown = false), SystemVpnSource.Now)
        compose.onNodeWithTag("settings.vpn.alwaysOn").performScrollTo().assertTextEquals(text(R.string.android_settings_vpn_on))
        compose.onNodeWithTag("settings.vpn.lockdown").performScrollTo().assertTextEquals(text(R.string.android_settings_vpn_off))
        compose.onNodeWithTag("settings.vpn.source").performScrollTo().assertTextEquals(text(R.string.android_settings_vpn_now))

        system = SystemVpnView(SystemVpn(alwaysOn = true, lockdown = true), SystemVpnSource.LastConnection)
        compose.onNodeWithTag("settings.vpn.lockdown").performScrollTo().assertTextEquals(text(R.string.android_settings_vpn_on))
        compose.onNodeWithTag("settings.vpn.source").performScrollTo().assertTextEquals(text(R.string.android_settings_vpn_last))

        compose.onNodeWithTag("settings.vpn").performScrollTo().performClick()
        assertEquals(1, opened)
    }

    @Test
    fun `notifications and reconnect are chosen on the main page`() {
        var behavior by mutableStateOf(Behavior())
        compose.setContent { PgTheme { SettingsScreen("26.10.21", {}, {}, TunnelSettings(), {}, behavior, { behavior = it }) } }

        // По умолчанию — как у десктопа: «Только важные» и «Экспоненц. до 60 с».
        compose.onNodeWithTag("notify.ImportantOnly").performScrollTo().assertIsSelected()
        compose.onNodeWithTag("reconnect.Backoff60Seconds").performScrollTo().assertIsSelected()

        compose.onNodeWithTag("notify.All").performScrollTo().performClick()
        compose.onNodeWithTag("reconnect.Never").performScrollTo().performClick()
        assertEquals(Behavior(NotificationLevel.All, ReconnectPolicy.Never), behavior)
        compose.onNodeWithTag("notify.All").performScrollTo().assertIsSelected()
        compose.onNodeWithTag("notify.ImportantOnly").assertIsNotSelected()
        compose.onNodeWithTag("reconnect.Never").performScrollTo().assertIsSelected()
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
        compose.onNodeWithTag("settings.profiles").assertIsDisplayed()
    }

    @Test
    fun `protection shows the saved choice and changes it by tap`() {
        var tunnel by mutableStateOf(TunnelSettings())
        compose.setContent { PgTheme { ProtectionScreen(tunnel, { tunnel = it }, {}) } }

        // По умолчанию — защищенный выбор: DNS из профиля, только IPv4, локальная сеть закрыта.
        compose.onNodeWithTag("dns.Profile").assertIsSelected()
        compose.onNodeWithTag("ipv6.off").assertIsSelected()
        compose.onNodeWithTag("lan").performScrollTo().assertIsOff()

        compose.onNodeWithTag("dns.Quad9Doh").performClick()
        compose.onNodeWithTag("ipv6.on").performScrollTo().performClick()
        compose.onNodeWithTag("lan").performScrollTo().performClick()
        assertEquals(TunnelSettings(DnsChoice.Quad9Doh, ipv6 = true, allowLan = true), tunnel)
        compose.onNodeWithTag("dns.Quad9Doh").performScrollTo().assertIsSelected()
        compose.onNodeWithTag("dns.Profile").assertIsNotSelected()
        compose.onNodeWithTag("lan").performScrollTo().assertIsOn()
    }

    @Test
    fun `custom dns is saved only when it is an address`() {
        var tunnel by mutableStateOf(TunnelSettings())
        compose.setContent { PgTheme { ProtectionScreen(tunnel, { tunnel = it }, {}) } }

        compose.onNodeWithTag("dns.Custom").performClick()
        compose.onNodeWithText(text(R.string.android_dnsCustom_note)).performScrollTo().assertIsDisplayed()
        // Имя хоста — нет: чтобы найти сам DNS, ядру пришлось бы спросить имя мимо туннеля.
        compose.onNodeWithTag("dns.custom.text").performTextInput("https://dns.example.com/dns-query")
        compose.onNodeWithTag("dns.custom.save").performScrollTo().performClick()
        compose.onNodeWithText(text(R.string.prot_dnsCustom_bad)).performScrollTo().assertIsDisplayed()
        assertEquals(TunnelSettings(), tunnel, "неверный адрес не сохранен")

        compose.onNodeWithTag("dns.custom.text").performTextClearance()
        compose.onNodeWithTag("dns.custom.text").performTextInput(" https://192.0.2.53/dns-query ")
        compose.onNodeWithTag("dns.custom.save").performScrollTo().performClick()
        assertEquals(TunnelSettings(DnsChoice.Custom, "https://192.0.2.53/dns-query"), tunnel)
        compose.onNodeWithTag("dns.Custom").performScrollTo().assertIsSelected()
        compose.onNodeWithText("https://192.0.2.53/dns-query").assertIsDisplayed()
        compose.onNodeWithTag("dns.custom.text").assertDoesNotExist()
    }
}

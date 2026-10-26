package com.privategateway.client.ui

import android.content.Context
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.assertIsSelected
import androidx.compose.ui.test.assertIsNotSelected
import androidx.compose.ui.test.assertIsOff
import androidx.compose.ui.test.assertIsOn
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
import com.privategateway.client.core.settings.DnsChoice
import com.privategateway.client.core.settings.TunnelSettings
import com.privategateway.client.ui.settings.ProtectionScreen
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
        compose.setContent { PgTheme { SettingsScreen("26.10.21", {}, {}, tunnel) { tunnel = it } } }
        compose.onNodeWithTag("settings.protection").performClick()
        compose.onNodeWithTag("protection").assertIsDisplayed()
        compose.onNodeWithText(text(R.string.prot_dns_title)).assertIsDisplayed()
        compose.onNodeWithContentDescription(text(R.string.common_back)).performClick()
        compose.onNodeWithTag("settings.protection").assertIsDisplayed()
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

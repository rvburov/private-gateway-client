package com.privategateway.client.ui

import android.content.Context
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.assertTextEquals
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performScrollTo
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.privategateway.client.R
import com.privategateway.client.core.vpn.TunnelErrors
import com.privategateway.client.ui.home.OtherVpnActions
import com.privategateway.client.ui.home.OtherVpnScreen
import com.privategateway.client.ui.theme.PgTheme
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.annotation.Config
import kotlin.test.assertEquals

/** «Работает другой VPN» на Android (доска 16, DECISIONS.md, п. 4): тексты для телефона и кнопки страницы. */
@RunWith(AndroidJUnit4::class)
@Config(qualifiers = "w384dp-h832dp")
class OtherVpnScreenTest {
    @get:Rule
    val compose = createComposeRule()

    private val context = ApplicationProvider.getApplicationContext<Context>()

    @Test
    fun `page explains the phone vpn and its buttons do their job`() {
        val pressed = mutableListOf<String>()
        compose.setContent {
            PgTheme {
                OtherVpnScreen(OtherVpnActions(retry = { pressed += "retry" }, vpnSettings = { pressed += "settings" }, details = { pressed += "details" }))
            }
        }

        compose.onNodeWithTag("other_vpn.title").assertTextEquals(context.getString(R.string.problem_conflict_title_mobile))
        compose.onNodeWithTag("other_vpn.text").assertTextEquals(context.getString(R.string.problem_conflict_text_android))
        compose.onNodeWithTag("other_vpn.retry").performScrollTo().assertIsDisplayed().performClick()
        compose.onNodeWithTag("other_vpn.settings").performScrollTo().performClick()
        compose.onNodeWithTag("other_vpn.details").performScrollTo()
            .assertTextEquals(context.getString(R.string.problem_details, TunnelErrors.OTHER_VPN)).performClick()
        assertEquals(listOf("retry", "settings", "details"), pressed)
    }
}

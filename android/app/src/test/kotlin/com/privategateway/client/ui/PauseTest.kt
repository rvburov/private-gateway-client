package com.privategateway.client.ui

import android.content.Context
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.assertTextContains
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performScrollTo
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.privategateway.client.R
import com.privategateway.client.core.vpn.TunnelState
import com.privategateway.client.core.vpn.TunnelStatus
import com.privategateway.client.ui.home.HomeActions
import com.privategateway.client.ui.home.HomeScreen
import com.privategateway.client.ui.theme.LocalPgToast
import com.privategateway.client.ui.theme.PgTheme
import com.privategateway.client.ui.theme.PgToastState
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.annotation.Config
import kotlin.test.assertEquals

/** Пауза для входа в Wi-Fi на главной (решение 557): вид «Пауза», отсчет и «Вернуть защиту», «Вход в Wi-Fi» в подключении. */
@RunWith(AndroidJUnit4::class)
@Config(qualifiers = "w384dp-h832dp")
class PauseTest {
    @get:Rule
    val compose = createComposeRule()

    private val context = ApplicationProvider.getApplicationContext<Context>()

    @Test
    fun `paused tunnel has its own look unless Android keeps the network closed`() {
        assertEquals(ProtectionView.Paused, ProtectionView.of(TunnelState.Off, closed = false, leak = false, paused = true))
        assertEquals(ProtectionView.Strict, ProtectionView.of(TunnelState.Off, closed = true, leak = false, paused = true))
        assertEquals(ProtectionView.Off, ProtectionView.of(TunnelState.Off, closed = false, leak = false))
        val look = StateLook.of(ProtectionView.Paused)
        assertEquals(R.string.status_paused_title, look.title)
        assertEquals(R.string.power_label_paused, look.power)
        assertEquals(ProtectionView.Paused, MainUi(status = TunnelStatus(pausedUntil = Long.MAX_VALUE)).view)
    }

    @Test
    fun `home offers sign-in while reconnecting and resume while paused`() {
        var captive = 0
        var resumed = 0
        val ui = mutableStateOf(MainUi(status = TunnelStatus(state = TunnelState.Reconnecting, attempt = 2), loaded = true))
        val toast = PgToastState()
        compose.setContent {
            PgTheme {
                CompositionLocalProvider(LocalPgToast provides toast) {
                    HomeScreen(ui.value, {}, {}, {}, actions = HomeActions(captive = { captive++ }, resume = { resumed++ }))
                }
            }
        }

        compose.onNodeWithTag("status.captive").performScrollTo().performClick()
        assertEquals(1, captive)

        // Срок уже вышел: отсчет не крутится в тесте с виртуальным временем, строка паузы и кнопка — те же.
        ui.value = MainUi(status = TunnelStatus(pausedUntil = System.currentTimeMillis() - 1), loaded = true)
        compose.waitForIdle()
        compose.onNodeWithTag("status.checked").assertTextContains(context.getString(R.string.pause_banner), substring = true)
        compose.onNodeWithTag("status.resume").performScrollTo().assertIsDisplayed().performClick()
        assertEquals(1, resumed)
    }
}

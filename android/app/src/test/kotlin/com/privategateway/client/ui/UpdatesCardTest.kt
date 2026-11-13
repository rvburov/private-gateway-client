package com.privategateway.client.ui

import android.content.Context
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.platform.LocalUriHandler
import androidx.compose.ui.platform.UriHandler
import androidx.compose.ui.test.assertIsEnabled
import androidx.compose.ui.test.assertIsNotEnabled
import androidx.compose.ui.test.assertTextEquals
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performScrollTo
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.privategateway.client.R
import com.privategateway.client.ui.settings.AboutScreen
import com.privategateway.client.ui.theme.PgTheme
import com.privategateway.client.updates.InstallStep
import com.privategateway.client.updates.UpdateStatus
import com.privategateway.client.updates.UpdatesView
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.annotation.Config
import java.util.Locale
import kotlin.test.assertEquals

/** «Обновления» в «О программе» Android: проверка, «Доступно обновление», загрузка, установка и ее отказы — словами. */
@RunWith(AndroidJUnit4::class)
@Config(qualifiers = "w384dp-h832dp")
class UpdatesCardTest {
    @get:Rule
    val compose = createComposeRule()

    private val context = ApplicationProvider.getApplicationContext<Context>()

    private val offer = UpdatesView(
        status = UpdateStatus.Available,
        checkedAt = 0,
        version = "0.3.0",
        highlights = listOf("Обновления с сайта", "Установка из приложения"),
        notes = "https://private-gateway.duckdns.org/downloads",
    )

    private class Links : UriHandler {
        val opened = mutableListOf<String>()

        override fun openUri(uri: String) {
            opened += uri
        }
    }

    @Test
    fun `check button asks and every result is said in words`() {
        var view by mutableStateOf(UpdatesView())
        var checks = 0
        var shown = 0
        compose.setContent {
            PgTheme { AboutScreen("26.10.21", {}, {}, {}, updates = view, onCheckUpdates = { checks++ }, onShown = { shown++ }) {} }
        }

        compose.waitForIdle()
        assertEquals(1, shown, "открыта «О программе» — проверка, если пора")
        // Еще не проверяли — проверка при показе окна вот-вот начнется.
        val state = compose.onNodeWithTag("about.updates.state")
        state.assertTextEquals(context.getString(R.string.about_updates_checking))
        compose.onNodeWithTag("about.updates.check").performClick()
        assertEquals(1, checks)

        view = UpdatesView(checking = true)
        state.assertTextEquals(context.getString(R.string.about_updates_checking))
        compose.onNodeWithTag("about.updates.check").assertIsNotEnabled()

        view = UpdatesView(status = UpdateStatus.NotSigned, checkedAt = 0)
        state.assertTextEquals(context.getString(R.string.android_updates_unsigned))

        view = UpdatesView(status = UpdateStatus.SignedOut, checkedAt = 0)
        state.assertTextEquals(context.getString(R.string.about_updates_signIn))

        view = UpdatesView(status = UpdateStatus.Failed, checkedAt = 0)
        state.assertTextEquals(context.getString(R.string.about_updates_failed))

        val now = System.currentTimeMillis()
        view = UpdatesView(status = UpdateStatus.UpToDate, checkedAt = now)
        val at = java.time.format.DateTimeFormatter.ofPattern("HH:mm").format(java.time.Instant.ofEpochMilli(now).atZone(java.time.ZoneId.systemDefault()))
        state.assertTextEquals(context.getString(R.string.about_updates_upToDate, context.getString(R.string.when_at, at)))
        compose.onNodeWithTag("about.updates.install").assertDoesNotExist()
        compose.onNodeWithTag("about.updates.highlights").assertDoesNotExist()
    }

    @Test
    fun `available update offers download and install with its highlights`() {
        var installs = 0
        var checks = 0
        compose.setContent {
            PgTheme { AboutScreen("26.10.21", {}, {}, {}, updates = offer, onCheckUpdates = { checks++ }, onInstallUpdate = { installs++ }) {} }
        }

        compose.onNodeWithTag("about.updates.state").assertTextEquals(context.getString(R.string.about_update_available, "0.3.0"))
        compose.onNodeWithText("Установка из приложения").performScrollTo()
        compose.onNodeWithTag("about.updates.install").assertTextEquals(context.getString(R.string.android_update_install)).performClick()
        compose.onNodeWithTag("about.updates.check").assertIsEnabled().performClick()
        compose.onNodeWithTag("about.updates.progress").assertDoesNotExist()
        assertEquals(1, installs)
        assertEquals(1, checks)
    }

    @Test
    fun `download shows its progress and megabytes and cannot be started twice`() {
        val received = 19_293_798L
        val total = 30_094_131L
        compose.setContent { PgTheme { AboutScreen("26.10.21", {}, {}, {}, updates = offer.copy(step = InstallStep.Downloading, received = received, total = total)) {} } }

        compose.onNodeWithTag("about.updates.state").assertTextEquals(context.getString(R.string.android_update_downloading, "0.3.0"))
        compose.onNodeWithTag("about.updates.progress").assertExists()
        val locale = Locale.forLanguageTag(Texts.language(context.resources.configuration.locales))
        fun mb(bytes: Long) = String.format(locale, "%.1f", bytes / 1_048_576.0)
        val bytes = context.getString(R.string.about_update_progress, mb(received), context.getString(R.string.about_update_mb, mb(total)))
        compose.onNodeWithTag("about.updates.bytes").assertTextEquals(bytes)
        assertEquals("18", mb(received).take(2))
        compose.onNodeWithTag("about.updates.install").assertTextEquals(context.getString(R.string.about_update_loading_button)).assertIsNotEnabled()
        compose.onNodeWithTag("about.updates.check").assertIsNotEnabled()
    }

    @Test
    fun `install steps and failures are said in words, a failure offers the site`() {
        var view by mutableStateOf(offer.copy(step = InstallStep.Installing))
        val links = Links()
        compose.setContent {
            CompositionLocalProvider(LocalUriHandler provides links) {
                PgTheme { AboutScreen("26.10.21", {}, {}, {}, updates = view) {} }
            }
        }

        val state = compose.onNodeWithTag("about.updates.state")
        state.assertTextEquals(context.getString(R.string.about_update_ready))
        compose.onNodeWithTag("about.updates.progress").assertExists()
        compose.onNodeWithTag("about.updates.bytes").assertDoesNotExist()
        compose.onNodeWithTag("about.updates.install").assertIsEnabled()

        view = offer.copy(step = InstallStep.Allow)
        state.assertTextEquals(context.getString(R.string.android_update_allow))
        compose.onNodeWithTag("about.updates.install").assertIsEnabled()
        compose.onNodeWithTag("about.updates.check").assertExists()

        view = offer.copy(step = InstallStep.BadFile)
        state.assertTextEquals(context.getString(R.string.android_update_badFile))
        compose.onNodeWithTag("about.updates.check").assertDoesNotExist()
        compose.onNodeWithTag("about.updates.site").assertTextEquals(context.getString(R.string.about_update_open)).performClick()

        view = offer.copy(step = InstallStep.Failed, notes = null)
        state.assertTextEquals(context.getString(R.string.android_update_failed))
        compose.onNodeWithTag("about.updates.site").performClick()
        assertEquals(listOf("https://private-gateway.duckdns.org/downloads", "https://private-gateway.duckdns.org/downloads"), links.opened)
    }

    @Test
    fun `new versions lead to the downloads page of the site, not to github`() {
        val links = Links()
        compose.setContent {
            CompositionLocalProvider(LocalUriHandler provides links) {
                PgTheme { AboutScreen("26.10.21", {}, {}, {}) {} }
            }
        }

        compose.onNodeWithTag("about.releases").performScrollTo().performClick()
        assertEquals(listOf("https://private-gateway.duckdns.org/downloads"), links.opened)
    }
}

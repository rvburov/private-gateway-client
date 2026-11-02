package com.privategateway.client.ui

import android.content.Context
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.assertTextContains
import androidx.compose.ui.test.assertTextEquals
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performScrollTo
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.privategateway.client.BuildConfig
import com.privategateway.client.R
import com.privategateway.client.ui.settings.AboutScreen
import com.privategateway.client.ui.settings.abiName
import com.privategateway.client.ui.theme.PgTheme
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.annotation.Config
import kotlin.test.assertEquals

/** «О программе» (доска 15) и первый запуск (доска 10) на телефоне — без устройства (решение 555). */
@RunWith(AndroidJUnit4::class)
@Config(qualifiers = "w384dp-h832dp")
class AboutWelcomeTest {
    @get:Rule
    val compose = createComposeRule()

    private val context = ApplicationProvider.getApplicationContext<Context>()

    private fun text(id: Int) = context.getString(id)

    @Test
    fun `about shows the app, its components, privacy and links`() {
        var licenses = 0
        var reports = 0
        compose.setContent { PgTheme { AboutScreen("26.10.21", {}, { licenses++ }, { reports++ }) {} } }

        compose.onNodeWithTag("about.version").assertTextContains(BuildConfig.VERSION_NAME, substring = true)
            .assertTextContains(" · Android ", substring = true)
        compose.onNodeWithTag("about.client").assertTextContains(BuildConfig.VERSION_NAME, substring = true)
        compose.onNodeWithTag("about.core").assertTextContains("26.10.21", substring = true)
        compose.onNodeWithTag("about.tun").assertTextContains(text(R.string.about_tun_system), substring = true)
        // Телефон пишет о своем устройстве, а не о компьютере.
        compose.onNodeWithText(text(R.string.about_privacy_logs_mobile)).performScrollTo().assertIsDisplayed()

        compose.onNodeWithTag("about.licenses").performScrollTo().performClick()
        compose.onNodeWithTag("about.report").performScrollTo().performClick()
        assertEquals(1, licenses)
        assertEquals(1, reports)

        compose.onNodeWithTag("about.privacy").performScrollTo().performClick()
        compose.onNodeWithTag("about.privacy.sheet").assertIsDisplayed()
        compose.onNodeWithText(text(R.string.about_privacy_text_mobile)).assertIsDisplayed()
        compose.onNodeWithTag("about.privacy.close").performClick()
        compose.onNodeWithTag("about.releases").performScrollTo().assertIsDisplayed()
    }

    @Test
    fun `architecture is named as in the mockup`() {
        assertEquals("arm64", abiName("arm64-v8a"))
        assertEquals("x64", abiName("x86_64"))
        assertEquals("arm", abiName("armeabi-v7a"))
        assertEquals("—", abiName(null))
    }

    @Test
    fun `welcome greets, then asks for the first server or lets the user skip`() {
        var added = 0
        var done = 0
        compose.setContent { PgTheme { WelcomeScreen(onAdd = { added++ }, onDone = { done++ }, language = null) } }

        compose.onNodeWithText(text(R.string.welcome_tagline)).assertIsDisplayed()
        compose.onNodeWithText(context.getString(R.string.welcome_all_title, "Android")).assertIsDisplayed()
        compose.onNodeWithText(text(R.string.welcome_all_text_mobile)).assertIsDisplayed()
        compose.onNodeWithTag("welcome.start").performScrollTo().performClick()

        compose.onNodeWithTag("welcome.add").assertIsDisplayed()
        compose.onNodeWithTag("welcome.add.button").performScrollTo().assertTextEquals(text(R.string.welcome_add_button)).performClick()
        assertEquals(1, added)
        assertEquals(0, done, "«Добавить сервер» еще не завершает первый запуск: его завершит первый сервер")
        compose.onNodeWithTag("welcome.add.later").performScrollTo().performClick()
        assertEquals(1, done)
    }
}

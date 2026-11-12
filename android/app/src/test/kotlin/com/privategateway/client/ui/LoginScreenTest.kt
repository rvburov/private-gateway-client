package com.privategateway.client.ui

import android.content.Context
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.assertIsEnabled
import androidx.compose.ui.test.assertIsNotEnabled
import androidx.compose.ui.test.assertTextEquals
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performImeAction
import androidx.compose.ui.test.performScrollTo
import androidx.compose.ui.test.performTextInput
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.privategateway.client.R
import com.privategateway.client.account.AccountInfo
import com.privategateway.client.account.AccountNotice
import com.privategateway.client.core.account.AccountError
import com.privategateway.client.ui.account.LoginActions
import com.privategateway.client.ui.account.LoginScreen
import com.privategateway.client.ui.account.LoginUi
import com.privategateway.client.ui.settings.SettingsScreen
import com.privategateway.client.ui.theme.PgTheme
import com.privategateway.client.core.settings.TunnelSettings
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.annotation.Config
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertTrue

/** Экран входа в аккаунт сайта (решение 621) и его форма — без устройства и без сети. */
@RunWith(AndroidJUnit4::class)
@Config(qualifiers = "w384dp-h832dp")
class LoginScreenTest {
    @get:Rule
    val compose = createComposeRule()

    private val context = ApplicationProvider.getApplicationContext<Context>()

    private fun text(id: Int) = context.getString(id)

    /** Форма в памяти теста: поля меняют то же, что модель; «Войти» и сайт — счетчики. */
    private inner class Host(start: LoginUi = LoginUi(), notice: AccountNotice? = null) {
        var ui by mutableStateOf(start)
        var submits = 0
        val sites = mutableListOf<String>()

        init {
            compose.setContent {
                PgTheme {
                    LoginScreen(
                        ui,
                        notice,
                        LoginActions(
                            email = { ui = ui.copy(email = it) },
                            password = { ui = ui.copy(password = it) },
                            code = { ui = ui.copy(code = it) },
                            toggleRecovery = { ui = ui.copy(recovery = !ui.recovery, code = "") },
                            submit = { submits++ },
                            site = { sites += it },
                        ),
                        language = null,
                    )
                }
            }
        }
    }

    @Test
    fun `sign-in is enabled when the form is filled and Enter in the code sends it`() {
        val host = Host()
        compose.onNodeWithTag("login").assertIsDisplayed()
        compose.onNodeWithTag("login.submit").performScrollTo().assertIsNotEnabled().assertTextEquals(text(R.string.account_signIn))

        compose.onNodeWithTag("login.email").performTextInput("user@example.com")
        compose.onNodeWithTag("login.password").performTextInput("pw")
        compose.onNodeWithTag("login.submit").assertIsNotEnabled()
        compose.onNodeWithTag("login.code").performTextInput("123456")
        compose.onNodeWithTag("login.submit").performScrollTo().assertIsEnabled().performClick()
        assertEquals(1, host.submits)

        compose.onNodeWithTag("login.code").performImeAction()
        assertEquals(2, host.submits, "Enter в поле кода отправляет форму")
        compose.onNodeWithTag("login.password.show").assertIsDisplayed().performClick()
    }

    @Test
    fun `request in progress shows signing in and blocks the button`() {
        Host(LoginUi("user@example.com", "pw", "123456", busy = true))
        compose.onNodeWithTag("login.submit").performScrollTo()
            .assertTextEquals(text(R.string.account_signingIn))
            .assertIsNotEnabled()
    }

    @Test
    fun `recovery code link switches the code field and back`() {
        val host = Host()
        compose.onNodeWithTag("login.recovery.toggle").performScrollTo().assertTextEquals(text(R.string.account_recovery_use)).performClick()
        assertTrue(host.ui.recovery)
        compose.onNodeWithTag("login.code.hint").assertTextEquals(text(R.string.account_recovery_hint))
        compose.onNodeWithTag("login.recovery.toggle").assertTextEquals(text(R.string.account_code_use)).performClick()
        compose.onNodeWithTag("login.code.hint").assertTextEquals(text(R.string.account_code_hint))
    }

    @Test
    fun `errors have their texts and 403 and 409 lead to the site`() {
        val host = Host()
        host.ui = LoginUi(error = AccountError.Invalid)
        compose.onNodeWithTag("login.error").assertTextEquals(text(R.string.account_error_invalid))
        compose.onNodeWithTag("login.site").assertDoesNotExist()

        host.ui = LoginUi(error = AccountError.RateLimited, retryMinutes = 3)
        compose.onNodeWithTag("login.error").assertTextEquals(context.getString(R.string.account_error_rateLimited, "3"))

        host.ui = LoginUi(error = AccountError.SetupRequired)
        compose.onNodeWithTag("login.error").assertTextEquals(text(R.string.account_error_setupRequired))
        compose.onNodeWithTag("login.site").performScrollTo().assertTextEquals(text(R.string.account_site)).performClick()

        host.ui = LoginUi(error = AccountError.DeviceLimit)
        compose.onNodeWithTag("login.error").assertTextEquals(text(R.string.account_error_deviceLimit))
        compose.onNodeWithTag("login.site").performScrollTo().assertTextEquals(text(R.string.account_devices)).performClick()
        assertEquals(listOf("https://private-gateway.duckdns.org/login", "https://private-gateway.duckdns.org/devices"), host.sites)

        host.ui = LoginUi(error = AccountError.Network)
        compose.onNodeWithTag("login.error").assertTextEquals(text(R.string.account_error_network))
        compose.onNodeWithTag("login.site").assertDoesNotExist()
    }

    @Test
    fun `revoked and expired sign-in are explained above the form`() {
        val host = Host(notice = AccountNotice.Revoked)
        compose.onNodeWithTag("login.notice").assertIsDisplayed()
        compose.onNodeWithText(text(R.string.account_revoked)).assertIsDisplayed()
        assertEquals(0, host.submits)
    }

    @Test
    @Config(qualifiers = "w360dp-h640dp")
    fun `narrow phone keeps the whole form reachable`() {
        Host(LoginUi(error = AccountError.DeviceLimit), AccountNotice.Expired)
        for (tag in listOf("login.email", "login.password", "login.code", "login.recovery.toggle", "login.error", "login.submit", "login.site")) {
            compose.onNodeWithTag(tag).performScrollTo().assertIsDisplayed()
        }
    }

    @Test
    fun `settings show the account and ask before signing out`() {
        var signOuts = 0
        val info = AccountInfo("user@example.com", "Участник", "Google Pixel 8", Instant.parse("2027-01-01T00:00:00Z"), null)
        compose.setContent { PgTheme { SettingsScreen("26.10.21", {}, {}, TunnelSettings(), {}, account = info, onSignOut = { signOuts++ }) } }

        compose.onNodeWithTag("settings.account.name").assertTextEquals("Участник")
        compose.onNodeWithTag("settings.account.email").assertTextEquals("user@example.com")
        compose.onNodeWithTag("settings.account.device").assertTextEquals(context.getString(R.string.settings_account_device, "Google Pixel 8"))

        compose.onNodeWithTag("settings.account.signOut").performScrollTo().performClick()
        compose.onNodeWithTag("dialog.signOut").assertIsDisplayed()
        compose.onNodeWithTag("dialog.signOut.primary").assertTextEquals(text(R.string.dialog_signOut_keep)).performClick()
        assertEquals(0, signOuts, "«Остаться» не выходит")
        compose.onNodeWithTag("dialog.signOut").assertDoesNotExist()

        compose.onNodeWithTag("settings.account.signOut").performScrollTo().performClick()
        compose.onNodeWithTag("dialog.signOut.secondary").assertTextEquals(text(R.string.dialog_signOut_confirm)).performClick()
        assertEquals(1, signOuts)
    }

    @Test
    fun `settings without a sign-in have no account card`() {
        compose.setContent { PgTheme { SettingsScreen("26.10.21", {}, {}, TunnelSettings(), {}) } }
        compose.onNodeWithTag("settings").assertIsDisplayed()
        compose.onNodeWithTag("settings.account").assertDoesNotExist()
    }
}

package com.privategateway.client.ui

import android.content.Context
import android.os.LocaleList
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.privategateway.client.R
import com.privategateway.client.core.account.AccountError
import com.privategateway.client.core.servers.ProxyProtocol
import com.privategateway.client.core.servers.ServerItem
import com.privategateway.client.core.servers.TransportKind
import com.privategateway.client.core.servers.TransportSecurity
import com.privategateway.client.core.subscriptions.FailureReason
import com.privategateway.client.core.vpn.TunnelErrors
import com.privategateway.client.core.vpn.TunnelState
import com.privategateway.client.core.vpn.TunnelStatus
import org.junit.Test
import org.junit.runner.RunWith
import kotlin.test.assertEquals
import kotlin.test.assertTrue

@RunWith(AndroidJUnit4::class)
class TextsTest {
    @Test
    fun `plural forms follow the language android took the texts in`() {
        // Украинских и немецких текстов нет: Android берет следующий язык списка, у которого тексты есть.
        assertEquals("ru", Texts.language(LocaleList.forLanguageTags("uk-UA,ru-RU")))
        assertEquals("ru", Texts.language(LocaleList.forLanguageTags("de-DE,ru-RU,en-US")))
        assertEquals("en", Texts.language(LocaleList.forLanguageTags("en-GB,ru-RU")))
        assertEquals("ru", Texts.language(LocaleList.forLanguageTags("ru-RU")))
        assertEquals(R.string.import_add_few, Texts.importAdd(3, Texts.language(LocaleList.forLanguageTags("uk-UA,ru-RU"))))
    }

    @Test
    fun `power button connects, adds the first server or leads to the list`() {
        val nl = ServerItem("nl", "Нидерланды 01", "NL", ProxyProtocol.Vless, TransportSecurity.Reality, TransportKind.Tcp, 443)
        assertEquals(PowerAction.Add, MainUi().power)
        assertEquals(PowerAction.Choose, MainUi(servers = listOf(nl)).power, "серверы есть, ни один не выбран — сам не выбирается")
        assertEquals(PowerAction.Toggle, MainUi(servers = listOf(nl), selectedId = "nl").power)
        assertEquals(PowerAction.Choose, MainUi(TunnelStatus(TunnelState.Error, error = "PGC-SVC-003"), listOf(nl)).power)
        // Защита идет — щит выключает ее, даже если выбор уже снят.
        assertEquals(PowerAction.Toggle, MainUi(TunnelStatus(TunnelState.Connecting), listOf(nl)).power)
        assertEquals(PowerAction.Toggle, MainUi(TunnelStatus(TunnelState.Connected, serverId = "gone")).power)
    }

    @Test
    fun `stale subscription in the window is the title and the text with the reason`() {
        // Доска 19: «Серверы не обновились · «Основная»: <причина>. Оставлен прошлый список.» — как подсказка десктопа.
        val context = ApplicationProvider.getApplicationContext<Context>()
        val message = Message(
            R.string.notify_stale_text, "", "Основная", long = true,
            title = R.string.notify_stale_title, reason = FailureReason.Forbidden(403),
        )

        val text = message.render(context)

        assertTrue(text.startsWith(context.getString(R.string.notify_stale_title) + " · "), text)
        assertTrue(text.contains("Основная") && text.contains("403"), text)
        assertTrue(!text.contains("%") && !text.contains("{"), "подстановки заполнены: $text")
        assertEquals(context.getString(R.string.subs_blocked_off), Message(R.string.subs_blocked_off).render(context))
    }

    @Test
    fun `every sign-in error has its own text`() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val texts = AccountError.entries.map { Texts.account(it) }
        assertEquals(AccountError.entries.size, texts.toSet().size, "у каждой причины свой текст")
        assertEquals(R.string.account_error_invalid, Texts.account(AccountError.Invalid))
        assertEquals(R.string.account_error_code, Texts.account(AccountError.Code))
        assertEquals(R.string.account_error_setupRequired, Texts.account(AccountError.SetupRequired))
        assertEquals(R.string.account_error_deviceLimit, Texts.account(AccountError.DeviceLimit))
        assertEquals(R.string.account_error_rateLimited, Texts.account(AccountError.RateLimited))
        assertEquals(R.string.account_error_validation, Texts.account(AccountError.Validation))
        assertEquals(R.string.account_error_network, Texts.account(AccountError.Network))
        assertEquals(R.string.account_error_server, Texts.account(AccountError.Server))
        assertEquals(R.string.account_error_store, Texts.account(AccountError.Store))
        val minutes = context.getString(Texts.account(AccountError.RateLimited), "3")
        assertTrue("3" in minutes && "{" !in minutes && "%" !in minutes, minutes)
    }

    @Test
    fun `no access has its own error text`() {
        assertEquals(R.string.error_PGC_ACC_001, Texts.error(TunnelErrors.NO_ACCESS))
        assertEquals(R.string.error_PGC_ACC_001, Texts.error("PGC-ACC-001"))
    }
}

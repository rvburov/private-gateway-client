package com.privategateway.client.ui

import android.content.Context
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.assertIsNotEnabled
import androidx.compose.ui.test.assertTextContains
import androidx.compose.ui.test.hasAnyAncestor
import androidx.compose.ui.test.hasTestTag
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performScrollTo
import androidx.compose.ui.test.performTextInput
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.privategateway.client.R
import com.privategateway.client.core.servers.ProxyProtocol
import com.privategateway.client.core.servers.ServerItem
import com.privategateway.client.core.servers.TransportKind
import com.privategateway.client.core.servers.TransportSecurity
import com.privategateway.client.core.settings.SubscriptionSettings
import com.privategateway.client.core.subscriptions.DownloadFailure
import com.privategateway.client.core.subscriptions.RefreshInterval
import com.privategateway.client.core.text.Plural
import com.privategateway.client.core.vpn.TunnelState
import com.privategateway.client.core.vpn.TunnelStatus
import com.privategateway.client.data.GroupItem
import com.privategateway.client.ui.subscriptions.SubscriptionsActions
import com.privategateway.client.ui.subscriptions.SubscriptionsScreen
import com.privategateway.client.ui.subscriptions.SubscriptionsUi
import com.privategateway.client.ui.theme.PgTheme
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.annotation.Config
import kotlin.test.assertEquals

/** «Профили и подписки» (доска 12 в одну колонку) без устройства. */
@RunWith(AndroidJUnit4::class)
@Config(qualifiers = "w384dp-h832dp")
class SubscriptionsScreenTest {
    @get:Rule
    val compose = createComposeRule()

    private val context = ApplicationProvider.getApplicationContext<Context>()
    private val now = System.currentTimeMillis()
    private val ok = GroupItem("ok", "Провайдер", "https://example.com/sub/••••", 12, now - 60_000, now - 60_000, null, null)
    private val broken = GroupItem("broken", "Запасная", "https://example.org/s/••••", 3, now - 86_400_000L * 3, now - 5 * 60_000, DownloadFailure.Status, 403)
    private val profile = GroupItem("profile", "Друзья", null, 1, null, null, null, null)
    private val server = ServerItem("s1", "Нидерланды 01", "NL", ProxyProtocol.Vless, TransportSecurity.Reality, TransportKind.Tcp, 443, subscriptionId = "ok")

    private fun text(id: Int, vararg args: Any) = context.getString(id, *args)

    /** «12 серверов» на языке ресурсов теста — та же форма числа, что выбирает экран. */
    private fun servers(count: Int): String {
        val form = Plural.form(count, Texts.language(context.resources.configuration.locales))
        val id = when (form) {
            "one" -> R.string.subs_servers_one
            "few" -> R.string.subs_servers_few
            else -> R.string.subs_servers_many
        }

        return text(id, count.toString())
    }

    private class Recorder {
        var settings by mutableStateOf(SubscriptionSettings())
        val updated = mutableListOf<String>()
        val edits = mutableListOf<Triple<String, String, String>>()
        val removed = mutableListOf<Triple<String?, String, Boolean>>()
        var accept = true

        val actions = SubscriptionsActions(
            settings = { settings = it },
            add = {},
            update = { updated += it.id },
            edit = { group, name, url ->
                edits += Triple(group.id, name, url)
                accept
            },
            remove = { id, name, inUse -> removed += Triple(id, name, inUse) },
        )
    }

    private fun show(state: SubscriptionsUi, main: MainUi = MainUi()): Recorder {
        val recorder = Recorder()
        compose.setContent { PgTheme { SubscriptionsScreen(state.copy(settings = recorder.settings), main, recorder.actions) {} } }
        return recorder
    }

    @Test
    fun `cards show state, masked address, time and the reason of a failure`() {
        show(SubscriptionsUi(listOf(ok, broken, profile), manualCount = 2))
        compose.onNodeWithText(text(R.string.subs_section, "3"), substring = true, ignoreCase = true).assertExists()
        compose.onNodeWithText("https://example.com/sub/••••").performScrollTo().assertIsDisplayed()
        compose.onNode(hasTestTag("group.meta") and hasAnyAncestor(hasTestTag("group.ok")))
            .assertTextContains(servers(12), substring = true)
        compose.onNodeWithTag("group.error").performScrollTo()
            .assertTextContains(text(R.string.import_download_forbidden, "403").replaceFirstChar { it.uppercaseChar() }, substring = true)
        compose.onNodeWithText(text(R.string.subs_manual_address)).performScrollTo().assertIsDisplayed()
        compose.onNodeWithTag("group.mine").performScrollTo().assertIsDisplayed()
        compose.onNodeWithText(servers(2)).performScrollTo().assertIsDisplayed()
        compose.onNode(hasTestTag("subs.update") and hasAnyAncestor(hasTestTag("group.profile"))).assertDoesNotExist()
    }

    @Test
    fun `only via vpn without protection says that nothing updates`() {
        show(SubscriptionsUi(listOf(ok)))
        compose.onNodeWithTag("subs.vpn.blocked").assertIsDisplayed()
    }

    @Test
    fun `under protection the note is gone`() {
        show(SubscriptionsUi(listOf(ok), connected = true))
        compose.onNodeWithTag("subs.vpn.blocked").assertDoesNotExist()
    }

    @Test
    fun `settings change the interval and the rules`() {
        val recorder = show(SubscriptionsUi(listOf(ok)))
        compose.onNodeWithTag("refresh.Off").performClick()
        assertEquals(RefreshInterval.Off, recorder.settings.refresh)
        compose.onNodeWithTag("subs.vpn").performScrollTo().performClick()
        assertEquals(false, recorder.settings.onlyViaVpn)
        compose.onNodeWithTag("subs.vpn.blocked").assertDoesNotExist()
        compose.onNodeWithTag("subs.notify").performScrollTo().performClick()
        assertEquals(false, recorder.settings.notifySelected)
    }

    @Test
    fun `update runs once and waits while busy`() {
        val recorder = show(SubscriptionsUi(listOf(ok, broken), busy = setOf("broken")))
        compose.onNode(hasTestTag("subs.update") and hasAnyAncestor(hasTestTag("group.ok"))).performScrollTo().performClick()
        assertEquals(listOf("ok"), recorder.updated)
        compose.onNode(hasTestTag("subs.update") and hasAnyAncestor(hasTestTag("group.broken"))).performScrollTo()
            .assertIsNotEnabled()
            .assertTextContains(text(R.string.import_downloading))
    }

    @Test
    fun `edit refuses an address that is not http and closes after a good one`() {
        val recorder = show(SubscriptionsUi(listOf(ok)))
        compose.onNode(hasTestTag("subs.edit") and hasAnyAncestor(hasTestTag("group.ok"))).performScrollTo().performClick()
        compose.onNodeWithTag("subs.edit.url").performScrollTo().performTextInput("ftp://example.com/sub")
        recorder.accept = false
        compose.onNodeWithTag("subs.edit.save").performScrollTo().performClick()
        compose.onNodeWithTag("subs.edit.bad").performScrollTo().assertIsDisplayed()
        compose.onNodeWithTag("subs.editor").assertExists()

        recorder.accept = true
        compose.onNodeWithTag("subs.edit.save").performScrollTo().performClick()
        compose.onNodeWithTag("subs.editor").assertDoesNotExist()
        assertEquals(Triple("ok", "Провайдер", "ftp://example.com/sub"), recorder.edits.last())
    }

    @Test
    fun `removing the group in use warns that the VPN goes off`() {
        val main = MainUi(TunnelStatus(TunnelState.Connected, serverId = "s1"), listOf(server), "s1")
        val recorder = show(SubscriptionsUi(listOf(ok, broken), connected = true), main)
        compose.onNode(hasTestTag("subs.remove") and hasAnyAncestor(hasTestTag("group.ok"))).performScrollTo().performClick()
        compose.onNodeWithText(text(R.string.subs_remove_textInUse, servers(12))).assertIsDisplayed()
        compose.onNodeWithTag("subs.remove.confirm").performClick()
        assertEquals(listOf(Triple<String?, String, Boolean>("ok", "Провайдер", true)), recorder.removed)

        compose.onNode(hasTestTag("subs.remove") and hasAnyAncestor(hasTestTag("group.broken"))).performScrollTo().performClick()
        compose.onNodeWithText(text(R.string.subs_remove_text, servers(3))).assertIsDisplayed()
        compose.onNodeWithTag("subs.remove.confirm").performClick()
        assertEquals(Triple<String?, String, Boolean>("broken", "Запасная", false), recorder.removed.last())
    }
}

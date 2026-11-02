package com.privategateway.client.ui

import android.content.Context
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.semantics.SemanticsProperties
import androidx.compose.ui.test.SemanticsMatcher
import androidx.compose.ui.test.assert
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.assertIsNotEnabled
import androidx.compose.ui.test.assertIsSelected
import androidx.compose.ui.test.assertTextContains
import androidx.compose.ui.test.assertTextEquals
import androidx.compose.ui.test.hasAnyAncestor
import androidx.compose.ui.test.hasContentDescription
import androidx.compose.ui.test.hasTestTag
import androidx.compose.ui.test.hasText
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performScrollTo
import androidx.compose.ui.test.performTextClearance
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
import kotlin.test.assertTrue

/** «Профили и подписки» (доска 12 в одну колонку) без устройства. */
@RunWith(AndroidJUnit4::class)
@Config(qualifiers = "w384dp-h832dp")
class SubscriptionsScreenTest {
    @get:Rule
    val compose = createComposeRule()

    private val context = ApplicationProvider.getApplicationContext<Context>()
    private val now = System.currentTimeMillis()
    private val ok = GroupItem("ok", "Провайдер", "https://example.com/sub/****", 12, now - 60_000, now - 60_000, null, null)
    private val broken = GroupItem("broken", "Запасная", "https://example.org/s/****", 3, now - 86_400_000L * 3, now - 5 * 60_000, DownloadFailure.Status, 403)
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
        var adds = 0
        var accept = true

        val actions = SubscriptionsActions(
            settings = { settings = it },
            add = { adds++ },
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
        // Считаются все карточки, и «Мои серверы» тоже (доска 12: «Подписки · 4»).
        compose.onNodeWithText(text(R.string.subs_section, "4"), substring = true, ignoreCase = true).performScrollTo().assertExists()
        compose.onNodeWithText("https://example.com/sub/****").performScrollTo().assertIsDisplayed()
        compose.onNode(hasTestTag("group.meta") and hasAnyAncestor(hasTestTag("group.ok")))
            .assertTextContains(servers(12), substring = true)
        compose.onNodeWithTag("group.error").performScrollTo()
            .assertTextContains(text(R.string.import_download_forbidden, "403").replaceFirstChar { it.uppercaseChar() }, substring = true)
        compose.onNode(hasTestTag("group.chip") and hasAnyAncestor(hasTestTag("group.broken"))).assertTextEquals(text(R.string.subs_chip_error))
        compose.onNode(hasTestTag("group.chip") and hasAnyAncestor(hasTestTag("group.ok"))).assertTextEquals(text(R.string.subs_chip_ok))
        compose.onNodeWithText(text(R.string.subs_manual_address)).performScrollTo().assertIsDisplayed()
        compose.onNodeWithTag("group.mine").performScrollTo().assertIsDisplayed()
        compose.onNodeWithText(servers(2)).performScrollTo().assertIsDisplayed()
        compose.onNode(hasTestTag("subs.update") and hasAnyAncestor(hasTestTag("group.profile"))).assertDoesNotExist()
        // У «Моих серверов» — только корзина.
        compose.onNode(hasTestTag("subs.edit") and hasAnyAncestor(hasTestTag("group.mine"))).assertDoesNotExist()
        compose.onNode(hasTestTag("subs.update") and hasAnyAncestor(hasTestTag("group.mine"))).assertDoesNotExist()
        compose.onNode(hasTestTag("subs.remove") and hasAnyAncestor(hasTestTag("group.mine"))).assertExists()
        compose.onNode(hasContentDescription(text(R.string.subs_remove, "Провайдер"))).assertExists()
    }

    @Test
    fun `the plus in the header and the dashed button after the cards both lead to adding`() {
        val recorder = show(SubscriptionsUi(listOf(ok), manualCount = 1))
        compose.onNodeWithTag("subs.add.header").assertIsDisplayed().performClick()
        assertEquals(1, recorder.adds)
        compose.onNodeWithTag("subs.add").performScrollTo().assertIsDisplayed().performClick()
        assertEquals(2, recorder.adds)

        // Кнопка — последней, под «Моими серверами».
        val mine = compose.onNodeWithTag("group.mine").fetchSemanticsNode()
        val add = compose.onNodeWithTag("subs.add").fetchSemanticsNode()
        assertTrue(add.positionInRoot.y >= mine.positionInRoot.y + mine.size.height, "«Добавить подписку» — после всех карточек")
    }

    @Test
    fun `only via vpn without protection says that nothing updates inside its row`() {
        show(SubscriptionsUi(listOf(ok)))
        compose.onNodeWithTag("subs.vpn.blocked", useUnmergedTree = true).assertIsDisplayed()
        compose.onNode(hasTestTag("subs.vpn.blocked") and hasAnyAncestor(hasTestTag("subs.vpn")), useUnmergedTree = true).assertExists()
    }

    @Test
    fun `under protection the note is gone`() {
        show(SubscriptionsUi(listOf(ok), connected = true))
        compose.onNodeWithTag("subs.vpn.blocked", useUnmergedTree = true).assertDoesNotExist()
    }

    @Test
    fun `settings change the interval from the menu and the rules`() {
        val recorder = show(SubscriptionsUi(listOf(ok)))
        compose.onNodeWithTag("refresh").assertTextEquals(text(R.string.subs_refresh_12))
        compose.onNodeWithTag("refresh.menu").assertDoesNotExist()
        compose.onNodeWithTag("refresh").performClick()
        compose.onNodeWithTag("refresh.Every12Hours").assertIsSelected()
        compose.onNodeWithTag("refresh.Off").performClick()
        assertEquals(RefreshInterval.Off, recorder.settings.refresh)
        compose.onNodeWithTag("refresh.menu").assertDoesNotExist()
        compose.onNodeWithTag("refresh").assertTextEquals(text(R.string.subs_refresh_off))

        compose.onNodeWithTag("subs.vpn").performScrollTo().performClick()
        assertEquals(false, recorder.settings.onlyViaVpn)
        compose.onNodeWithTag("subs.vpn.blocked", useUnmergedTree = true).assertDoesNotExist()
        compose.onNodeWithTag("subs.notify").performScrollTo().performClick()
        assertEquals(false, recorder.settings.notifySelected)
    }

    @Test
    fun `update runs once and while busy only the arrow spins`() {
        val recorder = show(SubscriptionsUi(listOf(ok, broken), busy = setOf("broken")))
        compose.onNode(hasTestTag("subs.update") and hasAnyAncestor(hasTestTag("group.ok"))).performScrollTo().performClick()
        assertEquals(listOf("ok"), recorder.updated)
        compose.onNode(hasTestTag("subs.update.spin") and hasAnyAncestor(hasTestTag("group.ok")), useUnmergedTree = true).assertDoesNotExist()

        val busy = compose.onNode(hasTestTag("subs.update") and hasAnyAncestor(hasTestTag("group.broken")))
        busy.performScrollTo()
            .assertIsNotEnabled()
            .assertTextContains(text(R.string.subs_update))
            .assert(SemanticsMatcher.expectValue(SemanticsProperties.StateDescription, text(R.string.import_downloading)))
        compose.onNode(hasTestTag("subs.update.spin") and hasAnyAncestor(hasTestTag("group.broken")), useUnmergedTree = true).assertExists()
        busy.performClick()
        assertEquals(listOf("ok"), recorder.updated, "повтор, пока загружается, не считается")
    }

    @Test
    fun `edit is a sheet that refuses an address that is not http and closes after a good one`() {
        val recorder = show(SubscriptionsUi(listOf(ok)))
        compose.onNode(hasTestTag("subs.edit") and hasAnyAncestor(hasTestTag("group.ok"))).performScrollTo().performClick()
        compose.onNodeWithTag("subs.editor").assertIsDisplayed()
        compose.onNodeWithTag("subs.edit.title", useUnmergedTree = true).assertTextEquals(text(R.string.subs_edit_title, "Провайдер"))
        // Подсказка поля адреса — маска текущего, как у десктопа.
        compose.onNode(hasText("https://example.com/sub/****") and hasAnyAncestor(hasTestTag("subs.editor")), useUnmergedTree = true).assertExists()
        compose.onNodeWithTag("subs.edit.url").performTextInput("ftp://example.com/sub")
        recorder.accept = false
        compose.onNodeWithTag("subs.edit.save").performClick()
        compose.onNodeWithTag("subs.edit.bad", useUnmergedTree = true).assertIsDisplayed()
        compose.onNodeWithTag("subs.editor").assertExists()

        recorder.accept = true
        compose.onNodeWithTag("subs.edit.save").performClick()
        compose.onNodeWithTag("subs.editor").assertDoesNotExist()
        assertEquals(Triple("ok", "Провайдер", "ftp://example.com/sub"), recorder.edits.last())
    }

    @Test
    fun `edit sheet keeps the name within 64 and closes by the cross and by cancel`() {
        val recorder = show(SubscriptionsUi(listOf(ok)))
        compose.onNode(hasTestTag("subs.edit") and hasAnyAncestor(hasTestTag("group.ok"))).performScrollTo().performClick()
        compose.onNodeWithTag("subs.edit.name").performTextClearance()
        compose.onNodeWithTag("subs.edit.save").assertIsNotEnabled()
        compose.onNodeWithTag("subs.edit.name").performTextInput("x".repeat(70))
        compose.onNodeWithTag("subs.edit.close").performClick()
        compose.onNodeWithTag("subs.editor").assertDoesNotExist()

        compose.onNode(hasTestTag("subs.edit") and hasAnyAncestor(hasTestTag("group.ok"))).performScrollTo().performClick()
        compose.onNodeWithTag("subs.edit.name").performTextClearance()
        compose.onNodeWithTag("subs.edit.name").performTextInput("y".repeat(70))
        compose.onNodeWithTag("subs.edit.save").performClick()
        assertEquals(64, recorder.edits.last().second.length)

        compose.onNode(hasTestTag("subs.edit") and hasAnyAncestor(hasTestTag("group.ok"))).performScrollTo().performClick()
        compose.onNodeWithTag("subs.edit.cancel").performClick()
        compose.onNodeWithTag("subs.editor").assertDoesNotExist()
        assertEquals(1, recorder.edits.size, "крестик и «Отмена» ничего не сохраняют")
    }

    @Test
    fun `removing the group in use warns that the VPN goes off`() {
        val main = MainUi(TunnelStatus(TunnelState.Connected, serverId = "s1"), listOf(server), "s1")
        val recorder = show(SubscriptionsUi(listOf(ok, broken), connected = true), main)
        compose.onNode(hasTestTag("subs.remove") and hasAnyAncestor(hasTestTag("group.ok"))).performScrollTo().performClick()
        compose.onNodeWithTag("dialog.removeGroup").assertIsDisplayed()
        compose.onNodeWithText(text(R.string.subs_remove_title, "Провайдер")).assertIsDisplayed()
        compose.onNodeWithText(text(R.string.subs_remove_textInUse, servers(12))).assertIsDisplayed()
        compose.onNodeWithTag("dialog.removeGroup.secondary").performClick()
        compose.onNodeWithTag("dialog.removeGroup").assertDoesNotExist()
        assertEquals(emptyList(), recorder.removed, "«Отмена» ничего не удаляет")

        compose.onNode(hasTestTag("subs.remove") and hasAnyAncestor(hasTestTag("group.ok"))).performScrollTo().performClick()
        compose.onNodeWithTag("dialog.removeGroup.primary").performClick()
        assertEquals(listOf(Triple<String?, String, Boolean>("ok", "Провайдер", true)), recorder.removed)

        compose.onNode(hasTestTag("subs.remove") and hasAnyAncestor(hasTestTag("group.broken"))).performScrollTo().performClick()
        compose.onNodeWithText(text(R.string.subs_remove_text, servers(3))).assertIsDisplayed()
        compose.onNodeWithTag("dialog.removeGroup.primary").performClick()
        assertEquals(Triple<String?, String, Boolean>("broken", "Запасная", false), recorder.removed.last())
    }

    @Test
    fun `the vpn warning in the remove dialog stays while protection is in error, as on the desktop`() {
        val main = MainUi(TunnelStatus(TunnelState.Error, serverId = "s1"), listOf(server), "s1")
        val recorder = show(SubscriptionsUi(listOf(ok)), main)
        compose.onNode(hasTestTag("subs.remove") and hasAnyAncestor(hasTestTag("group.ok"))).performScrollTo().performClick()
        compose.onNodeWithText(text(R.string.subs_remove_textInUse, servers(12))).assertIsDisplayed()
        compose.onNodeWithTag("dialog.removeGroup.primary").performClick()
        assertEquals(Triple<String?, String, Boolean>("ok", "Провайдер", true), recorder.removed.last())
    }

    @Test
    fun `my servers are removed with their own dialog`() {
        val recorder = show(SubscriptionsUi(listOf(ok), manualCount = 2))
        compose.onNode(hasTestTag("subs.remove") and hasAnyAncestor(hasTestTag("group.mine"))).performScrollTo().performClick()
        compose.onNodeWithText(text(R.string.subs_remove_title, text(R.string.subs_mine_name))).assertIsDisplayed()
        compose.onNodeWithTag("dialog.removeGroup.primary").performClick()
        assertEquals(Triple<String?, String, Boolean>(null, text(R.string.subs_mine_name), false), recorder.removed.last())
    }
}

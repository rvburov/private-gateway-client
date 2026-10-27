package com.privategateway.client.ui

import android.content.Context
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.test.SemanticsMatcher
import androidx.compose.ui.test.assert
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.assertIsEnabled
import androidx.compose.ui.test.assertIsNotEnabled
import androidx.compose.ui.test.assertTextEquals
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performScrollTo
import androidx.compose.ui.test.performTextClearance
import androidx.compose.ui.test.performTextInput
import androidx.compose.ui.semantics.SemanticsProperties
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.privategateway.client.R
import com.privategateway.client.core.servers.Latency
import com.privategateway.client.core.servers.LatencyFailure
import com.privategateway.client.core.servers.ProxyProtocol
import com.privategateway.client.core.servers.ServerItem
import com.privategateway.client.core.servers.ServerListQuery
import com.privategateway.client.core.servers.ServerListTab
import com.privategateway.client.core.servers.ServerSort
import com.privategateway.client.core.servers.SubscriptionRef
import com.privategateway.client.core.servers.TransportKind
import com.privategateway.client.core.servers.TransportSecurity
import com.privategateway.client.core.vpn.TunnelState
import com.privategateway.client.core.vpn.TunnelStatus
import com.privategateway.client.ui.servers.CardActions
import com.privategateway.client.ui.servers.ServerCardScreen
import com.privategateway.client.ui.servers.ServersActions
import com.privategateway.client.ui.servers.ServersScreen
import com.privategateway.client.ui.theme.PgTheme
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.annotation.Config
import kotlin.test.assertEquals

/** «Серверы» и карточка сервера (доски 2 и 11 в одну колонку) без устройства. */
@RunWith(AndroidJUnit4::class)
@Config(qualifiers = "w384dp-h832dp")
class ServersScreenTest {
    @get:Rule
    val compose = createComposeRule()

    private val context = ApplicationProvider.getApplicationContext<Context>()
    private val provider = SubscriptionRef("s1", "Провайдер", null)
    private val nl = ServerItem("nl", "Нидерланды 01", "NL", ProxyProtocol.Vless, TransportSecurity.Reality, TransportKind.Tcp, 443, subscriptionId = "s1", latency = Latency.of(42))
    private val us = ServerItem("us", "США 01", "US", ProxyProtocol.Trojan, TransportSecurity.Tls, TransportKind.WebSocket, 443, subscriptionId = "s1", latency = Latency.of(148))
    private val tr = ServerItem("tr", "Турция 01", "TR", ProxyProtocol.Vless, TransportSecurity.Tls, TransportKind.Grpc, 443, latency = Latency.failed(LatencyFailure.NoResponse))
    private val fi = ServerItem("fi", "Финляндия 01", "FI", ProxyProtocol.Hysteria2, TransportSecurity.Tls, TransportKind.Quic, 8443)

    private fun text(id: Int, vararg args: Any) = context.getString(id, *args)

    /** Список с запросом, который меняют нажатия, — как ServersViewModel. */
    private class Harness {
        var query by mutableStateOf(ServerListQuery())
        val selected = mutableListOf<String>()
        val opened = mutableListOf<String>()

        val actions = ServersActions(
            select = { selected += it },
            open = { opened += it },
            add = {},
            refresh = {},
            search = { query = query.copy(search = it) },
            tab = { query = query.copy(tab = it) },
            toggleProtocol = { p -> query = query.copy(hiddenProtocols = if (p in query.hiddenProtocols) query.hiddenProtocols - p else query.hiddenProtocols + p) },
            sort = { query = query.copy(sort = it) },
            reset = { query = query.copy(hiddenProtocols = emptySet(), sort = ServerSort.Latency) },
        )
    }

    private fun list(ui: MainUi): Harness {
        val harness = Harness()
        compose.setContent { PgTheme { ServersScreen(ui, harness.query, harness.actions) } }
        return harness
    }

    @Test
    fun `groups go by subscription with my servers last and a tap selects`() {
        val harness = list(MainUi(servers = listOf(tr, nl, us), selectedId = "nl", subscriptions = listOf(provider)))
        compose.onNodeWithText(text(R.string.servers_group_subscription, "Провайдер", "2"), substring = true, ignoreCase = true).assertIsDisplayed()
        compose.onNodeWithText(text(R.string.servers_updated_never)).assertIsDisplayed()
        compose.onNodeWithText(text(R.string.servers_group_manual, "1"), substring = true, ignoreCase = true).assertIsDisplayed()
        compose.onNodeWithText(text(R.string.servers_tab_all, "3")).assertIsDisplayed()
        compose.onNodeWithText(text(R.string.servers_tag_selected)).assertIsDisplayed()

        compose.onNodeWithTag("server.us").performClick()
        assertEquals(listOf("us"), harness.selected)
        compose.onNodeWithTag("more.nl").performClick()
        assertEquals(listOf("nl"), harness.opened, "«⋯» открывает карточку, а не выбирает")
        assertEquals(listOf("us"), harness.selected)
    }

    @Test
    fun `latency is fast, slow, no answer or not measured`() {
        list(MainUi(servers = listOf(nl, us, tr, fi), subscriptions = listOf(provider)))
        val ms = text(R.string.unit_ms)
        compose.onNodeWithTag("latency.nl", useUnmergedTree = true).assertTextEquals("42 $ms")
        compose.onNodeWithTag("latency.us", useUnmergedTree = true).assertTextEquals("148 $ms")
        compose.onNodeWithTag("latency.tr", useUnmergedTree = true).assertTextEquals(text(R.string.servers_latency_none))
        compose.onNodeWithTag("latency.fi", useUnmergedTree = true).assertTextEquals("—")
            .assert(SemanticsMatcher.expectValue(SemanticsProperties.ContentDescription, listOf(text(R.string.android_servers_latency_never))))
    }

    @Test
    fun `search narrows the list and explains an empty result`() {
        val harness = list(MainUi(servers = listOf(nl, us, tr), subscriptions = listOf(provider)))
        compose.onNodeWithTag("servers.search").performTextInput("турц")
        assertEquals("турц", harness.query.search)
        compose.onNodeWithTag("server.tr").assertIsDisplayed()
        compose.onNodeWithTag("server.nl").assertDoesNotExist()

        compose.onNodeWithTag("servers.search").performTextClearance()
        compose.onNodeWithTag("servers.search").performTextInput("198.51.100.2")
        compose.onNodeWithText(text(R.string.servers_empty)).assertIsDisplayed()
    }

    @Test
    fun `filter hides protocols, counts what differs and resets`() {
        val harness = list(MainUi(servers = listOf(nl, us, tr), subscriptions = listOf(provider)))
        compose.onNodeWithTag("servers.filter.count", useUnmergedTree = true).assertDoesNotExist()
        compose.onNodeWithTag("servers.filter").performClick()
        compose.onNodeWithTag("filter.Hysteria2").assertDoesNotExist()
        compose.onNodeWithTag("filter.Trojan").performClick()
        assertEquals(setOf(ProxyProtocol.Trojan), harness.query.hiddenProtocols)
        compose.onNodeWithTag("server.us").assertDoesNotExist()
        compose.onNodeWithTag("servers.filter.count", useUnmergedTree = true).assertTextEquals("1")

        compose.onNodeWithTag("sort.Name").performScrollTo().performClick()
        compose.onNodeWithTag("servers.filter.count", useUnmergedTree = true).assertTextEquals("2")
        compose.onNodeWithTag("filter.reset").performScrollTo().performClick()
        compose.onNodeWithTag("servers.filter.count", useUnmergedTree = true).assertDoesNotExist()
        compose.onNodeWithTag("filter.done").performScrollTo().performClick()
        compose.onNodeWithTag("servers.filter.panel").assertDoesNotExist()
    }

    @Test
    fun `empty favorites and recent say what they are for`() {
        val harness = list(MainUi(servers = listOf(nl, tr), subscriptions = listOf(provider)))
        compose.onNodeWithTag("tab.Favorites").performClick()
        assertEquals(ServerListTab.Favorites, harness.query.tab)
        compose.onNodeWithText(text(R.string.servers_empty_favorites)).assertIsDisplayed()

        compose.onNodeWithTag("tab.Recent").performClick()
        compose.onNodeWithText(text(R.string.servers_empty_recent)).assertIsDisplayed()
    }

    @Test
    fun `server gone from its subscription is marked`() {
        list(MainUi(servers = listOf(nl.copy(missingSince = 1L)), selectedId = "nl", subscriptions = listOf(provider)))
        compose.onNodeWithText(text(R.string.card_missing)).assertIsDisplayed()
    }

    /** Карточка с записью нажатий. */
    private class Card {
        val connected = mutableListOf<String>()
        val favorites = mutableListOf<Pair<String, Boolean>>()
        val renamed = mutableListOf<Pair<String, String>>()
        val deleted = mutableListOf<String>()
        val copied = mutableListOf<String>()
        val sent = mutableListOf<String>()
        var link: String? = LINK
        var back = 0

        val actions = CardActions(
            back = { back++ },
            connect = { connected += it },
            favorite = { id, on -> favorites += id to on },
            rename = { id, name -> renamed += id to name },
            delete = { deleted += it },
            address = { "[2001:db8::1]:443" },
            share = { link },
            copy = { copied += it },
            send = { sent += it },
        )
    }

    private fun card(ui: MainUi, id: String): Card {
        val card = Card()
        compose.setContent { PgTheme { ServerCardScreen(ui, id, card.actions) } }
        return card
    }

    @Test
    fun `card shows where the server came from and its details`() {
        card(MainUi(servers = listOf(nl), subscriptions = listOf(provider)), "nl")
        compose.onNodeWithText(text(R.string.card_from_subscription, "Провайдер")).assertIsDisplayed()
        compose.onNodeWithTag("card.address").assertTextEquals("[2001:db8::1]:443")
        compose.onNodeWithTag("card.protocol").assertTextEquals("VLESS")
        compose.onNodeWithTag("card.transport").assertTextEquals("TCP")
        compose.onNodeWithTag("card.security").assertTextEquals("REALITY")
        compose.onNodeWithTag("card.core").assertTextEquals("Xray-core")
    }

    @Test
    fun `card says the phone has no core for hysteria2`() {
        card(MainUi(servers = listOf(fi)), "fi")
        compose.onNodeWithText(text(R.string.card_from_link)).assertIsDisplayed()
        compose.onNodeWithTag("card.core").assertTextEquals(text(R.string.android_card_core_none))
    }

    @Test
    fun `card toggles favorite and connects`() {
        val card = card(MainUi(servers = listOf(nl), subscriptions = listOf(provider)), "nl")
        compose.onNodeWithText(text(R.string.card_favorite_add)).assertIsDisplayed()
        compose.onNodeWithTag("card.favorite").performClick()
        assertEquals(listOf("nl" to true), card.favorites)

        compose.onNodeWithTag("card.connect").performScrollTo().performClick()
        assertEquals(listOf("nl"), card.connected)
    }

    @Test
    fun `rename saves 1 to 64 characters and nothing else`() {
        val card = card(MainUi(servers = listOf(nl)), "nl")
        val field = compose.onNodeWithTag("card.rename.text").performScrollTo()
        compose.onNodeWithTag("card.rename.save").performScrollTo().assertIsNotEnabled()

        field.performTextClearance()
        compose.onNodeWithTag("card.rename.save").assertIsNotEnabled()
        field.performTextInput("x".repeat(65))
        compose.onNodeWithTag("card.rename.save").assertIsNotEnabled()

        field.performTextClearance()
        field.performTextInput("  Мой  ")
        compose.onNodeWithTag("card.rename.save").assertIsEnabled().performClick()
        assertEquals(listOf("nl" to "Мой"), card.renamed)
    }

    @Test
    fun `deleting the server in use warns that the VPN goes off`() {
        val status = TunnelStatus(TunnelState.Connected, serverId = "nl")
        val card = card(MainUi(status, listOf(nl), "nl", listOf(provider)), "nl")
        compose.onNodeWithTag("card.inUse").performScrollTo().assertIsDisplayed()
        compose.onNodeWithTag("card.connect").assertDoesNotExist()
        compose.onNodeWithText(text(R.string.card_status_active)).assertIsDisplayed()

        compose.onNodeWithTag("card.delete").performScrollTo().performClick()
        compose.onNodeWithText(text(R.string.dialog_deleteServer_textInUse)).assertIsDisplayed()
        compose.onNodeWithText(text(R.string.dialog_cancel)).performClick()
        assertEquals(emptyList(), card.deleted)

        compose.onNodeWithTag("card.delete").performScrollTo().performClick()
        compose.onNodeWithTag("card.delete.confirm").performClick()
        assertEquals(listOf("nl"), card.deleted)
    }

    @Test
    fun `card of a deleted server closes itself`() {
        val card = card(MainUi(servers = listOf(tr)), "nl")
        compose.waitForIdle()
        assertEquals(1, card.back)
    }

    // Android 12L: снимок для «Недавних» на 13+ выключает система окна, которой в тесте нет.
    @Test
    @Config(sdk = [32])
    fun `share shows the code with a warning, copies and sends the link and hides`() {
        val card = card(MainUi(servers = listOf(nl), subscriptions = listOf(provider)), "nl")
        compose.onNodeWithTag("card.share.qr").assertDoesNotExist()
        compose.onNodeWithTag("card.share").performScrollTo().performClick()
        compose.onNodeWithTag("card.share.qr").performScrollTo().assertIsDisplayed()
        compose.onNodeWithText(text(R.string.card_share_warning)).assertExists()

        compose.onNodeWithTag("card.share.copy").performScrollTo().performClick()
        compose.onNodeWithTag("card.share.send").performScrollTo().performClick()
        assertEquals(listOf(LINK), card.copied)
        assertEquals(listOf(LINK), card.sent)

        compose.onNodeWithTag("card.share.hide").performScrollTo().performClick()
        compose.onNodeWithTag("card.share.qr").assertDoesNotExist()
    }

    @Test
    @Config(sdk = [32])
    fun `share without the key says the key is gone`() {
        val card = Card().apply { link = null }
        compose.setContent { PgTheme { ServerCardScreen(MainUi(servers = listOf(nl)), "nl", card.actions) } }
        compose.onNodeWithTag("card.share").performScrollTo().performClick()
        compose.onNodeWithTag("card.share.noKey").performScrollTo().assertIsDisplayed()
        compose.onNodeWithTag("card.share.qr").assertDoesNotExist()
    }

    private companion object {
        const val LINK = "vless://11111111-2222-3333-4444-555555555555@192.0.2.10:443?encryption=none&security=tls&type=tcp#NL"
    }
}

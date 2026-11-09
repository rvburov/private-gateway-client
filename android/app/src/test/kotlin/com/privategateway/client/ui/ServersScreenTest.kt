package com.privategateway.client.ui

import android.content.Context
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.semantics.SemanticsProperties
import androidx.compose.ui.test.SemanticsMatcher
import androidx.compose.ui.test.assert
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.assertIsEnabled
import androidx.compose.ui.test.assertIsNotEnabled
import androidx.compose.ui.test.assertIsOff
import androidx.compose.ui.test.assertIsOn
import androidx.compose.ui.test.assertIsSelected
import androidx.compose.ui.test.assertTextEquals
import androidx.compose.ui.test.click
import androidx.compose.ui.test.hasText
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performScrollTo
import androidx.compose.ui.test.performScrollToNode
import androidx.compose.ui.test.performTextClearance
import androidx.compose.ui.test.performTextInput
import androidx.compose.ui.test.performTouchInput
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
import com.privategateway.client.ui.theme.LocalPgToast
import com.privategateway.client.ui.theme.PgTheme
import com.privategateway.client.ui.theme.PgToastState
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.annotation.Config
import kotlin.test.assertEquals

/** «Серверы» и карточка сервера — нижний лист поверх списка (доски 2 и 11) без устройства. */
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
        var refreshed = 0

        val actions = ServersActions(
            select = { selected += it },
            open = { opened += it },
            add = {},
            refresh = { refreshed++ },
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
        compose.onNodeWithTag("server.tag.nl", useUnmergedTree = true).assertTextEquals(text(R.string.servers_tag_selected))
        compose.onNodeWithTag("server.nl").assertIsSelected()

        compose.onNodeWithTag("server.us").performClick()
        assertEquals(listOf("us"), harness.selected)
        compose.onNodeWithTag("more.nl").performClick()
        assertEquals(listOf("nl"), harness.opened, "«⋯» открывает карточку, а не выбирает")
        assertEquals(listOf("us"), harness.selected)
    }

    @Test
    fun `a row says active only while protection is up through it, connecting says selected`() {
        var ui by mutableStateOf(MainUi(TunnelStatus(TunnelState.Connecting, serverId = "nl"), listOf(nl, us), "nl", listOf(provider)))
        compose.setContent { PgTheme { ServersScreen(ui, ServerListQuery(), Harness().actions) } }
        compose.onNodeWithTag("server.tag.nl", useUnmergedTree = true).assertTextEquals(text(R.string.servers_tag_selected))
        compose.onNodeWithTag("server.tag.us", useUnmergedTree = true).assertDoesNotExist()

        ui = ui.copy(status = TunnelStatus(TunnelState.Reconnecting, serverId = "nl"))
        compose.onNodeWithTag("server.tag.nl", useUnmergedTree = true).assertTextEquals(text(R.string.servers_tag_selected))

        ui = ui.copy(status = TunnelStatus(TunnelState.Connected, serverId = "nl"))
        compose.onNodeWithTag("server.tag.nl", useUnmergedTree = true).assertTextEquals(text(R.string.servers_tag_active))
    }

    @Test
    fun `a row shows the protocol, no star and no missing note`() {
        list(MainUi(servers = listOf(nl.copy(favorite = true, missingSince = 1L)), selectedId = "nl", subscriptions = listOf(provider)))
        compose.onNodeWithTag("server.meta.nl", useUnmergedTree = true).assertTextEquals("VLESS · REALITY")
        // Звезда и «Пропал из подписки» — в карточке, не в строке (доска 2).
        compose.onNodeWithText(text(R.string.card_missing)).assertDoesNotExist()
        compose.onNodeWithText(text(R.string.server_port, "443"), substring = true).assertDoesNotExist()
    }

    @Test
    fun `latency is fast, slow, no answer or not measured`() {
        list(MainUi(servers = listOf(nl, us, tr, fi), subscriptions = listOf(provider)))
        val ms = text(R.string.unit_ms)
        compose.onNodeWithTag("latency.nl", useUnmergedTree = true).assertTextEquals("42 $ms")
        compose.onNodeWithTag("latency.us", useUnmergedTree = true).assertTextEquals("148 $ms")
        compose.onNodeWithTag("latency.tr", useUnmergedTree = true).assertTextEquals(text(R.string.servers_latency_none))
            .assert(SemanticsMatcher.expectValue(SemanticsProperties.ContentDescription, listOf(text(R.string.servers_latency_noneHint))))
        compose.onNodeWithTag("latency.fi", useUnmergedTree = true).assertTextEquals("—")
            .assert(SemanticsMatcher.expectValue(SemanticsProperties.ContentDescription, listOf(text(R.string.android_servers_latency_never))))
    }

    @Test
    fun `search, filter and tabs are there without servers and refresh spins without going grey`() {
        var ui by mutableStateOf(MainUi())
        val harness = Harness()
        compose.setContent { PgTheme { ServersScreen(ui, harness.query, harness.actions) } }
        compose.onNodeWithTag("servers.search").assertIsDisplayed()
        compose.onNodeWithTag("servers.filter").assertIsDisplayed()
        compose.onNodeWithText(text(R.string.servers_tab_all, "0")).assertIsDisplayed()
        compose.onNodeWithTag("servers.empty").assertTextEquals(text(R.string.servers_none))

        compose.onNodeWithTag("servers.refresh.spin", useUnmergedTree = true).assertDoesNotExist()
        compose.onNodeWithTag("servers.refresh").assertIsEnabled().performClick()
        assertEquals(1, harness.refreshed)

        ui = MainUi(servers = listOf(nl), measuring = true)
        compose.onNodeWithTag("servers.refresh.spin", useUnmergedTree = true).assertExists()
        // Во время замера кнопка не гаснет: повтор отсекает модель.
        compose.onNodeWithTag("servers.refresh").assertIsEnabled().performClick()
        assertEquals(2, harness.refreshed)

        // Без серверов в фильтре нет протоколов — нет и их заголовка.
        ui = MainUi()
        compose.onNodeWithTag("servers.filter").performClick()
        compose.onNodeWithText(text(R.string.servers_filter_protocols), ignoreCase = true).assertDoesNotExist()
        compose.onNodeWithTag("sort.Latency").assertIsSelected()
    }

    @Test
    fun `header, search and tabs stay while the list scrolls`() {
        val many = (1..24).map { nl.copy(id = "s$it", linkName = "Сервер $it", latency = Latency.of(10 + it)) }
        list(MainUi(servers = many, subscriptions = listOf(provider)))
        // Подпись — в самом конце списка, ниже 24 строк: прокрутка до нее уводит вверх всю прокручиваемую часть.
        compose.onNodeWithTag("servers.list").performScrollToNode(hasText(text(R.string.servers_note)))
        compose.onNodeWithText(text(R.string.servers_note)).assertIsDisplayed()
        compose.onNodeWithTag("servers.search").assertIsDisplayed()
        compose.onNodeWithTag("tab.All").assertIsDisplayed()
        compose.onNodeWithTag("servers.refresh").assertIsDisplayed()
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
        compose.onNodeWithTag("servers.empty").assertTextEquals(text(R.string.servers_empty))
    }

    @Test
    fun `filter hides protocols with checks, counts what differs and resets`() {
        val harness = list(MainUi(servers = listOf(nl, us, tr), subscriptions = listOf(provider)))
        compose.onNodeWithTag("servers.filter.count", useUnmergedTree = true).assertDoesNotExist()
        compose.onNodeWithTag("servers.filter").performClick()
        compose.onNodeWithTag("filter.Hysteria2").assertDoesNotExist()
        compose.onNodeWithTag("filter.Vless").assertIsOn()
        compose.onNodeWithTag("filter.Trojan").assertIsOn().performClick()
        assertEquals(setOf(ProxyProtocol.Trojan), harness.query.hiddenProtocols)
        compose.onNodeWithTag("filter.Trojan").assertIsOff()
        compose.onNodeWithTag("server.us").assertDoesNotExist()
        compose.onNodeWithTag("servers.filter.count", useUnmergedTree = true).assertTextEquals("1")

        compose.onNodeWithTag("sort.Name").performClick()
        assertEquals(ServerSort.Name, harness.query.sort)
        compose.onNodeWithTag("sort.Name").assertIsSelected()
        compose.onNodeWithTag("servers.filter.count", useUnmergedTree = true).assertTextEquals("2")
        compose.onNodeWithTag("filter.reset").performClick()
        compose.onNodeWithTag("servers.filter.count", useUnmergedTree = true).assertDoesNotExist()
        compose.onNodeWithTag("filter.done").performClick()
        compose.onNodeWithTag("servers.filter.panel").assertDoesNotExist()
    }

    @Test
    fun `filter panel lies over the tabs and closes by a tap beside it, not on itself`() {
        list(MainUi(servers = listOf(nl, us), subscriptions = listOf(provider)))
        compose.onNodeWithTag("servers.filter").performClick()
        compose.onNodeWithTag("servers.filter.panel").assertIsDisplayed()
        // Вкладки и список остаются на месте: панель ложится поверх, а не сдвигает их.
        compose.onNodeWithTag("tab.All").assertExists()

        compose.onNodeWithTag("servers.filter.panel").performTouchInput { click(Offset(width / 2f, 4f)) }
        compose.onNodeWithTag("servers.filter.panel").assertIsDisplayed()
        compose.onNodeWithTag("servers.filter.outside").performTouchInput { click(Offset(width / 2f, height - 4f)) }
        compose.onNodeWithTag("servers.filter.panel").assertDoesNotExist()
    }

    @Test
    fun `empty favorites and recent say what they are for`() {
        val harness = list(MainUi(servers = listOf(nl, tr), subscriptions = listOf(provider)))
        compose.onNodeWithTag("tab.Favorites").performClick()
        assertEquals(ServerListTab.Favorites, harness.query.tab)
        compose.onNodeWithTag("servers.empty").assertTextEquals(text(R.string.servers_empty_favorites))

        compose.onNodeWithTag("tab.Recent").performClick()
        compose.onNodeWithTag("servers.empty").assertTextEquals(text(R.string.servers_empty_recent))
    }

    /** Карточка с записью нажатий. */
    private class Card {
        val connected = mutableListOf<String>()
        val favorites = mutableListOf<Pair<String, Boolean>>()
        val renamed = mutableListOf<Pair<String, String>>()
        val deleted = mutableListOf<String>()
        val copied = mutableListOf<String>()
        var link: String? = LINK
        var exit: String? = null
        var closed = 0

        val actions = CardActions(
            close = { closed++ },
            connect = { connected += it },
            favorite = { id, on -> favorites += id to on },
            rename = { id, name -> renamed += id to name },
            delete = { deleted += it },
            address = { "[2001:db8::1]:443" },
            share = { link },
            copy = { copied += it },
            exit = { exit },
        )
    }

    private fun card(ui: MainUi, id: String, card: Card = Card()): Card {
        val toast = PgToastState()
        compose.setContent {
            PgTheme {
                CompositionLocalProvider(LocalPgToast provides toast) { ServerCardScreen(ui, id, card.actions) }
            }
        }
        return card
    }

    @Test
    fun `card shows the server, where it came from and its details`() {
        card(MainUi(servers = listOf(nl), subscriptions = listOf(provider)), "nl")
        compose.onNodeWithTag("card.title").assertTextEquals(nl.displayName)
        compose.onNodeWithTag("card.subtitle").assertTextEquals(text(R.string.card_from_subscription, "Провайдер"))
        compose.onNodeWithTag("card.latency", useUnmergedTree = true).assertTextEquals("42 " + text(R.string.unit_ms))
        compose.onNodeWithTag("card.status").assertDoesNotExist()
        compose.onNodeWithTag("card.address").assertTextEquals("[2001:db8::1]:443")
        compose.onNodeWithTag("card.exitIp").assertTextEquals(text(R.string.card_exitIp_unknown))
        compose.onNodeWithTag("card.protocol").assertTextEquals("VLESS")
        compose.onNodeWithTag("card.transport").assertTextEquals("TCP")
        compose.onNodeWithTag("card.security").assertTextEquals("REALITY")
        compose.onNodeWithTag("card.core").assertTextEquals("Xray-core")
    }

    @Test
    fun `card shows the exit ip seen through the server`() {
        // Решения 583 и пользователя 2026-11-08: сервер «VPS + прокси» — телефон подключается к VPS, а сайты видят выход за ним.
        card(MainUi(servers = listOf(nl), subscriptions = listOf(provider)), "nl", Card().apply { exit = "203.0.113.90" })
        compose.onNodeWithTag("card.address").assertTextEquals("[2001:db8::1]:443")
        compose.onNodeWithTag("card.exitIp").assertTextEquals("203.0.113.90")
    }

    @Test
    fun `card of a server gone from its subscription says so instead of the source`() {
        card(MainUi(servers = listOf(nl.copy(missingSince = 1L)), selectedId = "nl", subscriptions = listOf(provider)), "nl")
        compose.onNodeWithTag("card.subtitle").assertTextEquals(text(R.string.card_missing))
        compose.onNodeWithTag("card.status", useUnmergedTree = true).assertExists()
        compose.onNodeWithText(text(R.string.card_status_selected)).assertIsDisplayed()
    }

    @Test
    fun `card latency says no answer in words of the card`() {
        card(MainUi(servers = listOf(tr)), "tr")
        compose.onNodeWithTag("card.subtitle").assertTextEquals(text(R.string.card_from_link))
        compose.onNodeWithTag("card.latency", useUnmergedTree = true).assertTextEquals(text(R.string.card_latency_none))
    }

    @Test
    fun `card says the phone has no core for hysteria2 and does not connect to it`() {
        val card = card(MainUi(servers = listOf(fi)), "fi")
        compose.onNodeWithTag("card.core").assertTextEquals(text(R.string.android_card_core_none))
        compose.onNodeWithTag("card.latency", useUnmergedTree = true).assertTextEquals("—")
        compose.onNodeWithTag("card.connect").assertIsNotEnabled().performClick()
        assertEquals(emptyList(), card.connected)
    }

    @Test
    fun `card toggles favorite by the star, connects and closes`() {
        val card = card(MainUi(servers = listOf(nl), subscriptions = listOf(provider)), "nl")
        compose.onNodeWithContentDescription(text(R.string.card_favorite_add)).assertIsDisplayed()
        compose.onNodeWithTag("card.favorite").performClick()
        assertEquals(listOf("nl" to true), card.favorites)

        compose.onNodeWithTag("card.connect").assertIsEnabled().performClick()
        assertEquals(listOf("nl"), card.connected)
        assertEquals(1, card.closed, "«Подключиться» закрывает лист")
    }

    @Test
    fun `card closes by the cross and by a tap beside the sheet`() {
        val card = card(MainUi(servers = listOf(nl.copy(favorite = true))), "nl")
        compose.onNodeWithContentDescription(text(R.string.card_favorite_remove)).assertIsDisplayed()
        compose.onNodeWithTag("card.close").performClick()
        assertEquals(1, card.closed)

        compose.onNodeWithTag("card.scrim").performTouchInput { click(Offset(width / 2f, 4f)) }
        assertEquals(2, card.closed)
    }

    @Test
    fun `rename opens instead of the details and saves 1 to 64 characters`() {
        val card = card(MainUi(servers = listOf(nl)), "nl")
        compose.onNodeWithTag("card.rename.text").assertDoesNotExist()
        compose.onNodeWithTag("card.rename").performClick()
        compose.onNodeWithTag("card.details").assertDoesNotExist()
        val field = compose.onNodeWithTag("card.rename.text").performScrollTo()
        compose.onNodeWithTag("card.rename.save").performScrollTo().assertIsNotEnabled()

        field.performTextClearance()
        compose.onNodeWithTag("card.rename.save").assertIsNotEnabled()
        field.performTextInput("x".repeat(65))
        compose.onNodeWithTag("card.rename.save").assertIsNotEnabled()

        // «Отмена» возвращает сведения, ничего не сохраняя.
        compose.onNodeWithTag("card.rename.cancel").performScrollTo().performClick()
        compose.onNodeWithTag("card.details").assertExists()
        assertEquals(emptyList(), card.renamed)

        compose.onNodeWithTag("card.rename").performClick()
        compose.onNodeWithTag("card.rename.text").performScrollTo().performTextClearance()
        compose.onNodeWithTag("card.rename.text").performTextInput("  Мой  ")
        compose.onNodeWithTag("card.rename.save").performScrollTo().assertIsEnabled().performClick()
        assertEquals(listOf("nl" to "Мой"), card.renamed)
        compose.onNodeWithTag("card.details").assertExists()
    }

    @Test
    fun `deleting the server in use warns that the VPN goes off`() {
        val status = TunnelStatus(TunnelState.Connected, serverId = "nl")
        val card = card(MainUi(status, listOf(nl), "nl", listOf(provider)), "nl")
        compose.onNodeWithTag("card.inUse").assertIsDisplayed()
        compose.onNodeWithTag("card.connect").assertDoesNotExist()
        compose.onNodeWithText(text(R.string.card_status_active)).assertIsDisplayed()

        compose.onNodeWithTag("card.delete").performClick()
        compose.onNodeWithText(text(R.string.dialog_deleteServer_title, nl.displayName)).assertIsDisplayed()
        compose.onNodeWithText(text(R.string.dialog_deleteServer_textInUse)).assertIsDisplayed()
        compose.onNodeWithTag("dialog.deleteServer.secondary").performClick()
        compose.onNodeWithTag("dialog.deleteServer").assertDoesNotExist()
        assertEquals(emptyList(), card.deleted)

        compose.onNodeWithTag("card.delete").performClick()
        compose.onNodeWithTag("dialog.deleteServer.primary").performClick()
        assertEquals(listOf("nl"), card.deleted)
    }

    @Test
    fun `deleting a server not in use just says it leaves the list`() {
        card(MainUi(servers = listOf(nl), selectedId = "nl"), "nl")
        compose.onNodeWithTag("card.delete").performClick()
        compose.onNodeWithText(text(R.string.dialog_deleteServer_text)).assertIsDisplayed()
    }

    @Test
    fun `card of a deleted server closes itself`() {
        val card = card(MainUi(servers = listOf(tr)), "nl")
        compose.waitForIdle()
        assertEquals(1, card.closed)
    }

    // Android 12L: снимок для «Недавних» на 13+ выключает система окна, которой в тесте нет.
    @Test
    @Config(sdk = [32])
    fun `share shows the code instead of the details, copies the link and hides`() {
        val card = card(MainUi(servers = listOf(nl), subscriptions = listOf(provider)), "nl")
        compose.onNodeWithTag("card.share.qr").assertDoesNotExist()
        compose.onNodeWithTag("card.share").assertTextEquals(text(R.string.card_share)).performClick()
        compose.onNodeWithTag("card.share.qr").performScrollTo().assertIsDisplayed()
        compose.onNodeWithTag("card.details").assertDoesNotExist()
        compose.onNodeWithTag("card.share.warning").assertTextEquals(text(R.string.card_share_warning))

        compose.onNodeWithTag("card.share.copy").performScrollTo().performClick()
        assertEquals(listOf(LINK), card.copied)

        compose.onNodeWithTag("card.share.hide").assertTextEquals(text(R.string.card_share_hide)).performClick()
        compose.onNodeWithTag("card.share.qr").assertDoesNotExist()
        compose.onNodeWithTag("card.details").assertExists()
    }

    @Test
    @Config(sdk = [32])
    fun `share without the key says so in a toast and the tile goes unavailable`() {
        card(MainUi(servers = listOf(nl)), "nl", Card().apply { link = null })
        compose.onNodeWithTag("card.share").assertIsEnabled().performClick()
        compose.onNodeWithTag("toast").assertTextEquals(text(R.string.card_share_noKey))
        compose.onNodeWithTag("card.share.qr").assertDoesNotExist()
        compose.onNodeWithTag("card.share").assertIsNotEnabled()
        compose.onNodeWithText(text(R.string.card_share_noKey)).assertIsDisplayed()
    }

    private companion object {
        const val LINK = "vless://11111111-2222-3333-4444-555555555555@192.0.2.10:443?encryption=none&security=tls&type=tcp#NL"
    }
}

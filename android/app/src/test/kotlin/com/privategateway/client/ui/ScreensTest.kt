package com.privategateway.client.ui

import android.content.Context
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.assertIsEnabled
import androidx.compose.ui.test.assertIsNotEnabled
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performScrollTo
import androidx.compose.ui.test.performTextInput
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.privategateway.client.Fixtures
import com.privategateway.client.R
import com.privategateway.client.core.links.ImportParser
import com.privategateway.client.core.subscriptions.FailureReason
import com.privategateway.client.core.servers.ProxyProtocol
import com.privategateway.client.core.servers.ServerItem
import com.privategateway.client.core.servers.TransportKind
import com.privategateway.client.core.servers.TransportSecurity
import com.privategateway.client.ui.home.HomeScreen
import com.privategateway.client.ui.servers.ImportScreen
import com.privategateway.client.ui.theme.PgTheme
import com.privategateway.client.vpn.TunnelState
import com.privategateway.client.vpn.TunnelStatus
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.annotation.Config
import kotlin.test.assertEquals

/** Экраны без устройства (Robolectric): что видно в каждом состоянии и что делают нажатия. Экран — как в макете (доска 25). */
@RunWith(AndroidJUnit4::class)
@Config(qualifiers = "w384dp-h832dp")
class ScreensTest {
    @get:Rule
    val compose = createComposeRule()

    private val context = ApplicationProvider.getApplicationContext<Context>()
    private val nl = ServerItem("nl", "Нидерланды 01", "NL", ProxyProtocol.Vless, TransportSecurity.Reality, TransportKind.Tcp, 443)
    private val plain = ServerItem("plain", "Без TLS", "FI", ProxyProtocol.Vless, TransportSecurity.None, TransportKind.WebSocket, 80)

    private fun text(id: Int, vararg args: Any) = context.getString(id, *args)

    @Test
    fun `home shows protection off with the selected server`() {
        compose.setContent { PgTheme { HomeScreen(MainUi(servers = listOf(nl), selectedId = "nl"), {}, {}, {}) } }
        compose.onNodeWithText(text(R.string.status_off_title)).assertIsDisplayed()
        compose.onNodeWithText(text(R.string.status_off_sub)).assertIsDisplayed()
        compose.onNodeWithText(text(R.string.check_all_off)).assertIsDisplayed()
        compose.onNodeWithText(text(R.string.android_check_ks_system)).assertIsDisplayed()
        compose.onNodeWithText(nl.displayName).assertIsDisplayed()
        compose.onNodeWithText("VLESS · REALITY · " + text(R.string.server_port, "443")).assertIsDisplayed()
    }

    @Test
    fun `home connected says whether traffic is encrypted`() {
        val status = TunnelStatus(TunnelState.Connected, serverId = "nl", since = 0L, lockdown = true)
        var ui by mutableStateOf(MainUi(status, listOf(nl, plain), "nl"))
        compose.setContent { PgTheme { HomeScreen(ui, {}, {}, {}) } }
        compose.onNodeWithText(text(R.string.status_connected_title)).assertIsDisplayed()
        compose.onNodeWithText(text(R.string.status_connected_sub)).assertIsDisplayed()
        compose.onNodeWithText(text(R.string.check_ks_ok)).assertIsDisplayed()
        compose.onNodeWithText(text(R.string.status_online)).assertIsDisplayed()

        ui = MainUi(status.copy(serverId = "plain"), listOf(nl, plain), "nl")
        compose.onNodeWithText(text(R.string.status_connected_subPlain)).assertIsDisplayed()
    }

    @Test
    fun `home error names the code and its meaning`() {
        val status = TunnelStatus(TunnelState.Error, error = "PGC-NET-010")
        compose.setContent { PgTheme { HomeScreen(MainUi(status, listOf(nl), "nl"), {}, {}, {}) } }
        compose.onNodeWithText(text(R.string.status_error_sub_direct, "PGC-NET-010")).assertIsDisplayed()
        compose.onNodeWithText(text(R.string.error_PGC_NET_010)).assertIsDisplayed()
    }

    @Test
    fun `home without servers leads to adding one`() {
        var added = 0
        compose.setContent { PgTheme { HomeScreen(MainUi(), {}, {}, { added++ }) } }
        compose.onNodeWithText(text(R.string.servers_none)).performScrollTo().performClick()
        compose.onNodeWithTag("add").performScrollTo().performClick()
        assertEquals(2, added)
    }

    @Test
    fun `home with servers but none selected leads to the list, not to adding`() {
        // Выбранный сервер удален, другие остались: «серверов нет» и «добавьте первый» были бы неправдой.
        var listed = 0
        var added = 0
        compose.setContent { PgTheme { HomeScreen(MainUi(servers = listOf(nl, plain)), { listed++ }, {}, { added++ }) } }
        compose.onNodeWithText(text(R.string.servers_none)).assertDoesNotExist()
        compose.onNodeWithText(text(R.string.server_addFirst)).assertDoesNotExist()
        compose.onNodeWithTag("server.choose").performScrollTo().assertIsDisplayed().performClick()
        assertEquals(1, listed)
        assertEquals(0, added)
    }

    @Test
    fun `import previews the text and offers to add what parsed`() {
        val link = Fixtures.testdata.resolve("links/vless/reality-vision.txt").readText().trim()
        val input = link + "\nnot-a-link\n"
        val previewed = mutableListOf<String>()
        var state by mutableStateOf(ImportUi())
        var added = 0
        compose.setContent { PgTheme { ImportScreen(state, { null }, { state = state.copy(text = it) }, { previewed += it }, { state = state.copy(name = it) }, { added++ }, {}) } }

        compose.onNodeWithTag("import.check").assertIsNotEnabled()
        compose.onNodeWithTag("import.clipboard").performClick()
        compose.onNodeWithText(text(R.string.import_empty_clipboard)).assertIsDisplayed()
        compose.onNodeWithTag("import.text").performTextInput(input)
        compose.onNodeWithTag("import.check").assertIsEnabled().performClick()
        assertEquals(listOf(input), previewed)

        assertEquals(input, state.text, "текст живет в модели окна, а не в сохраненном состоянии")
        state = state.copy(batch = ImportParser.parse(input))
        compose.onNodeWithText(text(R.string.import_line, "2", text(R.string.import_error_bad_link))).performScrollTo().assertIsDisplayed()
        compose.onNodeWithTag("import.add").performScrollTo().assertIsDisplayed().performClick()
        assertEquals(1, added)

        state = state.copy(adding = true)
        compose.onNodeWithTag("import.add").performScrollTo().assertIsNotEnabled()

        state = state.copy(added = 1)
        compose.onNodeWithTag("import.done").performScrollTo().assertIsDisplayed()
    }

    @Test
    fun `subscription download shows progress, the reason it failed and the http warning`() {
        val link = Fixtures.testdata.resolve("links/vless/reality-vision.txt").readText().trim()
        var state by mutableStateOf(ImportUi(text = "https://example.com/sub/abcdef", downloading = true))
        compose.setContent { PgTheme { ImportScreen(state, { null }, {}, {}, { state = state.copy(name = it) }, {}, {}) } }
        compose.onNodeWithTag("import.downloading").assertIsDisplayed()
        compose.onNodeWithTag("import.check").assertIsNotEnabled()

        state = ImportUi(text = state.text, failure = FailureReason.Forbidden(403))
        compose.onNodeWithText(text(R.string.import_download_failed, text(R.string.import_download_forbidden, "403"))).assertIsDisplayed()

        state = ImportUi(text = state.text, batch = ImportParser.parse(link), insecure = true, name = "example.com")
        compose.onNodeWithTag("import.insecure").performScrollTo().assertIsDisplayed()
        compose.onNodeWithTag("import.name").performScrollTo().assertIsDisplayed()
        compose.onNodeWithText("example.com").assertIsDisplayed()
    }

    @Test
    fun `power button and tabs call back`() {
        var power = 0
        val tabs = mutableListOf<Tab>()
        compose.setContent { PgTheme { Island(Tab.Home, TunnelState.Off, hasServer = true, onTab = { tabs += it }, onPower = { power++ }) } }
        compose.onNodeWithText(text(R.string.power_label_off)).assertIsDisplayed()
        compose.onNodeWithTag("power").performClick()
        compose.onNodeWithTag("nav.servers").performClick()
        compose.onNodeWithTag("nav.check").performClick()
        assertEquals(1, power)
        assertEquals(listOf(Tab.Servers, Tab.Check), tabs)
    }
}

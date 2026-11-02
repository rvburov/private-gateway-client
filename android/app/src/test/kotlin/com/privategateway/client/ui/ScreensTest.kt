package com.privategateway.client.ui

import android.content.Context
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.assertIsEnabled
import androidx.compose.ui.test.assertIsNotEnabled
import androidx.compose.ui.test.assertIsNotSelected
import androidx.compose.ui.test.assertIsSelected
import androidx.compose.ui.test.assertTextContains
import androidx.compose.ui.test.assertTextEquals
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performScrollTo
import androidx.compose.ui.test.performTextInput
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.privategateway.client.BuildConfig
import com.privategateway.client.Fixtures
import com.privategateway.client.R
import com.privategateway.client.core.inspect.PhoneNetworkKind
import com.privategateway.client.core.links.ImportParser
import com.privategateway.client.core.subscriptions.FailureReason
import com.privategateway.client.core.servers.Latency
import com.privategateway.client.core.servers.LatencyFailure
import com.privategateway.client.core.servers.ProxyProtocol
import com.privategateway.client.core.servers.ServerItem
import com.privategateway.client.core.servers.TransportKind
import com.privategateway.client.core.servers.TransportSecurity
import com.privategateway.client.core.vpn.TunnelState
import com.privategateway.client.core.vpn.TunnelStatus
import com.privategateway.client.ui.check.CheckScreen
import com.privategateway.client.ui.home.HomeActions
import com.privategateway.client.ui.home.HomeScreen
import com.privategateway.client.ui.servers.ImportScreen
import com.privategateway.client.ui.theme.LocalPgToast
import com.privategateway.client.ui.theme.PgTheme
import com.privategateway.client.ui.theme.PgToast
import com.privategateway.client.ui.theme.PgToastState
import com.privategateway.client.vpn.TrafficSpeed
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.annotation.Config
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.Locale
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

    /** Главная с подсказками окна сверху — как в AppScreen. */
    @Composable
    private fun Home(
        ui: MainUi,
        onServers: () -> Unit = {},
        onCheck: () -> Unit = {},
        onAdd: () -> Unit = {},
        onSelect: (String) -> Unit = {},
        onRefresh: () -> Unit = {},
        speed: TrafficSpeed? = null,
        actions: HomeActions = HomeActions(),
    ) {
        PgTheme {
            CompositionLocalProvider(LocalPgToast provides toast) {
                Box(Modifier.fillMaxSize()) {
                    HomeScreen(ui, onServers, onCheck, onAdd, onSelect, onRefresh, speed, actions)
                    PgToast(toast, Modifier.align(Alignment.TopCenter))
                }
            }
        }
    }

    private val toast = PgToastState()

    @Test
    fun `home shows protection off with the selected server`() {
        compose.setContent { Home(MainUi(servers = listOf(nl), selectedId = "nl")) }
        compose.onNodeWithTag("status.title").assertTextEquals(text(R.string.status_off_title))
        compose.onNodeWithText(text(R.string.status_off_sub)).assertIsDisplayed()
        compose.onNodeWithText(text(R.string.check_all_off)).assertIsDisplayed()
        // Сеть не закрыта системой — трафик идет мимо, блок при обрыве «выключен» (поправка карты, доска 26).
        compose.onNodeWithText(text(R.string.check_ks_off)).assertIsDisplayed()
        compose.onNodeWithText(text(R.string.android_check_ks_system)).assertDoesNotExist()
        compose.onNodeWithTag("status.checked").assertTextEquals(text(R.string.checked_off))
        compose.onNodeWithTag("status.verify").assertDoesNotExist()
        compose.onNodeWithTag("server.title").performScrollTo().assertTextEquals("NL · " + nl.displayName)
        compose.onNodeWithText("VLESS · REALITY · " + text(R.string.server_port, "443")).assertIsDisplayed()
        compose.onNodeWithTag("ip.address").assertTextEquals("—")
        compose.onNodeWithTag("ip.meta").assertTextEquals(text(R.string.ip_meta_off))
        compose.onNodeWithTag("ip.copy").assertIsNotEnabled()
    }

    @Test
    fun `home header has the shield with a mark and the full version`() {
        compose.setContent { Home(MainUi()) }
        compose.onNodeWithTag("logo").assertIsDisplayed()
        compose.onNodeWithTag("version").assertTextEquals(BuildConfig.VERSION_NAME)
        assertEquals(3, BuildConfig.VERSION_NAME.split('.').size, "версия целиком, а не «0.1»")
    }

    @Test
    fun `home connected says whether traffic is encrypted`() {
        val status = TunnelStatus(TunnelState.Connected, serverId = "nl", since = 0L, lockdown = true)
        var ui by mutableStateOf(MainUi(status, listOf(nl, plain), "nl"))
        compose.setContent { Home(ui) }
        compose.onNodeWithTag("status.title").assertTextEquals(text(R.string.status_connected_title))
        compose.onNodeWithText(text(R.string.status_connected_sub)).assertIsDisplayed()
        compose.onNodeWithText(text(R.string.check_ks_ok)).assertIsDisplayed()
        compose.onNodeWithText(text(R.string.status_online)).assertIsDisplayed()

        ui = MainUi(status.copy(serverId = "plain"), listOf(nl, plain), "nl")
        compose.onNodeWithText(text(R.string.status_connected_subPlain)).assertIsDisplayed()
    }

    @Test
    fun `home shows the network the phone goes out through`() {
        var ui by mutableStateOf(MainUi(network = PhoneNetworkKind.Wifi))
        compose.setContent { Home(ui) }
        compose.onNodeWithTag("status.network").assertTextEquals(text(R.string.network_wifiNoName))

        ui = ui.copy(network = PhoneNetworkKind.Cellular)
        compose.onNodeWithTag("status.network").assertTextEquals(text(R.string.network_mobile))

        ui = ui.copy(network = null)
        compose.onNodeWithTag("status.network").assertTextEquals(text(R.string.network_none))
    }

    @Test
    fun `connecting pauses the internet, dns and ipv6 are already green, and sites see nothing yet`() {
        val ip = WebIp(direct = "198.51.100.7")
        var ui by mutableStateOf(MainUi(TunnelStatus(TunnelState.Connecting, serverId = "nl"), listOf(nl), "nl", ip = ip))
        compose.setContent { Home(ui) }
        compose.onNodeWithTag("status.title").assertTextEquals(text(R.string.status_connecting_title))
        compose.onNodeWithTag("status.sub").assertTextEquals(text(R.string.status_connecting_sub))
        compose.onNodeWithText(text(R.string.check_all_waiting)).assertIsDisplayed()
        compose.onNodeWithText(text(R.string.check_dns_ok)).assertIsDisplayed()
        compose.onNodeWithText(text(R.string.check_ipv6_ok)).assertIsDisplayed()
        compose.onNodeWithTag("status.checked").assertTextEquals(text(R.string.checked_connecting))
        compose.onNodeWithTag("status.verify").assertDoesNotExist()
        compose.onNodeWithTag("ip.address").assertTextEquals("—")
        compose.onNodeWithTag("ip.meta").assertTextEquals(text(R.string.ip_meta_waiting))
        compose.onNodeWithTag("ip.real").assertTextContains("198.51.100.7", substring = true).assertTextContains(text(R.string.ip_real_hidden), substring = true)
        compose.onNodeWithTag("ip.copy").assertIsNotEnabled()

        ui = ui.copy(status = TunnelStatus(TunnelState.Reconnecting, serverId = "nl", attempt = 2, lockdown = true))
        compose.onNodeWithTag("status.sub").assertTextEquals(text(R.string.status_reconnecting_sub, "2"))
        compose.onNodeWithTag("status.checked").assertTextEquals(text(R.string.checked_reconnecting))
        compose.onNodeWithText(text(R.string.check_ks_ok)).assertIsDisplayed()
        // Паузы для входа в Wi-Fi у телефона нет — и кнопки «Вход в Wi-Fi» тоже.
        compose.onNodeWithText(text(R.string.action_captive)).assertDoesNotExist()
        compose.onNodeWithTag("status.retry").assertDoesNotExist()
    }

    @Test
    fun `home without vpn says android closed the internet`() {
        // «Блокировать подключения без VPN»: без туннеля трафик не идет напрямую — Android его остановил (решение 445).
        val settings = mutableListOf<String>()
        val retried = mutableListOf<String>()
        var ui by mutableStateOf(MainUi(servers = listOf(nl), selectedId = "nl", blocked = true, ip = WebIp(direct = "198.51.100.7")))
        compose.setContent { Home(ui, actions = HomeActions(retry = { retried += "retry" }, vpnSettings = { settings += "vpn" })) }
        compose.onNodeWithTag("status.title").assertTextEquals(text(R.string.status_strict_title))
        compose.onNodeWithText(text(R.string.android_status_off_closed)).assertIsDisplayed()
        compose.onNodeWithText(text(R.string.check_all_stopped)).assertIsDisplayed()
        compose.onNodeWithText(text(R.string.check_ks_stopped)).assertIsDisplayed()
        compose.onNodeWithTag("ip.address").assertTextEquals("—")
        compose.onNodeWithTag("ip.meta").assertTextEquals(text(R.string.ip_meta_stopped))
        compose.onNodeWithTag("ip.real").assertTextContains(text(R.string.ip_real_hidden), substring = true)
        // Выключается блок в настройках VPN Android — туда и ведет строка.
        compose.onNodeWithTag("status.vpnSettings").assertTextEquals(text(R.string.android_settings_vpn_open)).performClick()
        assertEquals(listOf("vpn"), settings)

        ui = ui.copy(status = TunnelStatus(TunnelState.Error, error = "PGC-SVC-002"))
        compose.onNodeWithTag("status.title").assertTextEquals(text(R.string.status_blocked_title))
        compose.onNodeWithText(text(R.string.status_error_sub, "PGC-SVC-002")).assertIsDisplayed()
        compose.onNodeWithTag("status.checked").assertTextEquals(text(R.string.checked_blocked))
        compose.onNodeWithTag("status.retry").assertTextEquals(text(R.string.action_retry)).performClick()
        assertEquals(listOf("retry"), retried)

        ui = ui.copy(status = TunnelStatus(TunnelState.Error, error = "PGC-NET-001"))
        compose.onNodeWithTag("status.sub").assertTextEquals(text(R.string.status_blocked_sub))

        ui = ui.copy(status = TunnelStatus(), blocked = false)
        compose.onNodeWithText(text(R.string.status_off_sub)).assertIsDisplayed()
        compose.onNodeWithText(text(R.string.check_all_off)).assertIsDisplayed()
        compose.onNodeWithTag("ip.address").assertTextEquals("198.51.100.7")
        compose.onNodeWithTag("ip.real").assertTextContains(text(R.string.ip_real_visible), substring = true)
    }

    @Test
    fun `home error names the code, offers a retry and sends to the check for details`() {
        val status = TunnelStatus(TunnelState.Error, error = "PGC-NET-010")
        val ui = MainUi(status, listOf(nl), "nl", ip = WebIp(direct = "198.51.100.7"))
        var retried = 0
        var check by mutableStateOf(false)
        compose.setContent {
            if (check) {
                PgTheme { CheckScreen(ui, null, {}) }
            } else {
                Home(ui, actions = HomeActions(retry = { retried++ }))
            }
        }

        compose.onNodeWithTag("status.title").assertTextEquals(text(R.string.status_error_title))
        compose.onNodeWithText(text(R.string.status_error_sub_direct, "PGC-NET-010")).assertIsDisplayed()
        compose.onNodeWithTag("status.checked").assertTextEquals(text(R.string.checked_error))
        compose.onNodeWithTag("status.retry").assertTextEquals(text(R.string.action_retry)).performClick()
        assertEquals(1, retried)
        // Ошибка без блока Android: трафик идет напрямую — сайты видят реальный IP, «трафик остановлен» было бы неправдой.
        compose.onNodeWithTag("ip.address").assertTextEquals("198.51.100.7")
        compose.onNodeWithTag("ip.meta").assertTextEquals(text(R.string.ip_meta_off))

        // Что значит код — на «Проверке», куда ведет строка.
        check = true
        compose.onNodeWithText(text(R.string.error_PGC_NET_010)).assertIsDisplayed()
    }

    @Test
    fun `home leak says the real ip is open`() {
        val status = TunnelStatus(TunnelState.Connected, serverId = "nl", since = 1L, lockdown = true)
        val ip = WebIp(exit = "198.51.100.7", direct = "198.51.100.7", session = 1L, checkedAt = System.currentTimeMillis())
        compose.setContent { Home(MainUi(status, listOf(nl), "nl", ip = ip)) }
        compose.onNodeWithTag("status.title").assertTextEquals(text(R.string.status_leak_title))
        compose.onNodeWithTag("status.sub").assertTextEquals(text(R.string.status_leak_sub))
        compose.onNodeWithText(text(R.string.check_all_same_ip)).assertIsDisplayed()
        compose.onNodeWithTag("ip.address").assertTextEquals("198.51.100.7")
        compose.onNodeWithTag("ip.real").assertTextContains(text(R.string.ip_real_visible), substring = true)
        compose.onNodeWithTag("status.verify").assertIsDisplayed()
    }

    @Test
    fun `chip opens its explanation under the grid and a second tap closes it`() {
        val status = TunnelStatus(TunnelState.Connected, serverId = "nl", since = 0L, lockdown = true)
        compose.setContent { Home(MainUi(status, listOf(nl), "nl")) }
        compose.onNodeWithTag("chip.info").assertDoesNotExist()

        compose.onNodeWithTag("chip.dns").performClick().assertIsSelected()
        compose.onNodeWithTag("chip.info").assertIsDisplayed()
        compose.onNodeWithText(text(R.string.check_dns_text)).assertIsDisplayed()

        // Другая плитка — ее пояснение, прежняя больше не выбрана.
        compose.onNodeWithTag("chip.ks").performClick().assertIsSelected()
        compose.onNodeWithTag("chip.dns").assertIsNotSelected()
        compose.onNodeWithText(text(R.string.android_check_ks_text)).assertIsDisplayed()
        compose.onNodeWithText(text(R.string.check_dns_text)).assertDoesNotExist()

        compose.onNodeWithTag("chip.ks").performClick().assertIsNotSelected()
        compose.onNodeWithTag("chip.info").assertDoesNotExist()
    }

    @Test
    fun `checked line tells when the exit was checked and checks again in place`() {
        val status = TunnelStatus(TunnelState.Connected, serverId = "nl", since = 1L, lockdown = true)
        val now = System.currentTimeMillis()
        var verified = 0
        var ui by mutableStateOf(MainUi(status, listOf(nl), "nl", ip = WebIp(exit = "203.0.113.24", session = 1L, checkedAt = now)))
        compose.setContent { Home(ui, actions = HomeActions(verify = { verified++ })) }
        compose.onNodeWithTag("status.checked").assertTextEquals(text(R.string.checked_now))
        compose.onNodeWithTag("status.verify").assertTextEquals(text(R.string.action_check)).performClick()
        assertEquals(1, verified)

        // Пока идет проверка — «Проверяем…», кнопки нет (HasCheckAction десктопа).
        ui = ui.copy(ip = ui.ip.copy(measuring = true))
        compose.onNodeWithTag("status.checked").assertTextEquals(text(R.string.checked_running))
        compose.onNodeWithTag("status.verify").assertDoesNotExist()

        val earlier = now - 60_000
        ui = ui.copy(ip = ui.ip.copy(measuring = false, checkedAt = earlier))
        val at = DateTimeFormatter.ofPattern("HH:mm", Locale.ROOT).format(Instant.ofEpochMilli(earlier).atZone(ZoneId.systemDefault()))
        compose.onNodeWithTag("status.checked").assertTextEquals(text(R.string.checked_at, at))
    }

    @Test
    fun `eye hides the ips with stars and copy gives a toast`() {
        val status = TunnelStatus(TunnelState.Connected, serverId = "nl", since = 1L, lockdown = true)
        val ip = WebIp(exit = "203.0.113.24", direct = "198.51.100.7", session = 1L, checkedAt = System.currentTimeMillis())
        val copied = mutableListOf<String>()
        compose.setContent { Home(MainUi(status, listOf(nl), "nl", ip = ip), actions = HomeActions(copy = { copied += it })) }
        compose.onNodeWithTag("ip.address").assertTextEquals("203.0.113.24")
        compose.onNodeWithTag("ip.real").assertTextContains("198.51.100.7", substring = true).assertTextContains(text(R.string.ip_real_hidden), substring = true)

        compose.onNodeWithTag("toast").assertDoesNotExist()
        compose.onNodeWithTag("ip.copy").assertIsEnabled().performClick()
        assertEquals(listOf("203.0.113.24"), copied)
        compose.onNodeWithTag("toast").assertTextEquals(text(R.string.ip_copied))

        // Глаз: цифры — звездочками, точки остаются; скрытый адрес не копируется.
        compose.onNodeWithTag("ip.mask").performClick()
        compose.onNodeWithTag("ip.address").assertTextEquals("***.*.***.**")
        compose.onNodeWithTag("ip.real").assertTextContains("***.**.***.*", substring = true)
        compose.onNodeWithText("198.51.100.7", substring = true).assertDoesNotExist()
        compose.onNodeWithTag("ip.copy").assertIsNotEnabled().performClick()
        assertEquals(listOf("203.0.113.24"), copied)

        compose.onNodeWithTag("ip.mask").performClick()
        compose.onNodeWithTag("ip.address").assertTextEquals("203.0.113.24")
    }

    @Test
    fun `home without servers leads to adding one`() {
        var added = 0
        compose.setContent { Home(MainUi(), onAdd = { added++ }) }
        // Строка без сервера — одна кнопка («server.add»): заголовок слит в нее и виден только в несклеенном дереве.
        compose.onNodeWithTag("server.title", useUnmergedTree = true).performScrollTo().assertTextEquals(text(R.string.server_none))
        compose.onNodeWithText(text(R.string.server_addFirst)).assertIsDisplayed()
        compose.onNodeWithTag("server.switch").assertDoesNotExist()
        compose.onNodeWithTag("server.add").performScrollTo().performClick()
        compose.onNodeWithTag("add").performScrollTo().performClick()
        assertEquals(2, added)
    }

    @Test
    fun `home with servers but none selected leads to the list, not to adding`() {
        // Выбранный сервер удален, другие остались: «серверов нет» и «добавьте первый» были бы неправдой.
        var listed = 0
        var added = 0
        compose.setContent { Home(MainUi(servers = listOf(nl, plain)), onServers = { listed++ }, onAdd = { added++ }) }
        compose.onNodeWithText(text(R.string.servers_none)).assertDoesNotExist()
        compose.onNodeWithText(text(R.string.server_addFirst)).assertDoesNotExist()
        compose.onNodeWithTag("server.choose").performScrollTo().assertIsDisplayed().performClick()
        assertEquals(1, listed)
        assertEquals(0, added)
    }

    @Test
    fun `home quick switch offers the server in use and the fastest measured`() {
        val fast = ServerItem("fast", "Быстрый", "FI", ProxyProtocol.Vless, TransportSecurity.Tls, TransportKind.Tcp, 443, latency = Latency.of(30))
        val silent = ServerItem("silent", "Молчит", "TR", ProxyProtocol.Vless, TransportSecurity.Tls, TransportKind.Tcp, 443, latency = Latency.failed(LatencyFailure.NoResponse))
        // Hysteria2 телефон не подключает — даже самый быстрый в быструю смену не попадает.
        val hy = ServerItem("hy", "Финляндия 01", "FI", ProxyProtocol.Hysteria2, TransportSecurity.Tls, TransportKind.Quic, 443, latency = Latency.of(20))
        val picked = mutableListOf<String>()
        var listed = 0
        compose.setContent {
            Home(MainUi(servers = listOf(nl, fast, silent, plain, hy), selectedId = "nl"), onServers = { listed++ }, onSelect = { picked += it })
        }

        compose.onNodeWithTag("quick.nl").assertDoesNotExist()
        compose.onNodeWithTag("server.switch").performScrollTo().assertTextEquals(text(R.string.server_switch)).performClick()
        // Закрытое меню не составляется вовсе (PgMenu): «есть» — значит открыто.
        compose.onNodeWithTag("server.switch.menu").assertExists()
        compose.onNodeWithText(text(R.string.server_quick).uppercase()).assertExists()
        compose.onNodeWithTag("quick.nl").assertExists().assertIsSelected()
        compose.onNodeWithTag("quick.fast").assertExists().assertIsNotSelected()
        compose.onNodeWithTag("quick.silent").assertDoesNotExist()
        compose.onNodeWithTag("quick.plain").assertDoesNotExist()
        compose.onNodeWithTag("quick.hy").assertDoesNotExist()

        compose.onNodeWithTag("quick.fast").performClick()
        assertEquals(listOf("fast"), picked)
        compose.onNodeWithTag("server.switch.menu").assertDoesNotExist()

        compose.onNodeWithTag("server.switch").performClick()
        compose.onNodeWithTag("quick.all").assertTextEquals(text(R.string.server_all)).performClick()
        assertEquals(1, listed)
        compose.onNodeWithTag("server.switch.menu").assertDoesNotExist()
    }

    @Test
    fun `refresh keeps its look while measuring and only the arrow spins`() {
        var refreshed = 0
        var ui by mutableStateOf(MainUi(servers = listOf(nl), selectedId = "nl"))
        compose.setContent { Home(ui, onRefresh = { refreshed++ }) }
        compose.onNodeWithTag("server.refresh.spin", useUnmergedTree = true).assertDoesNotExist()
        compose.onNodeWithTag("server.refresh").performScrollTo().performClick()
        assertEquals(1, refreshed)

        ui = ui.copy(measuring = true)
        compose.onNodeWithTag("server.refresh.spin", useUnmergedTree = true).assertExists()
        // Кнопка не гаснет: нажатие во время замера доходит до модели, а она его не считает.
        compose.onNodeWithTag("server.refresh").assertIsEnabled().performClick()
        assertEquals(2, refreshed)
    }

    @Test
    fun `speed card shows the ping of the server in use and the tunnel speed only under protection`() {
        val measured = nl.copy(latency = Latency.of(42))
        var opened = 0
        var ui by mutableStateOf(MainUi(servers = listOf(measured), selectedId = "nl"))
        val speed = TrafficSpeed(18.94, 4.0)
        compose.setContent { Home(ui, onCheck = { opened++ }, speed = speed) }
        compose.onNodeWithTag("speed.ping", useUnmergedTree = true).assertTextEquals("—")
        compose.onNodeWithTag("speed.down", useUnmergedTree = true).assertTextEquals("—")
        compose.onNodeWithTag("speed.up", useUnmergedTree = true).assertTextEquals("—")

        ui = ui.copy(status = TunnelStatus(TunnelState.Connected, serverId = "nl", since = 0L, lockdown = true))
        val locale = Locale.forLanguageTag(Texts.language(context.resources.configuration.locales))
        compose.onNodeWithTag("speed.ping", useUnmergedTree = true).assertTextEquals("42")
        compose.onNodeWithTag("speed.down", useUnmergedTree = true).assertTextEquals(String.format(locale, "%.1f", 18.94))
        compose.onNodeWithTag("speed.up", useUnmergedTree = true).assertTextEquals(String.format(locale, "%.1f", 4.0))

        // «Пинг» ведет на «Проверку».
        compose.onNodeWithTag("speed.ping.open").performScrollTo().performClick()
        assertEquals(1, opened)
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
    fun `import offers the camera only when the phone has one`() {
        var scans = 0
        val scan: () -> Unit = { scans++ }
        var camera by mutableStateOf(true)
        compose.setContent { PgTheme { ImportScreen(ImportUi(), { null }, {}, {}, {}, {}, {}, onScan = scan.takeIf { camera }) } }
        compose.onNodeWithTag("import.qr.camera").performScrollTo().assertIsDisplayed().performClick()
        assertEquals(1, scans)

        camera = false
        compose.onNodeWithTag("import.qr.camera").assertDoesNotExist()
        compose.onNodeWithTag("import.qr.image").performScrollTo().assertIsDisplayed()
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
    fun `island shows what the home knows beyond the tunnel state`() {
        // Сеть закрыл Android без туннеля — подпись щита «Блокировка», а не «Выключено».
        compose.setContent {
            PgTheme { Island(Tab.Home, TunnelState.Off, hasServer = true, onTab = {}, onPower = {}, look = StateLook.of(ProtectionView.Strict)) }
        }
        compose.onNodeWithText(text(R.string.power_label_blocked)).assertIsDisplayed()
        compose.onNodeWithText(text(R.string.power_label_off)).assertDoesNotExist()
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

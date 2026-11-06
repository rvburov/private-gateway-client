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
import com.privategateway.client.ui.servers.ImportActions
import com.privategateway.client.ui.servers.ImportScreen
import com.privategateway.client.ui.servers.importDone
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
import kotlin.test.assertNotNull

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
        compose.onNodeWithTag("ip.real.unknown").assertDoesNotExist()
        compose.onNodeWithTag("ip.copy").assertIsNotEnabled()

        // Реальный адрес не измерен (сеть до подключения была закрыта): «не определен», а не пропавшая строка — как у десктопа.
        ui = ui.copy(ip = WebIp(direct = null))
        compose.onNodeWithTag("ip.real").assertDoesNotExist()
        compose.onNodeWithTag("ip.real.unknown").assertTextContains(text(R.string.ip_real_unknown), substring = true)
            .assertTextContains(text(R.string.ip_real_hidden), substring = true)
        ui = ui.copy(ip = ip)

        ui = ui.copy(status = TunnelStatus(TunnelState.Reconnecting, serverId = "nl", attempt = 2, lockdown = true))
        compose.onNodeWithTag("status.sub").assertTextEquals(text(R.string.status_reconnecting_sub, "2"))
        compose.onNodeWithTag("status.checked").assertTextEquals(text(R.string.checked_reconnecting))
        compose.onNodeWithText(text(R.string.check_ks_ok)).assertIsDisplayed()
        // «Вход в Wi-Fi» — пауза для входа в сеть (решение 557).
        compose.onNodeWithTag("status.captive").assertTextEquals(text(R.string.action_captive))
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

        compose.onNodeWithTag("status.title").assertTextEquals(text(R.string.status_error_title_mobile))
        compose.onNodeWithText(text(R.string.status_error_sub_direct, "PGC-NET-010")).assertIsDisplayed()
        compose.onNodeWithTag("status.checked").assertTextEquals(text(R.string.checked_error))
        compose.onNodeWithTag("status.retry").assertTextEquals(text(R.string.action_retry)).performClick()
        assertEquals(1, retried)
        // Ошибка без блока Android: трафик идет напрямую — сайты видят реальный IP, «трафик остановлен» было бы неправдой.
        compose.onNodeWithTag("ip.address").assertTextEquals("198.51.100.7")
        compose.onNodeWithTag("ip.meta").assertTextEquals(text(R.string.ip_meta_off))

        // Что значит код — на «Проверке», куда ведет строка: в строке «Сетевое ядро», отдельной карточки ошибки нет.
        check = true
        compose.onNodeWithTag("check.row.core.detail")
            .assertTextEquals(text(R.string.diag_core_stopped) + " · " + text(R.string.error_PGC_NET_010))
            .assertIsDisplayed()
        compose.onNodeWithTag("check.row.core.chip").assertTextEquals(text(R.string.diag_state_stopped_chip))
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

    /** «Добавить профиль» с источником и текстом, которые меняют нажатия, — как MainViewModel; остальное записывается. */
    private class ImportHarness(initial: ImportUi = ImportUi()) {
        var state by mutableStateOf(initial)
        val checked = mutableListOf<String>()
        var clipboard = 0
        var added = 0
        var images = 0
        var files = 0
        var scans = 0
        var closed = 0
        var camera by mutableStateOf(true)
        private val scan: () -> Unit = { scans++ }

        val actions: ImportActions
            get() = ImportActions(
                source = { state = ImportUi(text = state.text, source = it) },
                clipboard = {
                    clipboard++
                    state = ImportUi(text = state.text, source = ImportSource.Clipboard)
                },
                text = { state = state.copy(text = it) },
                check = { checked += it },
                name = { state = state.copy(name = it) },
                add = { added++ },
                close = { closed++ },
                pickImage = { images++ },
                pickFile = { files++ },
                scan = scan.takeIf { camera },
            )
    }

    private fun showImport(harness: ImportHarness) {
        compose.setContent { PgTheme { ImportScreen(harness.state, harness.actions) } }
    }

    @Test
    fun `import shows four source cards and the tapped one is chosen`() {
        val harness = ImportHarness()
        showImport(harness)
        compose.onNodeWithText(text(R.string.page_add_title)).assertIsDisplayed()
        for (tag in listOf("import.clipboard", "import.qr", "import.link", "import.file")) {
            compose.onNodeWithTag(tag).performScrollTo().assertIsDisplayed().assertIsNotSelected()
        }
        compose.onNodeWithText(text(R.string.import_file_text)).performScrollTo().assertIsDisplayed()
        compose.onNodeWithText(text(R.string.android_import_hint)).assertDoesNotExist()
        compose.onNodeWithTag("import.text").assertDoesNotExist()
        compose.onNodeWithTag("import.add").assertDoesNotExist()

        compose.onNodeWithTag("import.clipboard").performClick()
        assertEquals(1, harness.clipboard)
        compose.onNodeWithTag("import.clipboard").assertIsSelected()

        compose.onNodeWithTag("import.file").performScrollTo().performClick()
        assertEquals(1, harness.files, "карточка файла открывает окно «Открыть»")
        compose.onNodeWithTag("import.file").assertIsSelected()
        compose.onNodeWithTag("import.clipboard").assertIsNotSelected()

        compose.onNodeWithTag("import.back").performClick()
        assertEquals(1, harness.closed)
    }

    @Test
    fun `link card opens the field and check below it, only while chosen`() {
        val link = Fixtures.testdata.resolve("links/vless/reality-vision.txt").readText().trim()
        val harness = ImportHarness()
        showImport(harness)
        compose.onNodeWithTag("import.link").performScrollTo().performClick()
        compose.onNodeWithTag("import.link").assertIsSelected()
        compose.onNodeWithTag("import.check").performScrollTo().assertIsNotEnabled()
        compose.onNodeWithTag("import.text").performTextInput(link)
        compose.onNodeWithTag("import.check").performScrollTo().assertIsEnabled().performClick()
        assertEquals(listOf(link), harness.checked)
        assertEquals(link, harness.state.text, "текст живет в модели окна, а не в сохраненном состоянии")

        // Повторное нажатие на раскрытую карточку проверенное не стирает.
        harness.state = harness.state.copy(batch = ImportParser.parse(link))
        compose.onNodeWithTag("import.link").performScrollTo().performClick()
        compose.onNodeWithTag("import.preview").performScrollTo().assertIsDisplayed()

        compose.onNodeWithTag("import.clipboard").performScrollTo().performClick()
        compose.onNodeWithTag("import.text").assertDoesNotExist()
        compose.onNodeWithTag("import.check").assertDoesNotExist()
    }

    @Test
    fun `preview tiles count found, new, duplicates and errors and the button adds the new ones`() {
        val links = Fixtures.servers().distinctBy { Fixtures.identity(it) }.take(3)
        val input = links.joinToString("\n") { com.privategateway.client.core.links.LinkWriter.write(it) } + "\nnot-a-link\n"
        val batch = ImportParser.parse(input)
        val harness = ImportHarness(ImportUi(batch = batch, duplicates = 1, source = ImportSource.Clipboard))
        showImport(harness)

        compose.onNodeWithTag("import.count.found", useUnmergedTree = true).performScrollTo().assertTextEquals("4")
        compose.onNodeWithTag("import.count.new", useUnmergedTree = true).assertTextEquals("2")
        compose.onNodeWithTag("import.count.duplicates", useUnmergedTree = true).assertTextEquals("1")
        compose.onNodeWithTag("import.count.failed", useUnmergedTree = true).assertTextEquals("1")
        compose.onNodeWithText(text(R.string.import_line, "4", text(R.string.import_error_bad_link))).performScrollTo().assertIsDisplayed()
        compose.onNodeWithTag("import.format", useUnmergedTree = true).assertTextContains(text(R.string.import_format_links), substring = true)
        compose.onNodeWithTag("import.name").performScrollTo().assertIsDisplayed()

        // Кнопка — вне прокрутки, по числу новых (а не найденных): «Добавить 2 сервера».
        val language = Texts.language(context.resources.configuration.locales)
        compose.onNodeWithTag("import.add").assertIsDisplayed().assertTextEquals(text(Texts.importAdd(2, language), "2")).performClick()
        assertEquals(1, harness.added)

        harness.state = harness.state.copy(adding = true)
        compose.onNodeWithTag("import.add").assertIsNotEnabled()
    }

    @Test
    fun `nothing new says so, renewals only offer to update protection`() {
        val link = Fixtures.testdata.resolve("links/vless/reality-vision.txt").readText().trim()
        val harness = ImportHarness(ImportUi(batch = ImportParser.parse(link), duplicates = 1, source = ImportSource.Clipboard))
        showImport(harness)
        compose.onNodeWithTag("import.add").assertTextEquals(text(R.string.import_nothing)).assertIsNotEnabled()
        compose.onNodeWithTag("import.name").assertDoesNotExist()

        harness.state = harness.state.copy(renewals = 1)
        val language = Texts.language(context.resources.configuration.locales)
        compose.onNodeWithTag("import.add").assertTextEquals(text(Texts.importRenew(1, language), "1")).assertIsEnabled()
    }

    @Test
    fun `more than three bad lines are counted in one line of the warning`() {
        val bad = (1..5).joinToString("\n") { "not-a-link-$it" }
        val harness = ImportHarness(ImportUi(batch = ImportParser.parse(bad), source = ImportSource.Clipboard))
        showImport(harness)
        compose.onNodeWithTag("import.errors").performScrollTo().assertIsDisplayed()
        compose.onNodeWithText(text(R.string.import_line, "3", text(R.string.import_error_bad_link))).assertExists()
        compose.onNodeWithText(text(R.string.import_line, "4", text(R.string.import_error_bad_link))).assertDoesNotExist()
        compose.onNodeWithText(text(R.string.import_more, "2")).assertExists()
    }

    @Test
    fun `text without servers says so instead of a preview`() {
        val harness = ImportHarness(ImportUi(batch = ImportParser.parse(""), source = ImportSource.Clipboard))
        showImport(harness)
        compose.onNodeWithTag("import.empty").performScrollTo().assertIsDisplayed()
        compose.onNodeWithTag("import.preview").assertDoesNotExist()
        compose.onNodeWithTag("import.add").assertDoesNotExist()
    }

    @Test
    fun `qr card offers the camera and an image, without a camera it opens the image at once`() {
        val harness = ImportHarness()
        showImport(harness)
        compose.onNodeWithTag("import.qr").performScrollTo().performClick()
        compose.onNodeWithTag("import.qr").assertIsSelected()
        // Закрытое меню не составляется вовсе (PgMenu): «есть» — значит открыто.
        compose.onNodeWithTag("import.qr.menu").assertExists()
        compose.onNodeWithTag("import.qr.camera").performClick()
        assertEquals(1, harness.scans)
        compose.onNodeWithTag("import.qr.menu").assertDoesNotExist()

        compose.onNodeWithTag("import.qr").performClick()
        compose.onNodeWithTag("import.qr.image").performClick()
        assertEquals(1, harness.images)

        harness.camera = false
        compose.onNodeWithTag("import.qr").performClick()
        compose.onNodeWithTag("import.qr.menu").assertDoesNotExist()
        assertEquals(2, harness.images, "без камеры — сразу выбор картинки")
        assertEquals(1, harness.scans)
    }

    @Test
    fun `subscription download shows progress, the reason it failed and the http warning`() {
        val link = Fixtures.testdata.resolve("links/vless/reality-vision.txt").readText().trim()
        val harness = ImportHarness(ImportUi(text = "https://example.com/sub/abcdef", downloading = true, source = ImportSource.Link))
        showImport(harness)
        compose.onNodeWithTag("import.downloading").performScrollTo().assertIsDisplayed()
        compose.onNodeWithTag("import.check").assertIsNotEnabled()

        harness.state = ImportUi(text = harness.state.text, failure = FailureReason.Forbidden(403), source = ImportSource.Link)
        compose.onNodeWithText(text(R.string.import_download_failed, text(R.string.import_download_forbidden, "403"))).performScrollTo().assertIsDisplayed()

        harness.state = ImportUi(text = harness.state.text, batch = ImportParser.parse(link), insecure = true, name = "example.com", source = ImportSource.Link)
        compose.onNodeWithTag("import.insecure").performScrollTo().assertIsDisplayed()
        compose.onNodeWithTag("import.name").performScrollTo().assertIsDisplayed()
        compose.onNodeWithText("example.com").assertIsDisplayed()
    }

    @Test
    fun `an added subscription warns in place of the name and the name is not longer than 64`() {
        val link = Fixtures.testdata.resolve("links/vless/reality-vision.txt").readText().trim()
        val harness = ImportHarness(ImportUi(batch = ImportParser.parse(link), existing = "Провайдер", source = ImportSource.Link))
        showImport(harness)
        compose.onNodeWithTag("import.existing").performScrollTo().assertIsDisplayed()
        compose.onNodeWithText(text(R.string.import_subscription_exists, "Провайдер")).assertIsDisplayed()
        compose.onNodeWithTag("import.name").assertDoesNotExist()

        harness.state = harness.state.copy(existing = null)
        compose.onNodeWithTag("import.name").performScrollTo().performTextInput("x".repeat(70))
        assertEquals(64, harness.state.name.length)
    }

    @Test
    fun `empty clipboard and an unreadable file are said under the cards`() {
        val harness = ImportHarness(ImportUi(source = ImportSource.Clipboard, problem = Message(R.string.import_empty_clipboard)))
        showImport(harness)
        compose.onNodeWithText(text(R.string.import_empty_clipboard)).performScrollTo().assertIsDisplayed()

        harness.state = ImportUi(source = ImportSource.File, problem = Message(R.string.import_file_failed, "huge.txt"))
        compose.onNodeWithText(text(R.string.import_file_failed, "huge.txt")).performScrollTo().assertIsDisplayed()
    }

    @Test
    fun `the toast after adding says what happened`() {
        assertEquals(null, importDone(ImportUi(), "ru"))
        val added = assertNotNull(importDone(ImportUi(added = 3), "ru"))
        assertEquals(R.string.import_added_few, added.text)
        assertEquals(listOf("3"), added.args.toList())
        assertEquals(R.string.import_renewed_one, importDone(ImportUi(added = 0, renewals = 1), "ru")?.text)
        assertEquals(R.string.import_nothing, importDone(ImportUi(added = 0), "ru")?.text)
        val updated = assertNotNull(importDone(ImportUi(added = 1, removed = 2, existing = "Провайдер"), "ru"))
        assertEquals(R.string.subs_updated, updated.text)
        assertEquals(listOf("Провайдер", "1", "2"), updated.args.toList())
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

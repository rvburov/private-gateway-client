package com.privategateway.client.ui

import android.content.Context
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.assertIsEnabled
import androidx.compose.ui.test.assertIsNotDisplayed
import androidx.compose.ui.test.assertIsNotEnabled
import androidx.compose.ui.test.assertTextEquals
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performScrollTo
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.privategateway.client.R
import com.privategateway.client.core.servers.ProxyProtocol
import com.privategateway.client.core.servers.ServerItem
import com.privategateway.client.core.servers.TransportKind
import com.privategateway.client.core.servers.TransportSecurity
import com.privategateway.client.core.settings.BypassApp
import com.privategateway.client.core.settings.DnsChoice
import com.privategateway.client.core.settings.RoutingMode
import com.privategateway.client.core.settings.RoutingSettings
import com.privategateway.client.core.settings.RuleAction
import com.privategateway.client.core.settings.SiteRule
import com.privategateway.client.core.settings.TunnelSettings
import com.privategateway.client.core.text.Plural
import com.privategateway.client.core.vpn.TunnelState
import com.privategateway.client.core.vpn.TunnelStatus
import com.privategateway.client.inspect.CheckUi
import com.privategateway.client.inspect.PhoneHints
import com.privategateway.client.ui.check.CheckActions
import com.privategateway.client.ui.check.CheckRows
import com.privategateway.client.ui.check.CheckScreen
import com.privategateway.client.ui.check.HinderRows
import com.privategateway.client.ui.theme.PgTheme
import com.privategateway.client.vpn.DelayBreakdown
import com.privategateway.client.vpn.JournalEntry
import com.privategateway.client.vpn.JournalLevel
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.annotation.Config
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.Locale
import kotlin.test.assertEquals
import kotlin.test.assertTrue

/** «Проверка» телефона без устройства (Robolectric): строки, состояния, задержка, «Что мешает», журнал, переходы — доска 7. */
@RunWith(AndroidJUnit4::class)
@Config(qualifiers = "w384dp-h832dp")
class CheckScreenTest {
    @get:Rule
    val compose = createComposeRule()

    private val context = ApplicationProvider.getApplicationContext<Context>()
    private val nl = ServerItem("nl", "Нидерланды 01", "NL", ProxyProtocol.Vless, TransportSecurity.Reality, TransportKind.Tcp, 443)
    private val connected = TunnelStatus(TunnelState.Connected, serverId = "nl", since = 1L, lockdown = true)
    private val ids = listOf("tunnel", "core", "all_traffic", "kill_switch", "dns", "ipv6", "exit_ip", "other_vpn")

    private fun text(id: Int, vararg args: Any) = context.getString(id, *args)

    private fun mainUi(status: TunnelStatus, ip: WebIp = WebIp(), blocked: Boolean = false) =
        MainUi(status = status, servers = listOf(nl), selectedId = "nl", blocked = blocked, ip = ip)

    private fun time(at: Long) = DateTimeFormatter.ofPattern("HH:mm:ss", Locale.ROOT).format(Instant.ofEpochMilli(at).atZone(ZoneId.systemDefault()))

    /** Строка проверки: заголовок, пояснение и чип. */
    private fun assertRow(id: String, title: Int, detail: String, chip: String) {
        compose.onNodeWithTag("check.row.$id.title").assertTextEquals(text(title))
        compose.onNodeWithTag("check.row.$id.detail").assertTextEquals(detail)
        compose.onNodeWithTag("check.row.$id.chip").assertTextEquals(chip)
    }

    @Test
    fun `protected results follow the board in its order`() {
        val ui = mainUi(connected, WebIp(exit = "203.0.113.24", direct = "198.51.100.7", session = 1L))
        compose.setContent { PgTheme { CheckScreen(ui, "26.10.21", {}, CheckUi(hints = PhoneHints())) } }

        compose.onNodeWithTag("check.title").assertTextEquals(text(R.string.page_check_title))
        compose.onNodeWithText(text(R.string.diag_run)).assertIsDisplayed()
        compose.onNodeWithText(text(R.string.diag_export)).assertIsDisplayed()
        compose.onNodeWithTag("check.note").assertTextEquals(text(R.string.diag_note))
        // Вместо «Службы» — туннель: службы у телефона нет.
        assertRow("tunnel", R.string.fact_tunnel, text(R.string.diag_state_ok_text), text(R.string.diag_state_ok_chip))
        assertRow("core", R.string.diag_core_title, text(R.string.diag_core_ok, "Xray-core 26.10.21"), text(R.string.diag_state_ok_chip))
        assertRow("all_traffic", R.string.diag_all_traffic_title, text(R.string.diag_all_traffic_ok), text(R.string.diag_state_ok_chip))
        assertRow(
            "kill_switch",
            R.string.diag_kill_switch_title,
            text(R.string.diag_kill_switch_ok, text(R.string.android_settings_vpn_lockdown)),
            text(R.string.diag_state_ok_chip),
        )
        assertRow("dns", R.string.diag_dns_title, text(R.string.diag_dns_ok), text(R.string.diag_dns_ok_chip))
        assertRow("ipv6", R.string.diag_ipv6_title, text(R.string.diag_ipv6_ok), text(R.string.diag_ipv6_ok_chip))
        assertRow("exit_ip", R.string.diag_exit_ip_title, "203.0.113.24", text(R.string.diag_exit_ip_ok_chip))
        assertRow("other_vpn", R.string.diag_other_vpn_title, text(R.string.diag_other_vpn_ok), text(R.string.diag_other_vpn_ok_chip))

        // Порядок строк — как на доске: каждая следующая ниже предыдущей. Строки «Службы» и карточки ошибки нет.
        val tops = ids.map { compose.onNodeWithTag("check.row.$it").fetchSemanticsNode().positionInRoot.y }
        assertEquals(tops.sorted(), tops, "строки идут по порядку доски")
        assertEquals(tops.size, tops.distinct().size, "у каждой строки свое место")
        compose.onNodeWithText(text(R.string.diag_service_title)).assertDoesNotExist()
        compose.onNodeWithText(text(R.string.card_core)).assertDoesNotExist()
    }

    @Test
    fun `the header stays while the content scrolls`() {
        compose.setContent { PgTheme { CheckScreen(mainUi(connected), null, {}, CheckUi(hints = PhoneHints())) } }

        compose.onNodeWithTag("check.journal").performScrollTo().assertIsDisplayed()
        compose.onNodeWithTag("check.title").assertIsDisplayed()
        compose.onNodeWithTag("check.run").assertIsNotDisplayed()
    }

    @Test
    fun `while checking every row waits and the arrow spins`() {
        compose.setContent { PgTheme { CheckScreen(mainUi(connected), null, {}, CheckUi(running = true, hints = PhoneHints())) } }

        compose.onNodeWithText(text(R.string.diag_running)).assertIsDisplayed()
        compose.onNodeWithTag("check.run.spin", useUnmergedTree = true).assertExists()
        for (id in ids) {
            compose.onNodeWithTag("check.row.$id.detail").assertTextEquals(text(R.string.diag_checking))
            compose.onNodeWithTag("check.row.$id.chip").assertTextEquals(text(R.string.diag_state_waiting_chip))
        }
    }

    @Test
    fun `reconnecting is the blocked state of the board`() {
        val status = connected.copy(state = TunnelState.Reconnecting, since = null, attempt = 1)
        compose.setContent { PgTheme { CheckScreen(mainUi(status), "26.10.21", {}, CheckUi(hints = PhoneHints(), delay = DelayBreakdown(38, 42, 160, 0L))) } }

        assertRow("core", R.string.diag_core_title, text(R.string.diag_core_no_reply), text(R.string.diag_core_no_reply_chip))
        assertRow("all_traffic", R.string.diag_all_traffic_title, text(R.string.diag_all_traffic_stopped), text(R.string.diag_state_stopped_chip))
        assertRow("dns", R.string.diag_dns_title, text(R.string.diag_dns_ok), text(R.string.diag_dns_ok_chip))
        assertRow("ipv6", R.string.diag_ipv6_title, text(R.string.diag_ipv6_ok), text(R.string.diag_ipv6_ok_chip))
        assertRow("exit_ip", R.string.diag_exit_ip_title, text(R.string.diag_exit_ip_stopped), text(R.string.diag_exit_ip_stopped_chip))
        // Задержки без работающего туннеля нет: прежний замер не показывается.
        compose.onNodeWithTag("check.delay.0.value").performScrollTo().assertTextEquals("—")
        compose.onNodeWithTag("check.delay.note").assertTextEquals(text(R.string.diag_delay_off))
    }

    @Test
    fun `protection off shows the real ip and what may interfere`() {
        val ui = mainUi(TunnelStatus(), WebIp(direct = "198.51.100.7"))
        val hints = PhoneHints(proxy = "192.0.2.10:3128")
        compose.setContent { PgTheme { CheckScreen(ui, null, {}, CheckUi(hints = hints)) } }

        assertRow("tunnel", R.string.fact_tunnel, text(R.string.diag_state_off_text), text(R.string.diag_state_off_chip))
        assertRow("core", R.string.diag_core_title, text(R.string.diag_core_off), text(R.string.diag_state_off_chip))
        assertRow("all_traffic", R.string.diag_all_traffic_title, text(R.string.diag_all_traffic_off), text(R.string.diag_all_traffic_off_chip))
        assertRow("kill_switch", R.string.diag_kill_switch_title, text(R.string.diag_kill_switch_off), text(R.string.diag_state_off_chip))
        assertRow("dns", R.string.diag_dns_title, text(R.string.diag_dns_off), text(R.string.diag_dns_off_chip))
        assertRow("ipv6", R.string.diag_ipv6_title, text(R.string.diag_ipv6_off), text(R.string.diag_ipv6_off_chip))
        assertRow("exit_ip", R.string.diag_exit_ip_title, text(R.string.diag_exit_ip_off, "198.51.100.7"), text(R.string.diag_exit_ip_off_chip))
        assertRow("other_vpn", R.string.diag_other_vpn_title, text(R.string.diag_other_vpn_system_proxy), text(R.string.diag_state_warning_chip))

        compose.onNodeWithTag("check.hinder.proxy").performScrollTo().assertIsDisplayed()
        compose.onNodeWithTag("check.hinder.proxy.title").assertTextEquals(text(R.string.diag_hinder_system))
        compose.onNodeWithTag("check.hinder.proxy.detail")
            .assertTextEquals(text(R.string.diag_hinder_detail, "192.0.2.10:3128", text(R.string.diag_hinder_unknown)) + " " + text(R.string.diag_hinder_how_system))
        compose.onNodeWithTag("check.hinder.proxy.chip").assertTextEquals(text(R.string.diag_hinder_chip_set))
        compose.onNodeWithTag("check.hinder.none").assertDoesNotExist()
        compose.onNodeWithTag("check.delay.note").performScrollTo().assertTextEquals(text(R.string.diag_delay_off))
    }

    @Test
    fun `nothing in the way says so`() {
        compose.setContent { PgTheme { CheckScreen(mainUi(connected), null, {}, CheckUi(hints = PhoneHints())) } }

        compose.onNodeWithTag("check.hinder.none").performScrollTo().assertIsDisplayed()
        compose.onNodeWithTag("check.hinder.none.title").assertTextEquals(text(R.string.diag_hinder_none))
        compose.onNodeWithTag("check.hinder.none.detail").assertTextEquals(text(R.string.diag_hinder_none_text))
        compose.onNodeWithTag("check.hinder.none.chip").assertTextEquals(text(R.string.diag_hinder_chip_none))
    }

    @Test
    fun `delay shows three numbers and where the new connection time comes from`() {
        val at = 1_790_000_000_000L
        var refreshed = 0
        val check = CheckUi(hints = PhoneHints(), delay = DelayBreakdown(38, 42, 160, at))
        compose.setContent { PgTheme { CheckScreen(mainUi(connected), null, {}, check, CheckActions(measureDelay = { refreshed++ })) } }

        compose.onNodeWithTag("check.delay.0.value").performScrollTo().assertTextEquals(text(R.string.diag_delay_ms, "38"))
        compose.onNodeWithTag("check.delay.1.value").assertTextEquals(text(R.string.diag_delay_ms, "42"))
        compose.onNodeWithTag("check.delay.2.value").assertTextEquals(text(R.string.diag_delay_ms, "160"))
        // 160 / 38 ≈ 4,2 круга до сервера; рукопожатие сервера — REALITY.
        val language = Texts.language(context.resources.configuration.locales)
        val word = when (Plural.form(4, language)) {
            "one" -> R.string.diag_delay_rounds_one
            "few" -> R.string.diag_delay_rounds_few
            else -> R.string.diag_delay_rounds_many
        }
        compose.onNodeWithTag("check.delay.note").assertTextEquals(text(R.string.diag_delay_why, "160", "4", text(word), "REALITY", "38"))
        compose.onNodeWithTag("check.delay.updated").assertTextEquals(text(R.string.diag_delay_updated, time(at)))
        compose.onNodeWithTag("check.delay.refresh").assertIsEnabled().performClick()
        assertEquals(1, refreshed)
    }

    @Test
    fun `delay while measuring shows dashes and the refresh waits`() {
        val check = CheckUi(hints = PhoneHints(), delay = DelayBreakdown(38, 42, 160, 0L), measuringDelay = true)
        compose.setContent { PgTheme { CheckScreen(mainUi(connected), null, {}, check) } }

        for (index in 0..2) {
            compose.onNodeWithTag("check.delay.$index.value").performScrollTo().assertTextEquals("—")
        }

        compose.onNodeWithTag("check.delay.note").assertTextEquals(text(R.string.diag_delay_measuring))
        compose.onNodeWithTag("check.delay.updated").assertTextEquals("")
        compose.onNodeWithTag("check.delay.refresh").assertIsNotEnabled()
    }

    @Test
    fun `journal shows new entries first and says when it is empty`() {
        val first = 1_790_000_000_000L
        val journal = listOf(
            JournalEntry(first, JournalLevel.Info, "connect  mode=vpn"),
            JournalEntry(first + 2_000, JournalLevel.Error, "stopped  error=PGC-SVC-002"),
        )
        var check by mutableStateOf(CheckUi(hints = PhoneHints(), journal = journal))
        compose.setContent { PgTheme { CheckScreen(mainUi(connected), null, {}, check) } }

        compose.onNodeWithTag("check.journal").performScrollTo()
        compose.onNodeWithText(text(R.string.diag_journal_masked)).assertIsDisplayed()
        compose.onNodeWithTag("check.journal.0").assertTextEquals(time(first + 2_000) + "  ERR  stopped  error=PGC-SVC-002")
        compose.onNodeWithTag("check.journal.1").assertTextEquals(time(first) + "  INF  connect  mode=vpn")
        compose.onNodeWithTag("check.journal.empty").assertDoesNotExist()

        check = check.copy(journal = emptyList())
        compose.onNodeWithTag("check.journal.empty").performScrollTo().assertTextEquals(text(R.string.diag_journal_empty))
    }

    @Test
    fun `buttons check and export and inspector rows open their pages`() {
        var opened = 0
        var runs = 0
        var exports = 0
        var screen by mutableStateOf(mainUi(connected))
        val actions = CheckActions(open = { opened++ }, run = { runs++ }, export = { exports++ })
        compose.setContent { PgTheme { CheckScreen(screen, null, {}, CheckUi(hints = PhoneHints()), actions) } }

        // Экран открыт — проверка сразу; другое состояние защиты — снова.
        compose.waitForIdle()
        assertEquals(1, opened)
        screen = mainUi(TunnelStatus())
        compose.waitForIdle()
        assertEquals(2, opened)

        compose.onNodeWithTag("check.run").performClick()
        compose.onNodeWithTag("check.export").performClick()
        assertEquals(1, runs)
        assertEquals(1, exports)

        compose.onNodeWithTag("check.network").performScrollTo().performClick()
        compose.onNodeWithTag("inspect.network.page").assertExists()
        compose.onNodeWithContentDescription(text(R.string.common_back)).performClick()
        compose.onNodeWithTag("check.probe").performScrollTo().performClick()
        compose.onNodeWithTag("inspect.probe.page").assertExists()
        compose.onNodeWithContentDescription(text(R.string.common_back)).performClick()
        compose.onNodeWithTag("check.results").assertExists()
    }

    @Test
    fun `ping tile scrolls to the delay once`() {
        var shown = 0
        var check by mutableStateOf(CheckUi(hints = PhoneHints(), focusDelay = true))
        val actions = CheckActions(delayShown = {
            shown++
            check = check.copy(focusDelay = false)
        })
        compose.setContent { PgTheme { CheckScreen(mainUi(connected), null, {}, check, actions) } }

        compose.waitUntil(5_000) { shown > 0 }
        compose.onNodeWithTag("check.delay").assertIsDisplayed()
        compose.waitForIdle()
        assertEquals(1, shown)
    }

    @Test
    fun `rows know the phone states`() {
        // Android закрыл сеть без туннеля — блок сработал, трафик стоит, утечки нет.
        val strict = CheckRows.of(mainUi(TunnelStatus(), blocked = true), null, PhoneHints(), TunnelSettings(), running = false).associateBy { it.id }
        assertEquals(R.string.diag_kill_switch_stopped, strict.getValue("kill_switch").detail.single().id)
        assertEquals(ChipLook.Ok, strict.getValue("kill_switch").look)
        assertEquals(R.string.diag_all_traffic_stopped, strict.getValue("all_traffic").detail.single().id)
        assertEquals(ChipLook.Stopped, strict.getValue("exit_ip").look)

        // Android не сказал о блоке без VPN (до Android 10) — внимание, а не «выключен».
        val unknown = CheckRows.of(mainUi(connected.copy(lockdown = null)), null, PhoneHints(), TunnelSettings(), running = false).associateBy { it.id }
        assertEquals(R.string.android_settings_vpn_never, unknown.getValue("kill_switch").detail.single().id)
        assertEquals(ChipLook.Warning, unknown.getValue("kill_switch").look)

        // Свой DNS в частной сети — мимо сервера; IPv6 через сервер — в туннеле.
        val tunnel = TunnelSettings(dns = DnsChoice.Custom, customDns = "192.168.1.53", ipv6 = true)
        val custom = CheckRows.of(mainUi(connected), null, PhoneHints(), tunnel, running = false).associateBy { it.id }
        assertEquals(R.string.diag_dns_own_dns, custom.getValue("dns").detail.single().id)
        assertEquals(R.string.diag_ipv6_tunnel, custom.getValue("ipv6").detail.single().id)

        // Раздельный режим с программой или сайтом мимо VPN — «Частично», как у десктопа; без исключений — через туннель.
        val apps = RoutingSettings(RoutingMode.Split, apps = listOf(BypassApp("org.example.game", "Игра")))
        val sites = RoutingSettings(RoutingMode.Split, rules = listOf(SiteRule("example.com", RuleAction.Direct)))
        for (routing in listOf(apps, sites)) {
            val split = CheckRows.of(mainUi(connected), null, PhoneHints(), TunnelSettings(routing = routing), running = false).associateBy { it.id }
            assertEquals(R.string.diag_all_traffic_split, split.getValue("all_traffic").detail.single().id, "$routing")
            assertEquals(R.string.diag_all_traffic_warning_chip, split.getValue("all_traffic").chip.id)
            assertEquals(ChipLook.Warning, split.getValue("all_traffic").look)
        }

        val idle = RoutingSettings(apps = apps.apps, rules = sites.rules)
        val whole = CheckRows.of(mainUi(connected), null, PhoneHints(), TunnelSettings(routing = idle), running = false).associateBy { it.id }
        assertEquals(R.string.diag_all_traffic_ok, whole.getValue("all_traffic").detail.single().id, "в режиме «Весь трафик» исключения молчат")
        val offSplit = CheckRows.of(mainUi(TunnelStatus()), null, PhoneHints(), TunnelSettings(routing = apps), running = false).associateBy { it.id }
        assertEquals(R.string.diag_all_traffic_off, offSplit.getValue("all_traffic").detail.single().id, "без защиты — напрямую весь трафик")

        // Утечка IP: выход совпал с реальным адресом.
        val leak = mainUi(connected, WebIp(exit = "198.51.100.7", direct = "198.51.100.7", session = 1L))
        val leaked = CheckRows.of(leak, null, PhoneHints(), TunnelSettings(), running = false).associateBy { it.id }
        assertEquals(R.string.diag_all_traffic_same_ip, leaked.getValue("all_traffic").detail.single().id)
        assertEquals(R.string.diag_exit_ip_same_ip, leaked.getValue("exit_ip").detail.single().id)
        assertEquals(ChipLook.Off, leaked.getValue("exit_ip").look)

        // VPN другого приложения; подсказки еще не читали — «Проверяется».
        val other = CheckRows.of(mainUi(TunnelStatus()), null, PhoneHints(otherVpn = true), TunnelSettings(), running = false).associateBy { it.id }
        assertEquals(R.string.diag_other_vpn_vpn_adapter, other.getValue("other_vpn").detail.single().id)
        val waiting = CheckRows.of(mainUi(TunnelStatus()), null, null, TunnelSettings(), running = false).associateBy { it.id }
        assertEquals(ChipLook.Waiting, waiting.getValue("other_vpn").look)
    }

    @Test
    fun `hindrances show what android reports and hide the script path`() {
        val rows = HinderRows.of(PhoneHints(pac = "https://example.com/proxy/11111111-2222-3333-4444-555555555555.pac", otherVpn = true, strictDns = "dns.example.org"))
        assertEquals(listOf("other", "pac", "private_dns"), rows.map { it.id })
        val pac = rows.single { it.id == "pac" }.detail.first().resolve(context)
        assertTrue("11111111-2222-3333-4444-555555555555" !in pac, pac)
        assertTrue(pac.startsWith("https://example.com/"), pac)
        // Частный DNS обычно не мешает — как WPAD у десктопа.
        assertEquals(ChipLook.Ok, rows.single { it.id == "private_dns" }.look)
        assertEquals(listOf("none"), HinderRows.of(PhoneHints()).map { it.id })
        assertEquals(listOf("checking"), HinderRows.of(null).map { it.id })
    }
}

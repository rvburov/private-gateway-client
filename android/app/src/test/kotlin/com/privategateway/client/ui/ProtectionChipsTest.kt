package com.privategateway.client.ui

import com.privategateway.client.R
import com.privategateway.client.core.servers.ProxyProtocol
import com.privategateway.client.core.servers.ServerItem
import com.privategateway.client.core.servers.TransportKind
import com.privategateway.client.core.servers.TransportSecurity
import com.privategateway.client.core.vpn.TunnelState
import com.privategateway.client.core.vpn.TunnelStatus
import com.privategateway.client.ui.theme.PgColor
import com.privategateway.client.ui.theme.PgIcons
import com.privategateway.client.ui.theme.PgState
import org.junit.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull

class ProtectionChipsTest {
    @Test
    fun `connected with android lockdown is protected on all four`() {
        val chips = ProtectionChips.of(TunnelState.Connected, lockdown = true)
        assertEquals(List(4) { ChipLook.Ok }, chips.map { it.look })
        assertEquals(listOf(R.string.check_all_ok, R.string.check_dns_ok, R.string.check_ipv6_ok, R.string.check_ks_ok), chips.map { it.value })
    }

    @Test
    fun `kill switch shows what android reported`() {
        assertEquals(ChipLook.Off to R.string.check_ks_off, ProtectionChips.of(TunnelState.Connected, lockdown = false).last().let { it.look to it.value })
        // До Android 10 система не говорит, включен ли блок без VPN.
        assertEquals(ChipLook.Warning to R.string.check_ks_warning, ProtectionChips.of(TunnelState.Connected, lockdown = null).last().let { it.look to it.value })
    }

    @Test
    fun `without the tunnel traffic goes direct and the kill switch is off`() {
        // Сеть не закрыта системой: трафик идет мимо туннеля, и «выключен» (красный) — правда (поправка карты, доска 26).
        for (state in listOf(TunnelState.Off, TunnelState.Error)) {
            val chips = ProtectionChips.of(state, lockdown = true)
            assertEquals(List(4) { ChipLook.Off }, chips.map { it.look }, state.name)
            assertEquals(listOf(R.string.check_all_off, R.string.check_dns_off, R.string.check_ipv6_off, R.string.check_ks_off), chips.map { it.value }, state.name)
        }
    }

    @Test
    fun `android closing the internet without the tunnel is a kill switch that went off`() {
        // «Блокировать подключения без VPN»: туннеля нет, Android закрыл сеть — трафик остановлен (оранжевый), утечки нет.
        for (state in listOf(TunnelState.Off, TunnelState.Error)) {
            val chips = ProtectionChips.of(state, lockdown = null, closed = true)
            assertEquals(listOf(R.string.check_all_stopped, R.string.check_dns_stopped, R.string.check_ipv6_stopped, R.string.check_ks_stopped), chips.map { it.value }, state.name)
            assertEquals(listOf(ChipLook.Stopped, ChipLook.Ok, ChipLook.Ok, ChipLook.Ok), chips.map { it.look }, state.name)
        }

        // Туннель поднимается или работает — его чипы, а не блок Android.
        assertEquals(ProtectionChips.of(TunnelState.Connecting, lockdown = null), ProtectionChips.of(TunnelState.Connecting, lockdown = null, closed = true))
        assertEquals(ProtectionChips.of(TunnelState.Connected, lockdown = true), ProtectionChips.of(TunnelState.Connected, lockdown = true, closed = true))
    }

    @Test
    fun `stopped is orange with a dash, like the desktop`() {
        // CheckState.Stopped десктопа: status.warning и Chip.Dash — не янтарный «частично».
        assertEquals(ChipLook.Stopped.base, PgColor.Status.Warning.Base)
        assertEquals(ChipLook.Stopped.mark, PgIcons.ChipDash)
        assertEquals(ChipLook.Warning.base, PgColor.Status.Caution.Base)
    }

    @Test
    fun `connecting waits while dns and ipv6 already stay in the tunnel`() {
        // Как ProtectionChecks.For(Connecting) десктопа и доски 09, 25: трафик ждет в карте туннеля — DNS и IPv6 не уходят мимо.
        val chips = ProtectionChips.of(TunnelState.Connecting, lockdown = null)
        assertEquals(listOf(ChipLook.Waiting, ChipLook.Ok, ChipLook.Ok, ChipLook.Warning), chips.map { it.look })
        assertEquals(listOf(R.string.check_all_waiting, R.string.check_dns_ok, R.string.check_ipv6_ok, R.string.android_check_ks_system), chips.map { it.value })
        assertEquals(chips, ProtectionChips.of(TunnelState.Stopping, lockdown = null))
    }

    @Test
    fun `reconnecting waits and keeps what android reported about the kill switch`() {
        // Карта туннеля осталась — трафик ждет, а не идет напрямую; Android сообщил о блоке при подключении.
        val chips = ProtectionChips.of(TunnelState.Reconnecting, lockdown = true)
        assertEquals(listOf(ChipLook.Waiting, ChipLook.Ok, ChipLook.Ok, ChipLook.Ok), chips.map { it.look })
        assertEquals(R.string.check_ks_ok, chips.last().value)
        assertEquals(ChipLook.Off to R.string.check_ks_off, ProtectionChips.of(TunnelState.Reconnecting, lockdown = false).last().let { it.look to it.value })
        val reconnecting = StateLook.of(TunnelState.Reconnecting)
        assertEquals(R.string.status_reconnecting_title, reconnecting.title)
        assertEquals(R.string.power_label_reconnecting, reconnecting.power)
        assertEquals(StateLook.of(TunnelState.Connecting).ring, reconnecting.ring, "вид подключения, как у десктопа")
    }

    @Test
    fun `leak found by the check opens all traffic and nothing else`() {
        val chips = ProtectionChips.of(TunnelState.Connected, lockdown = true, leak = true)
        assertEquals(ProtectionChip("all", R.string.check_all_title, R.string.check_all_same_ip, ChipLook.Warning, R.string.check_all_same_ip_text), chips.first())
        assertEquals(ProtectionChips.of(TunnelState.Connected, lockdown = true).drop(1), chips.drop(1))
        // Утечка — только под защитой: без туннеля выход не меряется.
        assertEquals(ProtectionChips.of(TunnelState.Off, lockdown = true), ProtectionChips.of(TunnelState.Off, lockdown = true, leak = true))
    }

    @Test
    fun `every chip explains itself and has an id for the info under the grid`() {
        val chips = ProtectionChips.of(TunnelState.Connected, lockdown = true)
        assertEquals(listOf("all", "dns", "ipv6", "ks"), chips.map { it.id })
        assertEquals(listOf(R.string.check_all_text, R.string.check_dns_text, R.string.check_ipv6_text, R.string.android_check_ks_text), chips.map { it.text })
    }

    @Test
    fun `every state has its own look and label`() {
        assertEquals(R.string.power_label_connected, StateLook.of(TunnelState.Connected).power)
        assertEquals(R.string.power_label_off, StateLook.of(TunnelState.Off).power)
        assertEquals(R.string.power_label_error, StateLook.of(TunnelState.Error).power)
        assertEquals(R.string.status_error_title_mobile, StateLook.of(TunnelState.Error).title)
        assertEquals(StateLook.of(TunnelState.Connecting), StateLook.of(TunnelState.Stopping))
    }

    @Test
    fun `home view knows the android block and the leak`() {
        assertEquals(ProtectionView.Strict, ProtectionView.of(TunnelState.Off, closed = true, leak = false))
        assertEquals(ProtectionView.Blocked, ProtectionView.of(TunnelState.Error, closed = true, leak = false))
        assertEquals(ProtectionView.Error, ProtectionView.of(TunnelState.Error, closed = false, leak = false))
        assertEquals(ProtectionView.Leak, ProtectionView.of(TunnelState.Connected, closed = false, leak = true))
        assertEquals(ProtectionView.Connecting, ProtectionView.of(TunnelState.Stopping, closed = false, leak = false))

        // Блокировка — оранжевый тон и подпись «Блокировка»; строгий режим Android — свой заголовок.
        val strict = StateLook.of(ProtectionView.Strict)
        assertEquals(R.string.power_label_blocked, strict.power)
        assertEquals(R.string.status_strict_title, strict.title)
        assertEquals(PgState.Blocked.Ring, strict.ring)
        assertEquals(R.string.status_blocked_title, StateLook.of(ProtectionView.Blocked).title)
        // Утечка — тон «Ошибки», подпись своя.
        val leak = StateLook.of(ProtectionView.Leak)
        assertEquals(R.string.power_label_leak, leak.power)
        assertEquals(R.string.status_leak_title, leak.title)
        assertEquals(StateLook.of(ProtectionView.Error).ring, leak.ring)
    }

    @Test
    fun `leak needs the exit of this connection equal to the real ip`() {
        val status = TunnelStatus(TunnelState.Connected, serverId = "nl", since = 5L)
        val ip = WebIp(exit = "198.51.100.7", direct = "198.51.100.7", session = 5L)
        assertEquals(ProtectionView.Leak, MainUi(status, ip = ip).view)
        assertEquals("198.51.100.7", MainUi(status, ip = ip).webAddress)
        // Выход прежнего подключения не в счет, другой адрес — не утечка, реального нет — сравнивать не с чем.
        assertEquals(ProtectionView.Connected, MainUi(status.copy(since = 6L), ip = ip).view)
        assertNull(MainUi(status.copy(since = 6L), ip = ip).webAddress)
        assertEquals(ProtectionView.Connected, MainUi(status, ip = ip.copy(exit = "203.0.113.24")).view)
        assertEquals(ProtectionView.Connected, MainUi(status, ip = ip.copy(direct = null)).view)
        // Без защиты сайты видят реальный IP, в подключении — никакой.
        assertEquals("198.51.100.7", MainUi(ip = ip).webAddress)
        assertNull(MainUi(TunnelStatus(TunnelState.Connecting), ip = ip).webAddress)
        assertNull(MainUi(blocked = true, ip = ip).webAddress)
    }

    @Test
    fun `server in use is the working one while protection is on`() {
        val nl = item("nl")
        val fi = item("fi")
        val working = MainUi(TunnelStatus(TunnelState.Connected, serverId = "fi"), listOf(nl, fi), selectedId = "nl")
        assertEquals(fi, working.inUse)
        assertEquals(nl, working.copy(status = TunnelStatus()).inUse)
        assertEquals(nl, working.copy(status = TunnelStatus(TunnelState.Connected, serverId = "gone")).inUse)
        assertNull(MainUi().inUse)
    }

    private fun item(id: String) = ServerItem(id, id.uppercase(), id.uppercase(), ProxyProtocol.Vless, TransportSecurity.Reality, TransportKind.Tcp, 443)
}

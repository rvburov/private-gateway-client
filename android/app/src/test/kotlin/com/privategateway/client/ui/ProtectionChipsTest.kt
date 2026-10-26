package com.privategateway.client.ui

import com.privategateway.client.R
import com.privategateway.client.core.servers.ProxyProtocol
import com.privategateway.client.core.servers.ServerItem
import com.privategateway.client.core.servers.TransportKind
import com.privategateway.client.core.servers.TransportSecurity
import com.privategateway.client.vpn.TunnelState
import com.privategateway.client.vpn.TunnelStatus
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
    fun `without the tunnel traffic goes direct and the kill switch is in android settings`() {
        for (state in listOf(TunnelState.Off, TunnelState.Error)) {
            val chips = ProtectionChips.of(state, lockdown = true)
            assertEquals(listOf(ChipLook.Off, ChipLook.Off, ChipLook.Off, ChipLook.Warning), chips.map { it.look }, state.name)
            assertEquals(R.string.check_all_off, chips.first().value)
            assertEquals(R.string.android_check_ks_system, chips.last().value)
        }
    }

    @Test
    fun `connecting waits`() {
        val chips = ProtectionChips.of(TunnelState.Connecting, lockdown = null)
        assertEquals(listOf(ChipLook.Waiting, ChipLook.Waiting, ChipLook.Waiting, ChipLook.Warning), chips.map { it.look })
    }

    @Test
    fun `every state has its own look and label`() {
        assertEquals(R.string.power_label_connected, StateLook.of(TunnelState.Connected).power)
        assertEquals(R.string.power_label_off, StateLook.of(TunnelState.Off).power)
        assertEquals(R.string.power_label_error, StateLook.of(TunnelState.Error).power)
        assertEquals(StateLook.of(TunnelState.Connecting), StateLook.of(TunnelState.Stopping))
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

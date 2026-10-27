package com.privategateway.client.core.vpn

import org.junit.jupiter.api.Test
import org.junit.jupiter.params.ParameterizedTest
import org.junit.jupiter.params.provider.EnumSource
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class SystemVpnTest {
    private val remembered = SystemVpn(alwaysOn = true, lockdown = true)

    @Test
    fun `under protection the flags are what android says now`() {
        val status = TunnelStatus(TunnelState.Connected, alwaysOn = false, lockdown = false)
        assertEquals(SystemVpnView(SystemVpn(false, false), SystemVpnSource.Now), SystemVpnView.of(status, remembered))

        val reconnecting = TunnelStatus(TunnelState.Reconnecting, alwaysOn = true, lockdown = false, attempt = 2)
        assertEquals(SystemVpnSource.Now, SystemVpnView.of(reconnecting, SystemVpn()).source, "карта туннеля та же — флаги те же")
    }

    @ParameterizedTest
    @EnumSource(TunnelState::class, names = ["Off", "Connecting", "Stopping", "Error"])
    fun `without protection the flags are the remembered ones`(state: TunnelState) {
        // Подключение и отключение — флагов в состоянии нет; даже если бы были, без работающего туннеля они не «сейчас».
        val status = TunnelStatus(state, alwaysOn = false, lockdown = false)
        assertEquals(SystemVpnView(remembered, SystemVpnSource.LastConnection), SystemVpnView.of(status, remembered))
    }

    @Test
    fun `android 9 or never connected is unknown`() {
        assertEquals(SystemVpnView(SystemVpn(), SystemVpnSource.Unknown), SystemVpnView.of(TunnelStatus(), SystemVpn()))
        // Android 9: под защитой флагов нет — показать запомненное нечего.
        assertEquals(SystemVpnSource.Unknown, SystemVpnView.of(TunnelStatus(TunnelState.Connected), SystemVpn()).source)
        assertEquals(SystemVpnSource.LastConnection, SystemVpnView.of(TunnelStatus(TunnelState.Connected), remembered).source)
        assertFalse(SystemVpn().known)
        assertTrue(SystemVpn(lockdown = false).known)
    }

    @Test
    fun `closed internet is said only without a tunnel`() {
        assertTrue(SystemVpnView.closedWithoutTunnel(TunnelState.Off, blocked = true))
        assertTrue(SystemVpnView.closedWithoutTunnel(TunnelState.Error, blocked = true))
        assertFalse(SystemVpnView.closedWithoutTunnel(TunnelState.Off, blocked = false))
        for (state in listOf(TunnelState.Connecting, TunnelState.Connected, TunnelState.Reconnecting, TunnelState.Stopping)) {
            assertFalse(SystemVpnView.closedWithoutTunnel(state, blocked = true), state.name)
        }
    }
}

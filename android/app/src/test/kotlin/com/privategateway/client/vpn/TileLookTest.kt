package com.privategateway.client.vpn

import com.privategateway.client.R
import org.junit.Test
import kotlin.test.assertEquals

class TileLookTest {
    @Test
    fun `tile is on while protection goes or starts and says why it is off`() {
        assertEquals(TileLook(true, R.string.status_connected_title), TileLook.of(TunnelState.Connected, hasServer = true))
        assertEquals(TileLook(true, R.string.power_label_connecting), TileLook.of(TunnelState.Connecting, hasServer = true))
        assertEquals(TileLook(false, R.string.power_label_off), TileLook.of(TunnelState.Stopping, hasServer = true))
        assertEquals(TileLook(false, R.string.power_label_error), TileLook.of(TunnelState.Error, hasServer = true))
        assertEquals(TileLook(false, R.string.power_label_off), TileLook.of(TunnelState.Off, hasServer = true))
        // Сервер не выбран: плитка не подключает к «какому-нибудь» — она открывает окно, подпись говорит почему.
        assertEquals(TileLook(false, R.string.power_label_noServer), TileLook.of(TunnelState.Off, hasServer = false))
    }
}

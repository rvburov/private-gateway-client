package com.privategateway.client.core.servers

import org.junit.jupiter.api.Test
import kotlin.test.assertEquals

// Быстрая смена десктопа (MainPageViewModel.QuickServers, MainWindowTests.Choosing_another_server_while_protected_reconnects_to_it).
class QuickSwitchTest {
    private fun server(id: String, latency: Latency?) =
        ServerItem(id, id.uppercase(), id.uppercase(), ProxyProtocol.Vless, TransportSecurity.Reality, TransportKind.Tcp, 443, latency = latency)

    private val nl = server("nl", Latency.of(42))
    private val fi = server("fi", Latency.of(38))
    private val de = server("de", Latency.of(51))
    private val us = server("us", Latency.of(148))
    private val tr = server("tr", Latency.failed(LatencyFailure.NoResponse))
    private val pl = server("pl", null)
    private val all = listOf(us, tr, nl, pl, de, fi)

    @Test
    fun `server in use comes first then the two fastest measured`() {
        assertEquals(3, QuickSwitch.SIZE)
        assertEquals(listOf("nl", "fi", "de"), QuickSwitch.pick(all, nl).map { it.id })
    }

    @Test
    fun `server in use stays first without a measurement and is not repeated`() {
        assertEquals(listOf("tr", "fi", "nl"), QuickSwitch.pick(all, tr).map { it.id }, "у текущего «—», но он первый")
        assertEquals(listOf("fi", "nl", "de"), QuickSwitch.pick(all, fi).map { it.id }, "самый быстрый — он же текущий, второй раз не идет")
    }

    @Test
    fun `without a server in use the three fastest are offered`() {
        assertEquals(listOf("fi", "nl", "de"), QuickSwitch.pick(all, null).map { it.id })
    }

    @Test
    fun `servers without an answer or never measured are left out`() {
        assertEquals(listOf("us"), QuickSwitch.pick(listOf(tr, us, pl), us).map { it.id })
        assertEquals(emptyList(), QuickSwitch.pick(listOf(tr, pl), null))
    }

    @Test
    fun `equal latency keeps the list order`() {
        val twin = server("twin", Latency.of(38))

        assertEquals(listOf("us", "fi", "twin"), QuickSwitch.pick(listOf(fi, twin, nl), us).map { it.id })
    }
}

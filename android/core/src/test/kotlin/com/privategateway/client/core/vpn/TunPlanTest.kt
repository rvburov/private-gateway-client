package com.privategateway.client.core.vpn

import com.privategateway.client.core.xray.CoreSettings
import com.privategateway.client.core.xray.XrayConfig
import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class TunPlanTest {
    @Test
    fun `all ipv4 goes into the tunnel and ipv6 stays closed by android`() {
        val plan = TunPlan.of(CoreSettings())
        assertEquals(listOf(Prefix("172.19.0.1", 30)), plan.addresses)
        assertEquals(listOf(Prefix("0.0.0.0", 0)), plan.routes)
        assertTrue(plan.addresses.none { it.address.contains(':') } && plan.routes.none { it.address.contains(':') })
        assertEquals(listOf("172.19.0.2"), plan.dnsServers)
        assertEquals(1500, plan.mtu)
    }

    @Test
    fun `ipv6 through the server adds an address and the whole ipv6 route`() {
        val plan = TunPlan.of(CoreSettings(ipv6 = true, mtu = 1400))
        assertEquals(listOf(Prefix("172.19.0.1", 30), Prefix("fd66:7067:6763::1", 126)), plan.addresses)
        assertEquals(listOf(Prefix("0.0.0.0", 0), Prefix("::", 0)), plan.routes)
        assertEquals(1400, plan.mtu)
    }

    @Test
    fun `dns of the tunnel lies inside its own network`() {
        // DNS туннеля — второй адрес той же /30: запросы к нему уходят в туннель, а ядро отвечает их через сервер.
        val plan = TunPlan.of(CoreSettings())
        val address = plan.addresses.first()
        fun number(ip: String) = ip.split('.').fold(0L) { acc, part -> acc * 256 + part.toLong() }
        val mask = (0xFFFFFFFFL shl (32 - address.length)) and 0xFFFFFFFFL
        assertEquals(number(address.address) and mask, number(XrayConfig.TUN_DNS) and mask)
        assertEquals(XrayConfig.TUN_DNS, plan.dnsServers.single())
    }

    @Test
    fun `core dns takes the network dns and prefers ipv4`() {
        assertEquals("192.0.2.53:53", CoreDns.endpoint(listOf("2001:db8::53", "192.0.2.53")))
        assertEquals("[2001:db8::53]:53", CoreDns.endpoint(listOf("2001:db8::53%wlan0")))
        assertEquals("[2001:db8::53]:53", CoreDns.endpoint(listOf("2001:db8::53")))
    }

    @Test
    fun `core dns never points into the tunnel or loopback`() {
        assertEquals(CoreDns.FALLBACK, CoreDns.endpoint(emptyList()))
        assertEquals(CoreDns.FALLBACK, CoreDns.endpoint(listOf("172.19.0.2", "127.0.0.53", "::1", "")))
        assertEquals(CoreDns.FALLBACK, CoreDns.endpoint(listOf("not-an-address", "300.1.1.1")))
        assertEquals("198.51.100.1:53", CoreDns.endpoint(listOf("172.19.0.2", "198.51.100.1")))
    }

    @Test
    fun `core dns follows the network under the tunnel and is set only when it changes`() {
        val follower = CoreDnsFollower(listOf("192.168.1.1"))
        assertEquals("192.168.1.1:53", follower.endpoint)
        assertEquals(null, follower.network(listOf("192.168.1.1", "fe80::1%wlan0")), "та же сеть — ядро не трогаем")

        // Wi-Fi пропал — мобильная сеть со своим DNS: роутер Wi-Fi из нее недоступен.
        assertEquals("10.0.0.53:53", follower.network(listOf("10.0.0.53")))
        assertEquals("10.0.0.53:53", follower.endpoint)
        assertEquals(CoreDns.FALLBACK, follower.network(emptyList()), "сеть еще без DNS — запасной")
        assertEquals("192.168.1.1:53", follower.network(listOf("192.168.1.1")), "назад в Wi-Fi")
        assertEquals(null, follower.network(listOf("192.168.1.1")))
    }
}

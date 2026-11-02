package com.privategateway.client.ui

import com.privategateway.client.core.inspect.PhoneNetwork
import com.privategateway.client.core.inspect.PhoneNetworkKind
import com.privategateway.client.vpn.ByteSample
import com.privategateway.client.vpn.TrafficSpeed
import org.junit.Test
import kotlin.test.assertEquals
import kotlin.test.assertNotEquals
import kotlin.test.assertNull

/** Что главная выводит из замеров: сеть под туннелем, скрытый IP, скорость по счетчикам байт. */
class HomeFactsTest {
    private fun network(kind: PhoneNetworkKind, name: String, isDefault: Boolean = false, validated: Boolean = true, address: String = "192.0.2.23/24") =
        PhoneNetwork(kind, name, isDefault, listOf(address), emptyList(), emptyList(), null, null, metered = false, validated = validated, captive = false)

    private val wifi = network(PhoneNetworkKind.Wifi, "wlan0")
    private val cellular = network(PhoneNetworkKind.Cellular, "rmnet0", address = "198.51.100.23/30")
    private val vpn = network(PhoneNetworkKind.Vpn, "tun0", isDefault = true, address = "172.19.0.1/30")

    @Test
    fun `the network under the tunnel is the one android would pick`() {
        // Без VPN — сеть приложения по умолчанию.
        assertEquals(PhoneNetworkKind.Cellular, HomeNetwork.pick(listOf(wifi, cellular.copy(isDefault = true)))?.kind)
        // Под VPN туннель по умолчанию: под ним — проверенная сеть, Wi-Fi раньше сотовой, кабель раньше Wi-Fi.
        assertEquals(PhoneNetworkKind.Wifi, HomeNetwork.pick(listOf(vpn, cellular, wifi))?.kind)
        assertEquals(PhoneNetworkKind.Cellular, HomeNetwork.pick(listOf(vpn, cellular, wifi.copy(validated = false)))?.kind)
        assertEquals(PhoneNetworkKind.Ethernet, HomeNetwork.pick(listOf(vpn, wifi, network(PhoneNetworkKind.Ethernet, "eth0")))?.kind)
        // Только туннель или ничего — сети нет.
        assertNull(HomeNetwork.pick(listOf(vpn)))
        assertNull(HomeNetwork.pick(emptyList()))
    }

    @Test
    fun `another network is another real ip`() {
        val home = HomeNetwork.identity(listOf(wifi))
        assertEquals(home, HomeNetwork.identity(listOf(wifi.copy(isDefault = true), vpn)), "VPN поверх той же сети — та же сеть")
        assertNotEquals(home, HomeNetwork.identity(listOf(wifi.copy(addresses = listOf("203.0.113.5/24")))))
        assertNotEquals(home, HomeNetwork.identity(listOf(cellular)))
        assertNull(HomeNetwork.identity(listOf(vpn)))
    }

    @Test
    fun `hidden ip keeps dots and colons and hides digits with stars`() {
        assertEquals("***.**.***.*", maskIp("198.51.100.7"))
        assertEquals("****:***::*", maskIp("2001:db8::1"))
    }

    @Test
    fun `speed is bits per second between two readings`() {
        // 2 500 000 байт за 2 с — 10 Мбит/с; 250 000 байт — 1 Мбит/с.
        val speed = TrafficSpeed.between(ByteSample(1_000, 500, 10_000), ByteSample(2_501_000, 250_500, 12_000))
        assertEquals(10.0, speed!!.downMbps, 1e-9)
        assertEquals(1.0, speed.upMbps, 1e-9)
    }

    @Test
    fun `without two readings or with a reset counter the speed is unknown`() {
        val first = ByteSample(1_000, 1_000, 10_000)
        assertNull(TrafficSpeed.between(null, first))
        assertNull(TrafficSpeed.between(first, null))
        assertNull(TrafficSpeed.between(first, first), "время не шло")
        assertNull(TrafficSpeed.between(first, ByteSample(10, 2_000, 11_000)), "счетчик начался заново")
    }
}

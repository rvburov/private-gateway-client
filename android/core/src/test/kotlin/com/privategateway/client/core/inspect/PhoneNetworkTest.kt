package com.privategateway.client.core.inspect

import org.junit.jupiter.api.Test
import kotlin.test.assertEquals

class PhoneNetworkTest {
    private fun network(kind: PhoneNetworkKind, name: String?, default: Boolean = false) =
        PhoneNetwork(kind, name, default, emptyList(), emptyList(), emptyList(), null, null, metered = false, validated = true, captive = false)

    @Test
    fun `default network comes first then vpn wifi cellular and the rest by name`() {
        val list = listOf(
            network(PhoneNetworkKind.Other, "dummy0"),
            network(PhoneNetworkKind.Cellular, "rmnet1"),
            network(PhoneNetworkKind.Wifi, "wlan0"),
            network(PhoneNetworkKind.Cellular, "rmnet0"),
            network(PhoneNetworkKind.Vpn, "tun0", default = true),
            network(PhoneNetworkKind.Ethernet, null),
        )
        assertEquals(listOf("tun0", "wlan0", "rmnet0", "rmnet1", null, "dummy0"), PhoneNetworks.order(list).map { it.name })

        val withoutVpn = list.filter { it.kind != PhoneNetworkKind.Vpn }.map { if (it.name == "rmnet1") it.copy(isDefault = true) else it }
        assertEquals("rmnet1", PhoneNetworks.order(withoutVpn).first().name, "сеть по умолчанию — первой, какого бы вида ни была")
    }
}

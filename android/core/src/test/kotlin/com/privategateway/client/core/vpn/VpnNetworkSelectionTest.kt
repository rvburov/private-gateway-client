package com.privategateway.client.core.vpn

import org.junit.jupiter.api.Test
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class VpnNetworkSelectionTest {
    private val ours = VpnNetworkIdentity(8, true, 10001, "tun1", setOf("172.19.0.1"))
    private fun selection(previous: Long? = null) = VpnNetworkSelection(setOf(1, 2), previous, 10001, setOf("172.19.0.1"))

    @Test
    fun `another vpn or physical network is never a protected route`() {
        assertTrue(selection().accepts(ours, prepared = true))
        assertFalse(selection().accepts(ours.copy(ownerUid = 10002), prepared = true))
        assertFalse(selection().accepts(ours.copy(ownerUid = -1), prepared = true))
        assertFalse(selection().accepts(ours.copy(vpn = false), prepared = true))
        assertFalse(selection().accepts(ours.copy(interfaceName = null), prepared = true))
        assertFalse(selection().accepts(ours.copy(addresses = setOf("10.0.0.1")), prepared = true))
        assertFalse(selection().accepts(ours, prepared = false))
    }

    @Test
    fun `before android eleven an old unknown vpn is rejected even if permission is ours`() {
        val unknown = ours.copy(ownerUid = null)
        assertTrue(selection().accepts(unknown, prepared = true))
        assertFalse(selection().accepts(unknown.copy(id = 1), prepared = true))
        assertFalse(selection().accepts(unknown.copy(id = 2), prepared = true))
        assertFalse(selection().accepts(unknown, prepared = false))
        assertTrue(selection(previous = 2).accepts(unknown.copy(id = 2), prepared = true), "передача карты может сохранить нашу подтвержденную сеть")
    }
}

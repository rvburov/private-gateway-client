package com.privategateway.client.core.subscriptions

import org.junit.jupiter.params.ParameterizedTest
import org.junit.jupiter.params.provider.CsvSource
import kotlin.test.assertEquals

// Случай десктопа (SubscriptionUpdateTests.Only_via_vpn_waits_for_protection_through_the_real_service) без «службы нет».
class UpdateGateTest {
    @ParameterizedTest
    @CsvSource(
        // только через VPN, защита подключена, уже обновляется → что мешает
        "true, false, false, ProtectionOff",
        "true, false, true, ProtectionOff",
        "true, true, false, None",
        "true, true, true, Busy",
        "false, false, false, None",
        "false, false, true, Busy",
        "false, true, false, None",
    )
    fun `only via vpn waits for protection and a running update is not started twice`(onlyViaVpn: Boolean, connected: Boolean, busy: Boolean, block: UpdateBlock) {
        assertEquals(block, UpdateGate.block(onlyViaVpn, connected, busy))
    }

    @ParameterizedTest
    @CsvSource("true, false, ProtectionOff", "true, true, None", "false, false, None")
    fun `scheduled check looks only at protection`(onlyViaVpn: Boolean, connected: Boolean, block: UpdateBlock) {
        assertEquals(block, UpdateGate.block(onlyViaVpn, connected))
    }
}

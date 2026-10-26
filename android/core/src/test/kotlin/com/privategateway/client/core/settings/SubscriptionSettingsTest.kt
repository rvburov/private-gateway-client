package com.privategateway.client.core.settings

import com.privategateway.client.core.subscriptions.RefreshInterval
import org.junit.jupiter.api.Test
import org.junit.jupiter.params.ParameterizedTest
import org.junit.jupiter.params.provider.EnumSource
import org.junit.jupiter.params.provider.ValueSource
import kotlin.test.assertEquals

class SubscriptionSettingsTest {
    @Test
    fun `defaults are as on the desktop`() {
        assertEquals(SubscriptionSettings(RefreshInterval.Every12Hours, onlyViaVpn = true, notifySelected = true), SubscriptionSettings())
        assertEquals(SubscriptionSettings(), SubscriptionSettings.read(null, null, null), "ничего не сохранено — значения по умолчанию")
    }

    @ParameterizedTest
    @EnumSource(RefreshInterval::class)
    fun `saved values read back`(refresh: RefreshInterval) {
        assertEquals(SubscriptionSettings(refresh, onlyViaVpn = false, notifySelected = false), SubscriptionSettings.read(refresh.name, false, false))
    }

    @ParameterizedTest
    @ValueSource(strings = ["Every48Hours", "every6hours", "2", ""])
    fun `unknown interval is the default and the rest is kept`(refresh: String) {
        assertEquals(SubscriptionSettings(RefreshInterval.Every12Hours, onlyViaVpn = false, notifySelected = true), SubscriptionSettings.read(refresh, false, null))
    }
}

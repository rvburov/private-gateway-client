package com.privategateway.client.core.subscriptions

import org.junit.jupiter.api.Test
import org.junit.jupiter.params.ParameterizedTest
import org.junit.jupiter.params.provider.CsvSource
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

// Случаи десктопа (SubscriptionUpdateTests: адрес для подписи).
class SubscriptionAddressTest {
    @ParameterizedTest
    @CsvSource(
        "https://example.com/sub/0123456789abcdef, https://example.com/sub/••••",
        "https://example.org/clash/abc?token=1, https://example.org/clash/••••",
        "https://sub.example.com/api/v1/client/subscribe?token=fake, https://sub.example.com/api/••••",
        "https://example.com/AbCdEf0123456789xyz/clash, https://example.com/••••",
        "https://example.com/0123456789abcdef, https://example.com/••••",
        "https://example.com/AbCdEfGhIjKlMnOpQr/clash, https://example.com/••••",
        "https://example.com/?token=fake, https://example.com/••••",
        "https://user:pass@example.com:8443/sub, https://example.com:8443/sub",
        "http://203.0.113.5:2096/sub/fake-id, http://203.0.113.5:2096/sub/••••",
        "https://example.com, https://example.com",
        "https://example.com:443/sub#x, https://example.com/sub/••••",
    )
    fun `mask keeps the host and a word and hides the rest`(url: String, expected: String) {
        assertEquals(expected, SubscriptionAddress.mask(assertNotNull(SubscriptionAddress.parse(url))))
    }

    @Test
    fun `only http and https with a host are subscriptions`() {
        assertNotNull(SubscriptionAddress.parse("  https://example.com/sub  "))
        assertNull(SubscriptionAddress.parse("vless://11111111-2222-3333-4444-555555555555@example.com:443"))
        assertNull(SubscriptionAddress.parse("https:///nohost"))
        assertNull(SubscriptionAddress.parse("not a url"))
        assertNull(SubscriptionAddress.parse("ftp://example.com/sub"))
    }

    @Test
    fun `http subscription is insecure`() {
        assertTrue(SubscriptionAddress.insecure(SubscriptionAddress.parse("http://example.com/sub")!!))
        assertFalse(SubscriptionAddress.insecure(SubscriptionAddress.parse("HTTPS://example.com/sub")!!))
    }
}

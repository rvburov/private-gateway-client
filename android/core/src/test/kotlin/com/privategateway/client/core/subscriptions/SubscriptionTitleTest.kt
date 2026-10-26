package com.privategateway.client.core.subscriptions

import org.junit.jupiter.api.Test
import org.junit.jupiter.params.ParameterizedTest
import org.junit.jupiter.params.provider.CsvSource
import kotlin.test.assertEquals
import kotlin.test.assertNull

// Случаи десктопа (SubscriptionDownloaderTests.Title_comes_from_profile_title_header).
class SubscriptionTitleTest {
    @ParameterizedTest
    @CsvSource(
        "Office VPN, Office VPN",
        "'  base64:0J3QvtGD0YLQsdGD0Lo=  ', Ноутбук",
        "BASE64:0J3QvtGD0YLQsdGD0Lo, Ноутбук",
        "base64:not base64!, ",
        "base64:AQID, ",
        "CafÃ©, ",
        "'   ', ",
        nullValues = [""],
    )
    fun `title comes from the profile title header`(header: String?, title: String?) {
        assertEquals(title, SubscriptionTitle.from(header))
    }

    @Test
    fun `missing header and long titles`() {
        assertNull(SubscriptionTitle.from(null))
        assertEquals("a".repeat(100), SubscriptionTitle.from("a".repeat(150)))
    }
}

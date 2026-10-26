package com.privategateway.client.core.text

import org.junit.jupiter.params.ParameterizedTest
import org.junit.jupiter.params.provider.CsvSource
import kotlin.test.assertEquals

class PluralTest {
    // Случаи десктопа (LocalizationTests.Russian_plural_follows_the_last_digits) и края второго десятка.
    @ParameterizedTest
    @CsvSource("1, one", "2, few", "4, few", "5, many", "11, many", "12, many", "14, many", "21, one", "22, few", "25, many", "101, one", "111, many", "112, many", "0, many")
    fun `russian follows the last digits`(count: Int, form: String) {
        assertEquals(form, Plural.form(count, "ru"))
    }

    @ParameterizedTest
    @CsvSource("1, one", "2, many", "21, many", "0, many")
    fun `english has one and other`(count: Int, form: String) {
        assertEquals(form, Plural.form(count, "en"))
    }
}

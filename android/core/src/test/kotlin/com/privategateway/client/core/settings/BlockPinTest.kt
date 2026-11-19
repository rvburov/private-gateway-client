package com.privategateway.client.core.settings

import org.junit.jupiter.api.Test
import org.junit.jupiter.params.ParameterizedTest
import org.junit.jupiter.params.provider.CsvSource
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotEquals
import kotlin.test.assertNull
import kotlin.test.assertTrue

/** PIN-код блока сайтов (решение 742) — как `BlockPinTests` десктопа. */
class BlockPinTest {
    @ParameterizedTest
    @CsvSource("1234, true", "12345678, true", "123, false", "123456789, false", "12a4, false", "'', false")
    fun `pin is four to eight digits`(pin: String, valid: Boolean) {
        assertEquals(valid, BlockPin.valid(pin))
    }

    @Test
    fun `only the same code matches and the record keeps no code`() {
        val pin = BlockPin.create("2468")
        assertTrue(pin.matches("2468"))
        assertFalse(pin.matches("2469"))
        assertFalse(pin.matches(null))
        assertFalse(pin.toString().contains("2468"))
        assertNotEquals(pin, BlockPin.create("2468"), "соль у каждого кода своя")
        assertEquals(pin, BlockPin.parse(pin.toString()))
        assertNull(BlockPin.parse(""))
        assertNull(BlockPin.parse("broken"))
        assertFalse(BlockPin("!!", "??").matches("2468"), "испорченная запись не пускает и не падает")
    }

    @Test
    fun `five wrong pins pause the attempts for a minute`() {
        var now = 0L
        val attempts = BlockPinAttempts { now }
        val pin = BlockPin.create("1234")
        repeat(BlockPinAttempts.ATTEMPTS - 1) { assertEquals(PinCheck.Wrong, attempts.check("9999", pin)) }
        assertEquals(PinCheck.Paused, attempts.check("9999", pin))
        assertEquals(PinCheck.Paused, attempts.check("1234", pin), "верный код в паузе не принимается")
        now += BlockPinAttempts.PAUSE_MS
        assertEquals(PinCheck.Right, attempts.check("1234", pin))
        assertEquals(PinCheck.Right, attempts.check("", null), "кода нет — пускает")
    }
}

package com.privategateway.client.core.vpn

import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/** Проверка профиля до «Защищено» (решение 556): ответила одна из двух проб — связь есть; ни одна — «Сервер не отвечает». */
class StartCheckTest {
    private fun run(answers: List<Boolean>): Pair<Boolean, Int> {
        var calls = 0
        var result = false
        runTest {
            result = StartCheck.answers(pause = {}) {
                calls++
                answers.getOrElse(calls - 1) { true }
            }
        }

        return result to calls
    }

    @Test
    fun `first answer is enough`() {
        assertEquals(true to 1, run(listOf(true)))
    }

    @Test
    fun `one silent probe is forgiven`() {
        val (answered, calls) = run(listOf(false, true))
        assertTrue(answered)
        assertEquals(2, calls)
    }

    @Test
    fun `a profile that never answers fails after two probes, not forever`() {
        val (answered, calls) = run(listOf(false, false, true))
        assertFalse(answered)
        assertEquals(StartCheck.TRIES, calls)
    }
}

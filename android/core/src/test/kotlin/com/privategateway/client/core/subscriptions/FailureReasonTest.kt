package com.privategateway.client.core.subscriptions

import org.junit.jupiter.api.Test
import org.junit.jupiter.params.ParameterizedTest
import org.junit.jupiter.params.provider.CsvSource
import kotlin.test.assertEquals
import kotlin.test.assertNull

// Правила `FailureText` десктопа: 404, 401 и 403 и остальные коды — разными словами; у прочих причин код не важен.
class FailureReasonTest {
    @ParameterizedTest
    @CsvSource("400", "402", "500", "502")
    fun `other answers keep their code`(code: Int) {
        assertEquals(FailureReason.Status(code), FailureReason.of(DownloadFailure.Status, code))
    }

    @Test
    fun `not found and forbidden are told apart`() {
        assertEquals(FailureReason.NotFound, FailureReason.of(DownloadFailure.Status, 404))
        assertEquals(FailureReason.Forbidden(401), FailureReason.of(DownloadFailure.Status, 401))
        assertEquals(FailureReason.Forbidden(403), FailureReason.of(DownloadFailure.Status, 403))
        assertEquals(FailureReason.Network, FailureReason.of(DownloadFailure.Status, null), "без кода «сервер ответил» сказать нечего")
    }

    @Test
    fun `each failure has its own reason and the code does not matter outside status`() {
        val expected = mapOf(
            DownloadFailure.Network to FailureReason.Network,
            DownloadFailure.NameResolution to FailureReason.NameResolution,
            DownloadFailure.Connection to FailureReason.Connection,
            DownloadFailure.Tls to FailureReason.Tls,
            DownloadFailure.Timeout to FailureReason.Timeout,
            DownloadFailure.Status to FailureReason.Status(500),
            DownloadFailure.TooLarge to FailureReason.TooLarge,
            DownloadFailure.NoServers to FailureReason.NoServers,
            DownloadFailure.NoAddress to FailureReason.NoAddress,
        )

        assertEquals(DownloadFailure.entries.toSet(), expected.keys)
        for ((failure, reason) in expected) {
            assertEquals(reason, FailureReason.of(failure, 500), failure.name)
            if (failure != DownloadFailure.Status) {
                assertEquals(reason, FailureReason.of(failure, 404), failure.name)
            }
        }
    }

    @Test
    fun `stored failure reads back and unknown names are a network error`() {
        assertNull(DownloadFailure.parse(null))
        DownloadFailure.entries.forEach { assertEquals(it, DownloadFailure.parse(it.name)) }
        assertEquals(DownloadFailure.Network, DownloadFailure.parse("Gone"))
    }
}

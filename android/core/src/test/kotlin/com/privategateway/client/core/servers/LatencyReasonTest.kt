package com.privategateway.client.core.servers

import org.junit.jupiter.api.Test
import org.junit.jupiter.params.ParameterizedTest
import org.junit.jupiter.params.provider.CsvSource
import kotlin.test.assertEquals
import kotlin.test.assertNull

/** Почему через сервер нет ответа — по тексту ошибки ядра, как `LatencyDiagnostic` десктопа. */
class LatencyReasonTest {
    @ParameterizedTest
    @CsvSource(
        "'context deadline exceeded', Timeout",
        "'i/o timeout', Timeout",
        "'dial tcp: lookup example.com: no such host', Dns",
        "'remote error: tls: handshake failure', Tls",
        "'x509: certificate signed by unknown authority', Tls",
        "'REALITY: processed invalid connection', Tls",
        "'dial tcp 203.0.113.24:443: connection refused', Connection",
        "'read: connection reset by peer', Connection",
        "'EOF', Connection",
        "'network is unreachable', Connection",
    )
    fun `error text tells the reason`(text: String, reason: LatencyReason) {
        assertEquals(reason, LatencyReason.of(text))
    }

    @Test
    fun `unknown text is no reason and a core timeout is a timeout`() {
        assertNull(LatencyReason.of("something odd"))
        assertNull(LatencyReason.of(null))
        assertEquals(LatencyReason.Timeout, LatencyReason.of(null, timedOut = true))
    }

    @Test
    fun `reason survives the store and old records read as before`() {
        val timeout = Latency.failed(LatencyFailure.NoResponse, LatencyReason.Timeout)
        assertEquals("NoResponse/Timeout", timeout.stored)
        assertEquals(timeout, Latency.read("NoResponse/Timeout"))
        assertEquals(Latency.failed(LatencyFailure.CoreFailed), Latency.read("CoreFailed"), "прежняя запись без причины")
        assertEquals(Latency.failed(LatencyFailure.NoResponse), Latency.read("Future/Thing"), "неизвестное — «нет ответа»")
        assertNull(Latency.of(41).stored)
    }
}

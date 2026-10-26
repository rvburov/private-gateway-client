package com.privategateway.client.core.servers

import org.junit.jupiter.params.ParameterizedTest
import org.junit.jupiter.params.provider.CsvSource
import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class ServerFactsTest {
    // Те же случаи, что у десктопа (ServerListTests.Summary_names_what_distinguishes_the_server).
    @ParameterizedTest
    @CsvSource(
        "Vless, Reality, Tcp, VLESS · REALITY",
        "Vless, Reality, Grpc, VLESS · REALITY",
        "Vless, Tls, Xhttp, VLESS · XHTTP",
        "Vless, Tls, Grpc, VLESS · gRPC",
        "Vless, Tls, Tcp, VLESS · TLS",
        "Vless, None, Tcp, VLESS",
        "Trojan, Tls, WebSocket, Trojan · WS",
        "Trojan, Tls, Tcp, Trojan",
        "Hysteria2, Tls, Quic, Hysteria2",
        "Tuic, Tls, Quic, TUIC v5",
        "WireGuard, None, Udp, WireGuard",
        "Vmess, None, HttpUpgrade, VMess · HTTPUpgrade",
    )
    fun `summary names what distinguishes the server`(protocol: ProxyProtocol, security: TransportSecurity, transport: TransportKind, expected: String) {
        assertEquals(expected, ServerFacts.summary(protocol, security, transport))
    }

    @Test
    fun `vless is encrypted only with tls or reality`() {
        assertFalse(ServerFacts.isEncrypted(ProxyProtocol.Vless, TransportSecurity.None))
        assertTrue(ServerFacts.isEncrypted(ProxyProtocol.Vless, TransportSecurity.Tls))
        assertTrue(ServerFacts.isEncrypted(ProxyProtocol.Vless, TransportSecurity.Reality))
        for (protocol in ProxyProtocol.entries.filter { it != ProxyProtocol.Vless }) {
            assertTrue(ServerFacts.isEncrypted(protocol, TransportSecurity.None), protocol.name)
        }
    }
}

package com.privategateway.client.core.servers

import com.privategateway.client.core.links.LinkParser
import com.privategateway.client.core.xray.CoreRejection
import com.privategateway.client.core.xray.CoreSettings
import com.privategateway.client.core.xray.XrayConfig
import org.junit.jupiter.params.ParameterizedTest
import org.junit.jupiter.params.provider.CsvSource
import org.junit.jupiter.api.Test
import java.io.File
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

class ServerFactsTest {
    // Случаи десктопа (ServerListTests.Summary_names_what_distinguishes_the_server) и VMess с TLS.
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
        // TLS пишется у протоколов, которые бывают и без него: VLESS и VMess, но не Trojan.
        "Vmess, Tls, Tcp, VMess · TLS",
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

    // Адрес карточки — как у десктопа (ServerCardViewModel.FormatAddress): IPv6 в скобках, иначе порт с ним не отличить.
    @ParameterizedTest
    @CsvSource(
        "203.0.113.24, 443, 203.0.113.24:443",
        "example.com, 8443, example.com:8443",
        "2001:db8::10, 8443, [2001:db8::10]:8443",
        "::1, 1, [::1]:1",
        "2001:db8:0:0:0:0:0:1, 443, [2001:db8:0:0:0:0:0:1]:443",
    )
    fun `address puts ipv6 in brackets`(host: String, port: Int, expected: String) {
        assertEquals(expected, ServerFacts.address(host, port))
    }

    @Test
    fun `address of a parsed ipv6 link has brackets once`() {
        val link = File(testData, "links/vless/private-gateway-ipv6.txt").readText().trim()
        val server = LinkParser.parse(link).server!!

        assertEquals("[2001:db8::10]:8443", ServerFacts.address(server.host, server.port))
    }

    @Test
    fun `core name is xray except for protocols android cannot connect`() {
        assertEquals("Xray-core", ServerFacts.coreName(ProxyProtocol.Vless))
        assertEquals("Xray-core", ServerFacts.coreName(ProxyProtocol.WireGuard), "у десктопа — sing-box, на Android — Xray")
        assertNull(ServerFacts.coreName(ProxyProtocol.Hysteria2))
        assertNull(ServerFacts.coreName(ProxyProtocol.Tuic))

        // Сверка с конфигом ядра на всех эталонах: «ядра нет» ровно у тех, от кого Xray отказывается по протоколу.
        val servers = File(testData, "links").walkTopDown().filter { it.extension == "txt" }
            .mapNotNull { LinkParser.parse(it.readText().trim()).server }
            .toList()
        assertEquals(ProxyProtocol.entries.toSet(), servers.map { it.protocol }.toSet(), "эталоны есть на каждый протокол")
        for (server in servers) {
            val refused = XrayConfig.build(server, CoreSettings(), 1).rejection == CoreRejection.UnsupportedProtocol
            assertEquals(refused, ServerFacts.coreName(server.protocol) == null, server.protocol.name)
        }
    }

    private val testData = File(System.getProperty("testdata") ?: error("Нет пути к shared/testdata"))
}

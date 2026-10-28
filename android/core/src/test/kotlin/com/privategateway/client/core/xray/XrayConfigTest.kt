package com.privategateway.client.core.xray

import com.privategateway.client.core.links.LinkParser
import com.privategateway.client.core.servers.SecurityOptions
import com.privategateway.client.core.servers.TransportSecurity
import com.privategateway.client.core.settings.DnsChoice
import com.privategateway.client.core.settings.TunnelSettings
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.boolean
import kotlinx.serialization.json.int
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.jupiter.api.Test
import org.junit.jupiter.params.ParameterizedTest
import org.junit.jupiter.params.provider.CsvSource
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * Конфиг Xray на телефоне: выход сервера — те же поля, что у службы десктопа; вход — TUN с дескриптором от VpnService;
 * DNS из туннеля — через сервер; локальная сеть и IPv6 — по настройкам; журнал соединений выключен.
 */
class XrayConfigTest {
    private val reality = server(
        "vless://11111111-2222-3333-4444-555555555555@203.0.113.24:443?type=tcp&security=reality&sni=www.example.com&fp=chrome" +
            "&pbk=bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb&sid=0123abcd&flow=xtls-rprx-vision#NL",
    )

    @Test
    fun `vless reality goes out through the server and the tunnel comes in with its fd`() {
        val config = build(reality, CoreSettings(), tunFd = 42)

        assertEquals("42", config["env"]!!.jsonObject["xray.tun.fd"]!!.jsonPrimitive.content)
        val tun = config["inbounds"]!!.jsonArray.single().jsonObject
        assertEquals("tun", tun["protocol"]!!.jsonPrimitive.content)
        assertEquals(1500, tun["settings"]!!.jsonObject["mtu"]!!.jsonPrimitive.int)
        assertEquals("none", config["log"]!!.jsonObject["access"]!!.jsonPrimitive.content, "адреса сайтов в журнал не попадают")

        val proxy = outbound(config, "proxy")
        val user = proxy["settings"]!!.jsonObject["vnext"]!!.jsonArray[0].jsonObject["users"]!!.jsonArray[0].jsonObject
        assertEquals("xtls-rprx-vision", user["flow"]!!.jsonPrimitive.content)
        val stream = proxy["streamSettings"]!!.jsonObject
        assertEquals("raw", stream["network"]!!.jsonPrimitive.content)
        val realitySettings = stream["realitySettings"]!!.jsonObject
        assertEquals("www.example.com", realitySettings["serverName"]!!.jsonPrimitive.content)
        assertEquals("chrome", realitySettings["fingerprint"]!!.jsonPrimitive.content)
        assertEquals(listOf("proxy", "direct", "block", "dns-out"), config["outbounds"]!!.jsonArray.map { it.jsonObject["tag"]!!.jsonPrimitive.content })
    }

    @Test
    fun `dns from the tunnel and local network and ipv6 follow the settings`() {
        val closed = rules(build(reality, CoreSettings(), 3))
        assertEquals("dns-out", closed[0]["outboundTag"]!!.jsonPrimitive.content)
        assertEquals("53", closed[0]["port"]!!.jsonPrimitive.content)
        assertEquals("block", closed[1]["outboundTag"]!!.jsonPrimitive.content, "локальная сеть по умолчанию закрыта")
        assertEquals(JsonArray(listOf(JsonPrimitive("::/0"))), closed[2]["ip"], "IPv6 без настройки — в блок")

        val open = build(reality, CoreSettings(allowLan = true, ipv6 = true), 3)
        assertEquals("direct", rules(open)[1]["outboundTag"]!!.jsonPrimitive.content)
        assertEquals(2, rules(open).size)
        assertEquals("UseIP", open["dns"]!!.jsonObject["queryStrategy"]!!.jsonPrimitive.content)
        assertEquals("UseIPv4", build(reality, CoreSettings(), 3)["dns"]!!.jsonObject["queryStrategy"]!!.jsonPrimitive.content)
    }

    @ParameterizedTest
    @CsvSource("192.168.1.53, true", "fd00::53, true", "9.9.9.9, false", "https://192.168.1.53/dns-query, false")
    fun `own dns in a private network is asked directly even with the lan closed`(custom: String, direct: Boolean) {
        val settings = TunnelSettings(dns = DnsChoice.Custom, customDns = custom)
        val rules = rules(build(reality, settings.core(), 3))
        val localBlock = rules.indexOfFirst { it["ip"] == JsonArray(XrayConfig.localNetworks.map(::JsonPrimitive)) }
        assertEquals("block", rules[localBlock]["outboundTag"]!!.jsonPrimitive.content)

        val own = rules.indexOfFirst { it["ip"] == JsonArray(listOf(JsonPrimitive(custom))) }
        if (direct) {
            assertEquals(localBlock - 1, own, "раньше закрытия локальной сети")
            assertEquals("direct", rules[own]["outboundTag"]!!.jsonPrimitive.content)
            assertEquals("53", rules[own]["port"]!!.jsonPrimitive.content)
            assertNull(rules[own]["inboundTag"], "это запросы самого Xray, не программ из туннеля")
            assertEquals(0, rules.indexOfFirst { it["outboundTag"]!!.jsonPrimitive.content == "dns-out" }, "DNS программ — по-прежнему выходом DNS")
            assertEquals(custom, build(reality, settings.core(), 3)["dns"]!!.jsonObject["servers"]!!.jsonArray.single().jsonPrimitive.content)
        } else {
            assertEquals(-1, own)
            assertTrue(rules.none { it["outboundTag"]!!.jsonPrimitive.content == "direct" })
        }
    }

    @Test
    fun `empty lan dns adds no rule`() {
        val plain = rules(build(reality, CoreSettings(), 3))
        assertEquals(plain, rules(build(reality, CoreSettings(lanDns = ""), 3)))
        assertEquals(plain.size + 1, rules(build(reality, CoreSettings(lanDns = "192.168.1.53"), 3)).size, "правило добавляется одно")
    }

    @Test
    fun `each protocol builds the same outbound as the desktop`() {
        val vmess = outbound(build(server("vmess://" + java.util.Base64.getEncoder().encodeToString(
            """{"v":"2","ps":"WS","add":"cdn.example.com","port":"443","id":"11111111-2222-3333-4444-555555555555","aid":"0","net":"ws","path":"/ws","host":"cdn.example.com","tls":"tls","sni":"cdn.example.com","alpn":"h2,http/1.1"}""".toByteArray())),
            CoreSettings(mux = true), 1), "proxy")
        assertEquals("vmess", vmess["protocol"]!!.jsonPrimitive.content)
        val ws = vmess["streamSettings"]!!.jsonObject
        assertEquals("/ws", ws["wsSettings"]!!.jsonObject["path"]!!.jsonPrimitive.content)
        assertEquals(JsonArray(listOf(JsonPrimitive("h2"), JsonPrimitive("http/1.1"))), ws["tlsSettings"]!!.jsonObject["alpn"])
        assertTrue(vmess["mux"]!!.jsonObject["enabled"]!!.jsonPrimitive.boolean, "mux без flow включается")
        assertNull(outbound(build(reality, CoreSettings(mux = true), 1), "proxy")["mux"], "mux несовместим с Vision")

        val trojan = outbound(build(server("trojan://pw@198.51.100.7:8443?type=grpc&serviceName=tunnel&sni=example.org#T"), CoreSettings(), 1), "proxy")
        assertEquals("tunnel", trojan["streamSettings"]!!.jsonObject["grpcSettings"]!!.jsonObject["serviceName"]!!.jsonPrimitive.content)
        assertEquals("pw", trojan["settings"]!!.jsonObject["servers"]!!.jsonArray[0].jsonObject["password"]!!.jsonPrimitive.content)

        val ss = outbound(build(server("ss://2022-blake3-aes-128-gcm:AAAAAAAAAAAAAAAAAAAAAA%3D%3D@[2001:db8::5]:8388#SS"), CoreSettings(), 1), "proxy")
        assertEquals("2022-blake3-aes-128-gcm", ss["settings"]!!.jsonObject["servers"]!!.jsonArray[0].jsonObject["method"]!!.jsonPrimitive.content)

        val wg = outbound(build(server("wireguard://aaaa%3D@192.0.2.33:51820?publickey=cccc%3D&address=10.0.0.2/32&mtu=1420#WG"), CoreSettings(), 1), "proxy")
        assertEquals("192.0.2.33:51820", wg["settings"]!!.jsonObject["peers"]!!.jsonArray[0].jsonObject["endpoint"]!!.jsonPrimitive.content)
        assertEquals(1420, wg["settings"]!!.jsonObject["mtu"]!!.jsonPrimitive.int)
        assertTrue(wg["settings"]!!.jsonObject["noKernelTun"]!!.jsonPrimitive.boolean, "свой стек Xray, а не адаптер wgN — как у десктопа")
    }

    @ParameterizedTest
    @CsvSource(
        "vless, 2001:db8::1, none, true",
        "vless, [2001:db8::1], none, true",
        "vless, vpn.example.com, none, true",
        "vless, vpn.example.com., none, true",
        "trojan, vpn.example.com, none, true",
        "vless, vpn.example.com, tls, false",
        "vless, vpn.example.com, reality, false",
        "vmess, vpn.example.com, none, false",
        "vless, 10.0.0.5, none, false",
        "vless, 192.0.2.1, none, false",
        "vless, ::ffff:192.168.1.5, none, false",
        "vless, fd00::5, none, false",
        "vless, [fd00::5], none, false",
        "vless, nas.lan, none, false",
        "vless, nas.lan., none, false",
        "vless, router.home.arpa, none, false",
        "vless, printer.local, none, false",
        "vless, NAS, none, false",
        "vless, lan.example.com, none, true",
        "vless, mylan, none, false",
        "vless, 010.0.0.1, none, true",
        "vless, 0177.0.0.1, none, true",
        "vless, 1:2:3, none, true",
    )
    fun `server without encryption on an open address is refused as libXray does`(protocol: String, host: String, security: String, refused: Boolean) {
        val base = when (protocol) {
            "trojan" -> server("trojan://pw@198.51.100.7:8443?sni=example.org#T")
            "vmess" -> server("vmess://" + java.util.Base64.getEncoder().encodeToString(
                """{"v":"2","ps":"V","add":"cdn.example.com","port":"443","id":"11111111-2222-3333-4444-555555555555","aid":"0","net":"tcp"}""".toByteArray()))
            else -> server("vless://11111111-2222-3333-4444-555555555555@203.0.113.24:443?security=none#V")
        }
        val tested = base.copy(
            host = host,
            security = when (security) {
                "tls" -> SecurityOptions(TransportSecurity.Tls, sni = "vpn.example.com")
                "reality" -> reality.security
                else -> SecurityOptions.None
            },
        )

        assertEquals(refused, XrayConfig.unencrypted(tested), host)
        val expected = if (refused) CoreRejection.Unencrypted else null
        assertEquals(expected, XrayConfig.build(tested, CoreSettings(), 1).rejection,
            "VLESS и Trojan без TLS к открытому адресу — трафик шел бы открытым; ядро libXray такой выход не строит")
        assertEquals(expected, PingBatch.outbounds(tested, CoreSettings()).rejection, "и замер задержки")
    }

    @Test
    fun `hysteria2 and tuic and missing transports are refused`() {
        assertEquals(CoreRejection.UnsupportedProtocol, XrayConfig.build(server("hy2://pw@192.0.2.1:443#H"), CoreSettings(), 1).rejection)
        assertEquals(
            CoreRejection.UnsupportedProtocol,
            XrayConfig.build(server("tuic://11111111-2222-3333-4444-555555555555:pw@192.0.2.1:443#T"), CoreSettings(), 1).rejection,
        )
        val noUuid = reality.copy(credentials = reality.credentials.copy(uuid = null))
        assertEquals(CoreRejection.MissingCredentials, XrayConfig.build(noUuid, CoreSettings(), 1).rejection)
        val quic = reality.copy(transport = com.privategateway.client.core.servers.TransportOptions.Quic)
        assertEquals(CoreRejection.UnsupportedTransport, XrayConfig.build(quic, CoreSettings(), 1).rejection)
    }

    @Test
    fun `pinned certificate goes to the core and allow insecure does not`() {
        val pinned = server(
            "vless://11111111-2222-3333-4444-555555555555@198.51.100.210:443?security=tls&sni=example.com" +
                "&pcs=A1B2C3D4E5F60718293A4B5C6D7E8F90A1B2C3D4E5F60718293A4B5C6D7E8F90%2C0f1e2d3c4b5a69788796a5b4c3d2e1f00f1e2d3c4b5a69788796a5b4c3d2e1f0&vcn=example.com%2Cexample.org#P",
        )
        val tls = outbound(build(pinned, CoreSettings(), 1), "proxy")["streamSettings"]!!.jsonObject["tlsSettings"]!!.jsonObject
        assertEquals("a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90,0f1e2d3c4b5a69788796a5b4c3d2e1f00f1e2d3c4b5a69788796a5b4c3d2e1f0", tls["pinnedPeerCertSha256"]!!.jsonPrimitive.content)
        assertEquals("example.com,example.org", tls["verifyPeerCertByName"]!!.jsonPrimitive.content)

        val insecure = server("trojan://pw@198.51.100.7:443?sni=example.org&allowInsecure=1#T")
        val plain = outbound(build(insecure, CoreSettings(), 1), "proxy")["streamSettings"]!!.jsonObject["tlsSettings"]!!.jsonObject
        assertFalse("allowInsecure" in plain, "Xray-core 26.x конфиг с allowInsecure не запускает (решение 477)")
        assertFalse("pinnedPeerCertSha256" in plain)
        assertFalse("verifyPeerCertByName" in plain)
    }

    @Test
    fun `fingerprint setting overrides the profile and requests and responses speak api 3`() {
        val tls = server("trojan://pw@198.51.100.7:443?sni=example.org&fp=firefox#T")
        val profile = outbound(build(tls, CoreSettings(), 1), "proxy")["streamSettings"]!!.jsonObject["tlsSettings"]!!.jsonObject
        assertEquals("firefox", profile["fingerprint"]!!.jsonPrimitive.content)
        val chosen = outbound(build(tls, CoreSettings(fingerprint = "safari"), 1), "proxy")["streamSettings"]!!.jsonObject["tlsSettings"]!!.jsonObject
        assertEquals("safari", chosen["fingerprint"]!!.jsonPrimitive.content)

        val request = Json.parseToJsonElement(XrayConfig.runRequest("{\"a\":1}")).jsonObject
        assertEquals(3, request["apiVersion"]!!.jsonPrimitive.int)
        assertEquals("runXray", request["method"]!!.jsonPrimitive.content)
        assertEquals("{\"a\":1}", request["payload"]!!.jsonObject["xrayJson"]!!.jsonPrimitive.content)
        assertEquals(XrayResponse(true, null), XrayResponse.parse("""{"success":true,"data":{},"error":""}"""))
        assertEquals(XrayResponse(false, "boom"), XrayResponse.parse("""{"success":false,"data":null,"error":"boom"}"""))
        assertFalse(XrayResponse.parse("not json").success)
    }

    private fun server(link: String) = LinkParser.parse(link).server ?: error("ссылка не разобралась: ${LinkParser.parse(link).error}")

    private fun build(server: com.privategateway.client.core.servers.ServerDefinition, settings: CoreSettings, tunFd: Int): JsonObject {
        val result = XrayConfig.build(server, settings, tunFd)
        return Json.parseToJsonElement(result.json ?: error("отказ: ${result.rejection}")).jsonObject
    }

    private fun outbound(config: JsonObject, tag: String) =
        config["outbounds"]!!.jsonArray.map { it.jsonObject }.single { it["tag"]!!.jsonPrimitive.content == tag }

    private fun rules(config: JsonObject) = config["routing"]!!.jsonObject["rules"]!!.jsonArray.map { it.jsonObject }
}

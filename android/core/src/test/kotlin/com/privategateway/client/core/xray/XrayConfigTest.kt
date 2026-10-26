package com.privategateway.client.core.xray

import com.privategateway.client.core.links.LinkParser
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

package com.privategateway.client.core.links

import com.privategateway.client.core.servers.ProxyProtocol
import com.privategateway.client.core.servers.ServerDefinition
import com.privategateway.client.core.servers.TransportKind
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.put
import org.junit.jupiter.api.DynamicTest
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.TestFactory
import org.junit.jupiter.params.ParameterizedTest
import org.junit.jupiter.params.provider.CsvSource
import java.io.File
import java.util.Locale
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertTrue

/**
 * Разбор ссылок и подписок на эталонах shared/testdata — тех же, что у десктопа (`LinkParserTests`) и iOS. Результат
 * переводится в тот же JSON-вид, что в .expected.json: пустые и ложные поля не пишутся.
 */
class LinkParserTest {
    private val testData = File(System.getProperty("testdata") ?: error("Нет пути к shared/testdata"))

    @TestFactory
    fun `link parses as in the shared fixture`() = cases("links").map { fixture ->
        DynamicTest.dynamicTest(fixture) {
            val (input, expected) = load(fixture)
            val actual = toJson(LinkParser.parse(input))
            assertEquals(expected, actual, "$fixture: получено $actual")
        }
    }

    @TestFactory
    fun `written link parses back to the same server`() = cases("links").map { fixture ->
        DynamicTest.dynamicTest(fixture) {
            val parsed = LinkParser.parse(load(fixture).first)
            val server = parsed.server ?: return@dynamicTest
            val written = LinkWriter.write(server)
            assertEquals(toJson(parsed), toJson(LinkParser.parse(written)), "$fixture: $written")
        }
    }

    @ParameterizedTest
    @CsvSource(
        delimiter = '|',
        value = [
            "ss://2022-blake3-aes-128-gcm:AAAAAAAAAAAAAAAAAAAAAA%3D%3D@192.0.2.1:8388#SS|ss://2022-blake3-aes-128-gcm:AAAAAAAAAAAAAAAAAAAAAA%3D%3D@192.0.2.1:8388#SS",
            "ss://YWVzLTI1Ni1nY206cHctMQ@192.0.2.1:8388#SS|ss://YWVzLTI1Ni1nY206cHctMQ@192.0.2.1:8388#SS",
            "vless://11111111-2222-3333-4444-555555555555@[2001:db8::1]:443?security=tls&sni=example.com#%F0%9F%87%B3%F0%9F%87%B1%20NL" +
                "|vless://11111111-2222-3333-4444-555555555555@[2001:db8::1]:443?encryption=none&type=tcp&security=tls&sni=example.com#%F0%9F%87%B3%F0%9F%87%B1%20NL",
        ],
    )
    fun `written link has the common form`(link: String, written: String) {
        // Shadowsocks 2022 — открытым текстом (SIP022), обычные шифры — base64url без «=» (SIP002); флаг страны — в названии.
        assertEquals(written, LinkWriter.write(LinkParser.parse(link).server!!))
    }

    @TestFactory
    fun `subscription parses as in the shared fixture`() = cases("subscriptions").map { fixture ->
        DynamicTest.dynamicTest(fixture) {
            val (input, expected) = load(fixture)
            val batch = ImportParser.parse(input)

            // У конфигов sing-box и Clash сверяется весь сервер: поля в них названы иначе, чем в ссылке.
            val whole = batch.format == ImportParser.FORMAT_SINGBOX || batch.format == ImportParser.FORMAT_CLASH
            val actual = buildJsonObject {
                put("format", batch.format)
                put(
                    "lines",
                    JsonArray(
                        batch.lines.map { line ->
                            val server = line.result.server
                            buildJsonObject {
                                put("line", line.number)
                                when {
                                    server == null -> put("error", line.result.error)
                                    whole -> put("server", toJson(line.result))
                                    else -> {
                                        put("protocol", name(server.protocol))
                                        put("name", server.name)
                                    }
                                }
                            }
                        },
                    ),
                )
            }

            assertEquals(expected, actual, "$fixture: получено $actual")
        }
    }

    @Test
    fun `every protocol and error code has a fixture`() {
        val expected = testData.resolve("links").walkTopDown().filter { it.name.endsWith(".expected.json") }
            .map { Json.parseToJsonElement(it.readText()).jsonObject }.toList()

        val protocols = expected.mapNotNull { it["protocol"]?.jsonPrimitive?.content }.toSet()
        assertEquals(ProxyProtocol.entries.map(::name).toSet(), protocols)
        val errors = expected.mapNotNull { it["error"]?.jsonPrimitive?.content }.toSet()
        assertEquals(ImportErrors.all.toSet(), errors)
    }

    @Test
    fun `private gateway formats give one and the same server`() {
        // Название в sing-box — тег «gateway», spx есть только в ссылке; остальное обязано совпасть.
        val servers = listOf("base64", "uri", "sing-box", "mihomo").map { format ->
            val batch = ImportParser.parse(testData.resolve("subscriptions/private-gateway/$format.txt").readText())
            assertEquals(1, batch.lines.size, format)
            val server = assertNotNull(batch.lines[0].result.server, format)
            server.copy(name = "", security = server.security.copy(realitySpiderX = null))
        }

        assertEquals(1, servers.distinct().size, servers.toString())
    }

    @Test
    fun `credentials never show up in text`() {
        val server = LinkParser.parse("trojan://pw-very-secret@example.com:443#X").server!!

        assertFalse(server.toString().contains("pw-very-secret"))
        assertEquals("ServerCredentials { *** }", server.credentials.toString())
    }

    @ParameterizedTest
    @CsvSource(
        delimiter = '|',
        value = ["🇳🇱 Нидерланды 01|NL|Нидерланды 01", "🇺🇸|US|''", "Нидерланды 🇳🇱|''|Нидерланды 🇳🇱", "'  Без флага '|''|Без флага"],
    )
    fun `leading flag becomes country code`(name: String, country: String, rest: String) {
        assertEquals(country to rest, CountryFlag.split(name))
    }

    @Test
    fun `percent decoding keeps plus and broken sequences`() {
        assertEquals("a+b", LinkParser.unescape("a+b"))
        assertEquals("Нидерланды", LinkParser.unescape("%D0%9D%D0%B8%D0%B4%D0%B5%D1%80%D0%BB%D0%B0%D0%BD%D0%B4%D1%8B"))
        assertEquals("%zz%D0", LinkParser.unescape("%zz%D0"), "неверная последовательность остается как есть")
        assertTrue(LinkParser.escape("a b/+=").let { it == "a%20b%2F%2B%3D" })
    }

    private fun cases(folder: String): List<String> =
        testData.resolve(folder).walkTopDown().filter { it.isFile && it.name.endsWith(".txt") }
            .map { it.relativeTo(testData).invariantSeparatorsPath.removeSuffix(".txt") }
            .sorted().toList()

    private fun load(fixture: String): Pair<String, JsonElement> {
        val base = testData.resolve(fixture)
        return File("$base.txt").readText() to Json.parseToJsonElement(File("$base.expected.json").readText())
    }

    private fun name(protocol: ProxyProtocol) = protocol.name.lowercase(Locale.ROOT)

    private fun toJson(result: LinkResult): JsonObject {
        val s: ServerDefinition = result.server ?: return buildJsonObject { put("error", result.error) }
        val json = LinkedHashMap<String, JsonElement>()
        json["name"] = JsonPrimitive(s.name)
        put(json, "country", s.countryCode)
        json["protocol"] = JsonPrimitive(name(s.protocol))
        json["host"] = JsonPrimitive(s.host)
        json["port"] = JsonPrimitive(s.port)

        val transport = LinkedHashMap<String, JsonElement>()
        transport["kind"] = JsonPrimitive(if (s.transport.kind == TransportKind.WebSocket) "ws" else s.transport.kind.name.lowercase(Locale.ROOT))
        put(transport, "path", s.transport.path)
        put(transport, "host", s.transport.host)
        put(transport, "serviceName", s.transport.serviceName)
        json["transport"] = JsonObject(transport)

        val security = LinkedHashMap<String, JsonElement>()
        security["kind"] = JsonPrimitive(s.security.kind.name.lowercase(Locale.ROOT))
        put(security, "sni", s.security.sni)
        put(security, "fingerprint", s.security.fingerprint)
        put(security, "alpn", s.security.alpn)
        if (s.security.allowInsecure) {
            security["allowInsecure"] = JsonPrimitive(true)
        }

        put(security, "realityPublicKey", s.security.realityPublicKey)
        put(security, "realityShortId", s.security.realityShortId)
        put(security, "realitySpiderX", s.security.realitySpiderX)
        put(security, "pinnedPeerCertSha256", s.security.pinnedPeerCertSha256)
        put(security, "verifyPeerCertByName", s.security.verifyPeerCertByName)
        json["security"] = JsonObject(security)

        val options = LinkedHashMap<String, JsonElement>()
        put(options, "flow", s.options.flow)
        put(options, "cipher", s.options.cipher)
        put(options, "obfs", s.options.obfs)
        put(options, "congestionControl", s.options.congestionControl)
        put(options, "peerPublicKey", s.options.peerPublicKey)
        put(options, "addresses", s.options.addresses)
        s.options.mtu?.let { options["mtu"] = JsonPrimitive(it) }
        if (options.isNotEmpty()) {
            json["options"] = JsonObject(options)
        }

        val credentials = LinkedHashMap<String, JsonElement>()
        put(credentials, "uuid", s.credentials.uuid)
        put(credentials, "password", s.credentials.password)
        put(credentials, "obfsPassword", s.credentials.obfsPassword)
        put(credentials, "privateKey", s.credentials.privateKey)
        put(credentials, "presharedKey", s.credentials.presharedKey)
        json["credentials"] = JsonObject(credentials)
        return JsonObject(json)
    }

    private fun put(json: MutableMap<String, JsonElement>, key: String, value: String?) {
        if (!value.isNullOrEmpty()) {
            json[key] = JsonPrimitive(value)
        }
    }

    private fun put(json: MutableMap<String, JsonElement>, key: String, values: List<String>?) {
        if (!values.isNullOrEmpty()) {
            json[key] = JsonArray(values.map { JsonPrimitive(it) })
        }
    }
}

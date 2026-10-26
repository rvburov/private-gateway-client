package com.privategateway.client.core.links

import com.privategateway.client.core.servers.ProxyProtocol
import org.junit.jupiter.api.Test
import org.junit.jupiter.params.ParameterizedTest
import org.junit.jupiter.params.provider.Arguments
import org.junit.jupiter.params.provider.MethodSource
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * Конфиги sing-box и Clash сверх общих эталонов — как у десктопа (`ConfigImportTests`): битый и враждебный текст.
 * Подписка приходит из сети — ни вложенность, ни ссылки-«бомбы» YAML не должны уронить программу или занять память.
 */
class ConfigImportTest {
    private val vless = """{ "type": "vless", "tag": "A", "server": "192.0.2.1", "server_port": 443, "uuid": "11111111-2222-3333-4444-555555555555" }"""

    @Test
    fun `broken json is one error on its line`() {
        val batch = ImportParser.parse("{\n  \"outbounds\": [\n    { \"type\": \"vless\" \n    \"tag\": \"A\" }\n  ]\n}")

        assertEquals(ImportParser.FORMAT_SINGBOX, batch.format)
        assertEquals(listOf(ImportLine(4, LinkResult(null, ImportErrors.BAD_ENCODING))), batch.lines)
    }

    @ParameterizedTest
    @MethodSource("brokenYaml")
    fun `broken yaml is one error on its line`(text: String, line: Int) {
        val batch = ImportParser.parse(text)

        assertEquals(ImportParser.FORMAT_CLASH, batch.format)
        assertEquals(listOf(ImportLine(line, LinkResult(null, ImportErrors.BAD_ENCODING))), batch.lines)
    }

    @Test
    fun `plain array of outbounds and bom are understood`() {
        val batch = ImportParser.parse("﻿[\n$vless\n]")

        assertEquals(ImportParser.FORMAT_SINGBOX, batch.format)
        assertEquals(listOf(2 to ProxyProtocol.Vless), batch.lines.map { it.number to it.result.server?.protocol })
    }

    @Test
    fun `lines go in text order even when endpoints come first`() {
        val batch = ImportParser.parse("{\n\"endpoints\": [{ \"type\": \"socks\" }],\n\"outbounds\": [\n$vless\n]}")

        assertEquals(listOf(2, 4), batch.lines.map { it.number })
    }

    @Test
    fun `hysteria2 and tuic keep tls fields without enabled flag`() {
        val batch = ImportParser.parse(
            """
            [
              { "type": "hysteria2", "server": "192.0.2.1", "server_port": 443, "password": "x", "tls": { "server_name": "hy.example.com" } },
              { "type": "tuic", "server": "192.0.2.1", "server_port": 443, "uuid": "11111111-2222-3333-4444-555555555555", "password": "x",
                "tls": { "server_name": "tuic.example.com" } }
            ]
            """.trimIndent(),
        )

        assertEquals(listOf("hy.example.com", "tuic.example.com"), batch.lines.map { it.result.server?.security?.sni })
    }

    @Test
    fun `deep json is refused not overflowed`() {
        val batch = ImportParser.parse("{\"outbounds\": " + "[".repeat(10_000) + "]".repeat(10_000) + "}")

        assertEquals(ImportErrors.BAD_ENCODING, batch.lines.single().result.error)
    }

    @Test
    fun `deep yaml is refused not overflowed`() {
        val batch = ImportParser.parse("proxies: " + "[".repeat(10_000) + "]".repeat(10_000) + "\n")

        assertEquals(ImportErrors.BAD_ENCODING, batch.lines.single().result.error)
    }

    @Test
    fun `yaml alias bomb is refused before it takes memory`() {
        // Каждый уровень — десять ссылок на предыдущий: 10⁹ узлов, если копировать без счета.
        val text = StringBuilder("a0: &a0 [x, x, x, x, x, x, x, x, x, x]\n")
        for (level in 1..9) {
            text.append("a$level: &a$level [").append(List(10) { "*a${level - 1}" }.joinToString(", ")).append("]\n")
        }

        text.append("proxies: *a9\n")
        val runtime = Runtime.getRuntime()
        val before = runtime.totalMemory() - runtime.freeMemory()

        val batch = ImportParser.parse(text.toString())

        assertEquals(ImportErrors.BAD_ENCODING, batch.lines.single().result.error)
        assertTrue(runtime.totalMemory() - runtime.freeMemory() - before < 500L * 1024 * 1024)
    }

    @Test
    fun `yaml merge keeps explicit keys`() {
        val batch = ImportParser.parse(
            """
            base: &base {type: trojan, port: 1, sni: from-base.example.com}
            proxies:
              - {<<: *base, name: A, server: 192.0.2.1, port: 443, password: x}
            """.trimIndent(),
        )

        val server = assertNotNull(batch.lines.single().result.server)
        assertEquals(Triple(ProxyProtocol.Trojan, 443, "from-base.example.com"), Triple(server.protocol, server.port, server.security.sni))
    }

    @Test
    fun `yaml tilde and null are missing values but quoted ones are text`() {
        val batch = ImportParser.parse(
            """
            proxies:
              - {name: A, type: trojan, server: 192.0.2.1, port: 443, password: x, sni: ~, client-fingerprint: null}
              - {name: B, type: trojan, server: 192.0.2.1, port: 443, password: "~"}
            """.trimIndent(),
        )

        val first = assertNotNull(batch.lines[0].result.server)
        assertNull(first.security.sni)
        assertNull(first.security.fingerprint)
        assertEquals("~", batch.lines[1].result.server?.credentials?.password)
    }

    @Test
    fun `links with a word proxies stay links`() {
        assertEquals(ImportParser.FORMAT_LINKS, ImportParser.parse("trojan://x@192.0.2.1:443#proxies: test").format)
    }

    @Test
    fun `config without servers finds nothing`() {
        assertTrue(ImportParser.parse("""{ "outbounds": [{ "type": "direct" }] }""").lines.isEmpty())
        assertTrue(ImportParser.parse("proxies:\nrules: []\n").lines.isEmpty())
        assertTrue(ImportParser.parse("[]").lines.isEmpty())
    }

    companion object {
        @JvmStatic
        fun brokenYaml() = listOf(
            Arguments.of("proxies:\n  - name: A\n    type: vless\n   bad: x\n", 4),
            Arguments.of("proxies:\n  - name: A\n    port: *nope\n", 3),
            Arguments.of("proxies:\n  - {name: A, type: vless\n  - name: B\n", 2),
        )
    }
}

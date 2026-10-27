package com.privategateway.client.core.xray

import com.privategateway.client.core.links.LinkParser
import com.privategateway.client.core.servers.Latency
import com.privategateway.client.core.servers.LatencyFailure
import com.privategateway.client.core.servers.ServerDefinition
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.int
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.jupiter.api.Test
import java.io.File
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/**
 * Задержка через libXray (`pingBatch`): у каждого сервера — свой конфиг из одного выхода, тот же, что в туннеле; порции
 * не больше пяти, порядок серверов сохраняется; Hysteria2 и TUIC в libXray не уходят; ответ разбирается так же, как
 * результат `LatencyProbe` десктопа: нет ответа, отказ ядра, задержка не меньше 1 мс.
 */
class PingBatchTest {
    private val testData = File(System.getProperty("testdata") ?: error("Нет пути к shared/testdata"))
    private val reality = link("vless/reality-vision")
    private val trojan = link("trojan/ws")
    private val hysteria = link("hysteria2/hy2-short-scheme")
    private val tuic = link("tuic/bbr")

    @Test
    fun `ping config is the tunnel outbound and nothing else`() {
        val settings = CoreSettings(fingerprint = "safari", mux = true)
        val config = json(PingBatch.outbounds(trojan, settings).json ?: error("отказ"))

        assertEquals(setOf("outbounds"), config.keys, "libXray читает только выходы")
        val tunnel = json(XrayConfig.build(trojan, settings, 1).json!!)["outbounds"]!!.jsonArray[0]
        assertEquals(tunnel, config["outbounds"]!!.jsonArray.single(), "тот же выход, что в туннеле, с настройками")
        assertEquals("proxy", tunnel.jsonObject["tag"]!!.jsonPrimitive.content)

        assertEquals(XrayConfigResult(null, CoreRejection.UnsupportedProtocol), PingBatch.outbounds(hysteria, CoreSettings()))
        assertEquals(CoreRejection.MissingCredentials, PingBatch.outbounds(reality.copy(credentials = reality.credentials.copy(uuid = null)), CoreSettings()).rejection)
    }

    @Test
    fun `request is api 3 ping batch with configs timeout and url`() {
        assertEquals(
            """{"apiVersion":3,"method":"pingBatch","payload":{"configs":[{"xrayJson":"{\"outbounds\":[]}","outboundTag":"proxy"}],""" +
                """"timeout":5,"url":"https://cp.cloudflare.com/generate_204"}}""",
            PingBatch.request(listOf("""{"outbounds":[]}""")),
        )
        val custom = json(PingBatch.request(listOf("a", "b"), "http://192.0.2.1/204", 2))["payload"]!!.jsonObject
        assertEquals(2, custom["timeout"]!!.jsonPrimitive.int)
        assertEquals("http://192.0.2.1/204", custom["url"]!!.jsonPrimitive.content)
        assertEquals(listOf("a", "b"), custom["configs"]!!.jsonArray.map { it.jsonObject["xrayJson"]!!.jsonPrimitive.content })

        assertFailsWith<IllegalArgumentException>("больше пяти libXray не примет") { PingBatch.request(List(6) { "{}" }) }
        assertFailsWith<IllegalArgumentException> { PingBatch.request(emptyList()) }
        assertFailsWith<IllegalArgumentException>("срок в целых секундах, больше нуля") { PingBatch.request(listOf("{}"), timeoutSeconds = 0) }
        PingBatch.request(List(5) { "{}" }, timeoutSeconds = 1)
    }

    @Test
    fun `twelve servers go in chunks of five five and two in their order`() {
        val targets = (1..12).map { PingTarget("s$it", reality.copy(port = 1000 + it)) }

        val plan = PingBatch.plan(targets, CoreSettings())

        assertEquals(listOf(5, 5, 2), plan.chunks.map { it.ids.size })
        assertEquals(targets.map { it.id }, plan.chunks.flatMap { it.ids })
        assertEquals(emptyMap(), plan.unsupported)
        for (chunk in plan.chunks) {
            val configs = json(chunk.request)["payload"]!!.jsonObject["configs"]!!.jsonArray.map { it.jsonObject }
            assertEquals(chunk.ids.map { 1000 + it.drop(1).toInt() }, configs.map { port(it["xrayJson"]!!.jsonPrimitive.content) })
            assertTrue(configs.all { it["outboundTag"]!!.jsonPrimitive.content == "proxy" })
        }

        assertEquals(listOf(5), PingBatch.plan(targets.take(5), CoreSettings()).chunks.map { it.ids.size }, "ровно пять — одна порция")
        assertEquals(listOf(5, 1), PingBatch.plan(targets.take(6), CoreSettings()).chunks.map { it.ids.size })
    }

    @Test
    fun `plan measures with the connection settings and passes url and timeout`() {
        val plan = PingBatch.plan(listOf(PingTarget("tr", trojan)), CoreSettings(fingerprint = "safari"), "http://192.0.2.1/204", 3)

        val payload = json(plan.chunks.single().request)["payload"]!!.jsonObject
        assertEquals(3, payload["timeout"]!!.jsonPrimitive.int)
        assertEquals("http://192.0.2.1/204", payload["url"]!!.jsonPrimitive.content)
        val outbound = json(payload["configs"]!!.jsonArray.single().jsonObject["xrayJson"]!!.jsonPrimitive.content)["outbounds"]!!.jsonArray.single()
        val tls = outbound.jsonObject["streamSettings"]!!.jsonObject["tlsSettings"]!!.jsonObject
        assertEquals("safari", tls["fingerprint"]!!.jsonPrimitive.content, "отпечаток из настроек, как при подключении")
    }

    @Test
    fun `hysteria2 and tuic are not sent and get unsupported at once`() {
        val plan = PingBatch.plan(
            listOf(PingTarget("hy", hysteria), PingTarget("nl", reality), PingTarget("tuic", tuic), PingTarget("tr", trojan)),
            CoreSettings(),
        )

        assertEquals(listOf(listOf("nl", "tr")), plan.chunks.map { it.ids })
        val unsupported = Latency.failed(LatencyFailure.Unsupported)
        assertEquals(mapOf("hy" to unsupported, "tuic" to unsupported), plan.unsupported)
        assertFalse(plan.chunks.single().request.contains("pw-hy2-2"), "Hysteria2 в запрос не попал")

        val only = PingBatch.plan(listOf(PingTarget("hy", hysteria)), CoreSettings())
        assertEquals(emptyList(), only.chunks, "одни неподдерживаемые — libXray не нужен")
        assertEquals(setOf("hy"), only.unsupported.keys)
        assertEquals(emptyList(), PingBatch.plan(emptyList(), CoreSettings()).chunks)
    }

    @Test
    fun `answers become latency no response or a failed chunk`() {
        val response = """{"success":true,"data":{"results":[""" +
            """{"success":true,"delay":41},{"success":false,"delay":11000,"error":"timeout"},""" +
            """{"success":false,"delay":10000,"error":"dial tcp 203.0.113.24:443: connection refused"},{"success":true,"delay":0}""" +
            """]},"error":""}"""

        assertEquals(
            listOf(Latency(41, null), noResponse, noResponse, Latency(1, null)),
            PingBatch.parse(response, 4),
        )

        val busy = """{"success":false,"data":null,"error":"pingBatch requires an isolated process without a managed Xray instance"}"""
        assertEquals(List(3) { coreFailed }, PingBatch.parse(busy, 3), "ядро уже запущено в процессе — замера не было")
        val failedWithResults = """{"success":false,"data":{"results":[{"success":true,"delay":41}]},"error":"x"}"""
        assertEquals(listOf(coreFailed), PingBatch.parse(failedWithResults, 1), "отказ вызова главнее результатов")
        assertEquals(List(2) { coreFailed }, PingBatch.parse(null, 2))
        assertEquals(List(2) { coreFailed }, PingBatch.parse("not json", 2))
        assertEquals(List(2) { coreFailed }, PingBatch.parse("""{"success":true,"data":{"results":[{"success":true,"delay":41}]}}""", 2))
        assertEquals(List(1) { coreFailed }, PingBatch.parse("""{"success":true,"data":null,"error":""}""", 1))
    }

    @Test
    fun `error texts of servers come only through errors`() {
        val refused = "dial tcp 203.0.113.24:443: connection refused"
        val response = """{"success":true,"data":{"results":[""" +
            """{"success":true,"delay":41,"error":""},{"success":false,"delay":11000,"error":"timeout"},""" +
            """{"success":false,"delay":10000,"error":"$refused"},{"success":false,"delay":10000}""" +
            """]},"error":""}"""

        assertEquals(listOf(null, "timeout", refused, null), PingBatch.errors(response, 4))
        assertFalse(PingBatch.parse(response, 4).toString().contains("203.0.113.24"), "адрес из ошибки — не в замере")

        val chunk = PingBatch.plan(listOf(PingTarget("a", reality), PingTarget("b", trojan)), CoreSettings()).chunks.single()
        val two = """{"success":true,"data":{"results":[{"success":true,"delay":30},{"success":false,"delay":10000,"error":"$refused"}]}}"""
        assertEquals(mapOf("b" to refused), chunk.errors(two))
        assertFalse(chunk.results(two).toString().contains("203.0.113.24"))

        val busy = """{"success":false,"data":{"results":[{"success":false,"error":"$refused"}]},"error":"busy"}"""
        assertEquals(listOf<String?>(null), PingBatch.errors(busy, 1), "отказ вызова — в самом ответе, не у серверов")
        assertEquals(listOf<String?>(null, null), PingBatch.errors(response, 2), "не столько результатов — ничего")
        assertEquals(listOf<String?>(null), PingBatch.errors("not json", 1))
        assertEquals(emptyMap(), chunk.errors(null))
    }

    @Test
    fun `sentinel delays are never a latency and the success flag decides`() {
        fun one(item: String) = PingBatch.parse("""{"success":true,"data":{"results":[$item]},"error":""}""", 1).single()

        assertEquals(noResponse, one("""{"success":true,"delay":11000}"""), "тайм-аут — не 11 секунд")
        assertEquals(noResponse, one("""{"success":true,"delay":10000}"""), "ошибка — не 10 секунд")
        assertEquals(Latency(9999, null), one("""{"success":true,"delay":9999}"""))
        assertEquals(Latency(10001, null), one("""{"success":true,"delay":10001}"""))
        assertEquals(noResponse, one("""{"success":false,"delay":41}"""))
        assertEquals(noResponse, one("""{"delay":41}"""))
        assertEquals(noResponse, one("""{"success":true}"""))
        assertEquals(noResponse, one("null"))
    }

    @Test
    fun `chunk results keep ids in order`() {
        val chunk = PingBatch.plan(listOf(PingTarget("a", reality), PingTarget("b", trojan), PingTarget("c", reality)), CoreSettings()).chunks.single()
        val response = """{"success":true,"data":{"results":[{"success":true,"delay":30},{"success":false,"delay":11000},{"success":true,"delay":7}]}}"""

        val results = chunk.results(response)

        assertEquals(mapOf("a" to Latency(30, null), "b" to noResponse, "c" to Latency(7, null)), results)
        assertEquals(listOf("a", "b", "c"), results.keys.toList())
        assertEquals(mapOf("a" to coreFailed, "b" to coreFailed, "c" to coreFailed), chunk.results(null))
    }

    @Test
    fun `secrets are only inside xrayJson`() {
        val secrets = listOf("11111111-2222-3333-4444-555555555555", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "pw-trojan-1")
        val plan = PingBatch.plan(listOf(PingTarget("nl", reality), PingTarget("tr", trojan), PingTarget("hy", hysteria)), CoreSettings())
        val request = plan.chunks.single().request

        val root = json(request)
        val payload = root["payload"]!!.jsonObject
        val configs = payload["configs"]!!.jsonArray.map { it.jsonObject }
        secrets.forEach { secret -> assertTrue(configs.any { it["xrayJson"]!!.jsonPrimitive.content.contains(secret) }, "ключ нужен ядру") }
        val stripped = JsonObject(root + ("payload" to JsonObject(payload + ("configs" to JsonArray(configs.map { JsonObject(it - "xrayJson") })))))
        secrets.forEach { assertFalse(stripped.toString().contains(it), "вне xrayJson ключей нет") }

        val texts = plan.chunks.map { it.toString() } + plan.unsupported.toString() +
            PingBatch.parse("""{"success":false,"error":"pingBatch: 11111111-2222-3333-4444-555555555555"}""", 2).toString()
        texts.forEach { text -> secrets.forEach { assertFalse(text.contains(it), "в тексте порции и результатов ключей нет: $text") } }
        assertTrue(plan.chunks.single().toString().contains("nl"))
    }

    private val noResponse = Latency.failed(LatencyFailure.NoResponse)
    private val coreFailed = Latency.failed(LatencyFailure.CoreFailed)

    private fun link(name: String): ServerDefinition {
        val text = testData.resolve("links/$name.txt").readText().trim()
        return LinkParser.parse(text).server ?: error("ссылка $name не разобралась")
    }

    private fun json(text: String): JsonObject = Json.parseToJsonElement(text).jsonObject

    private fun port(xrayJson: String): Int {
        val outbound: JsonElement = json(xrayJson)["outbounds"]!!.jsonArray.single()
        return outbound.jsonObject["settings"]!!.jsonObject["vnext"]!!.jsonArray[0].jsonObject["port"]!!.jsonPrimitive.int
    }
}

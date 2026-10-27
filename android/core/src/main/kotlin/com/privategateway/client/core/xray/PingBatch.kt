package com.privategateway.client.core.xray

import com.privategateway.client.core.links.JsonText
import com.privategateway.client.core.links.JsonTree
import com.privategateway.client.core.servers.Latency
import com.privategateway.client.core.servers.LatencyFailure
import com.privategateway.client.core.servers.ServerDefinition

/** Сервер для замера: ключ (id в базе) и описание из запечатанной ссылки. */
data class PingTarget(val id: String, val server: ServerDefinition)

/**
 * Порция замера — один вызов `LibXray.invoke`: серверы [ids] и готовый запрос [request]. В запросе — выходы серверов с
 * ключами, поэтому в текст порции (журнал, отладка) он не попадает.
 */
class PingChunk(val ids: List<String>, val request: String) {
    /** Замер каждого сервера порции по ответу libXray на [request]. */
    fun results(response: String?): Map<String, Latency> = ids.zip(PingBatch.parse(response, ids.size)).toMap()

    /** Текст ошибок libXray по серверам порции, у которых он есть, — только для журнала через маскировку. */
    fun errors(response: String?): Map<String, String> =
        ids.zip(PingBatch.errors(response, ids.size)).mapNotNull { (id, error) -> error?.let { id to it } }.toMap()

    override fun toString(): String = "PingChunk($ids)"
}

/** План замера: порции для libXray по порядку списка и серверы, за которые ядро не возьмется, — с готовым [LatencyFailure.Unsupported]. */
class PingPlan(val chunks: List<PingChunk>, val unsupported: Map<String, Latency>)

/**
 * Задержка серверов через libXray (`pingBatch`) — то, что у десктопа делает `LatencyProbe` отдельным процессом Xray: через
 * настоящее ядро, а не пингом адреса. На каждый сервер — конфиг из одного выхода, того же, что собирает [XrayConfig] для
 * туннеля; libXray поднимает их в своем экземпляре ядра и через каждый шлет запрос HEAD к проверочному адресу (десктоп —
 * GET); задержка — до ответа. За вызов — не больше [MAX_CONFIGS] серверов, все одновременно. Сервер, за который ядро не
 * возьмется, в libXray не отправляется. Текст ошибок libXray в результат не попадает: в нем бывают адреса серверов.
 */
object PingBatch {
    /** Больше конфигов за вызов libXray не примет (`maxPingBatchConfigs`). */
    const val MAX_CONFIGS = 5

    /** Проверочный адрес — как у десктопа (`LatencyProbe.DefaultTarget`): пустой ответ 204, без cookies и без Google. */
    const val TARGET = "https://cp.cloudflare.com/generate_204"

    /** Срок ответа одного сервера, секунды — как у десктопа (`Gateway.LatencyTimeout`). */
    const val TIMEOUT_SECONDS = 5

    /** Задержка-признак ошибки в ответе libXray (`nodep.PingDelayError`). */
    const val DELAY_ERROR = 10_000L

    /** Задержка-признак тайм-аута (`nodep.PingDelayTimeout`). */
    const val DELAY_TIMEOUT = 11_000L

    /** Конфиг для замера — только выход сервера, `{"outbounds":[…]}` с меткой [XrayConfig.PROXY_TAG], или отказ ядра. */
    fun outbounds(server: ServerDefinition, settings: CoreSettings): XrayConfigResult {
        val outbound = XrayConfig.outbound(server, settings)
        return XrayConfigResult(outbound.json?.let { JsonText.value(linkedMapOf("outbounds" to listOf(it))) }, outbound.rejection)
    }

    /**
     * Порции по [MAX_CONFIGS] в порядке [targets]; отказанные ядром — сразу в [PingPlan.unsupported], как у десктопа.
     *
     * @param targets серверы с неповторяющимися id.
     * @param settings настройки подключения: от них зависит выход (отпечаток TLS, mux).
     */
    fun plan(targets: List<PingTarget>, settings: CoreSettings, url: String = TARGET, timeoutSeconds: Int = TIMEOUT_SECONDS): PingPlan {
        val unsupported = linkedMapOf<String, Latency>()
        val configs = mutableListOf<Pair<String, String>>()
        for (target in targets) {
            val config = outbounds(target.server, settings)
            if (config.json == null) {
                unsupported[target.id] = Latency.failed(LatencyFailure.Unsupported)
            } else {
                configs.add(target.id to config.json)
            }
        }

        val chunks = configs.chunked(MAX_CONFIGS).map { chunk ->
            PingChunk(chunk.map { it.first }, request(chunk.map { it.second }, url, timeoutSeconds))
        }
        return PingPlan(chunks, unsupported)
    }

    /** Запрос `pingBatch` (версия API 3): у каждого конфига — выход [XrayConfig.PROXY_TAG]; срок — в целых секундах. */
    fun request(configs: List<String>, url: String = TARGET, timeoutSeconds: Int = TIMEOUT_SECONDS): String {
        require(configs.size in 1..MAX_CONFIGS) { "За вызов — от 1 до $MAX_CONFIGS конфигов, а не ${configs.size}" }
        require(timeoutSeconds > 0) { "Срок замера — больше нуля" }
        return XrayConfig.invoke(
            "pingBatch",
            linkedMapOf(
                "configs" to configs.map { linkedMapOf("xrayJson" to it, "outboundTag" to XrayConfig.PROXY_TAG) },
                "timeout" to timeoutSeconds,
                "url" to url,
            ),
        )
    }

    /**
     * Ответ libXray на запрос из [count] конфигов — замер каждого в том же порядке. Отказ всего вызова (в том числе «ядро
     * уже запущено в процессе»), не JSON или не столько результатов — [LatencyFailure.CoreFailed] у всех; у сервера без
     * ответа или с задержкой-признаком ошибки или тайм-аута — [LatencyFailure.NoResponse].
     */
    fun parse(response: String?, count: Int): List<Latency> {
        val failed = List(count) { Latency.failed(LatencyFailure.CoreFailed) }
        val root = try {
            JsonTree.parse(response ?: "").value as? Map<*, *>
        } catch (_: JsonTree.SyntaxError) {
            null
        }

        if (root?.get("success") != true) {
            return failed
        }

        val results = (root["data"] as? Map<*, *>)?.get("results") as? List<*>
        if (results?.size != count) {
            return failed
        }

        return results.map { item(it as? Map<*, *>) }
    }

    /**
     * Текст ошибки каждого сервера из ответа на [count] конфигов (в том же порядке), пустой — null. В нем бывают адреса
     * серверов, поэтому он не входит в [parse] и в журнал идет только через маскировку. Отказ всего вызова здесь не
     * повторяется — его текст в самом ответе (`XrayResponse`); тогда у всех null.
     */
    fun errors(response: String?, count: Int): List<String?> {
        val none = List<String?>(count) { null }
        val root = try {
            JsonTree.parse(response ?: "").value as? Map<*, *>
        } catch (_: JsonTree.SyntaxError) {
            null
        }

        val results = (root?.get("data") as? Map<*, *>)?.get("results") as? List<*>
        if (root?.get("success") != true || results?.size != count) {
            return none
        }

        return results.map { ((it as? Map<*, *>)?.get("error") as? String)?.takeIf { text -> text.isNotBlank() } }
    }

    private fun item(result: Map<*, *>?): Latency {
        val delay = (result?.get("delay") as? JsonTree.JsonNumber)?.text?.toLongOrNull()
        return when {
            result?.get("success") != true || delay == null -> Latency.failed(LatencyFailure.NoResponse)
            // Признак вместо задержки — не ответ, даже если флаг успеха говорит иное.
            delay == DELAY_ERROR || delay == DELAY_TIMEOUT -> Latency.failed(LatencyFailure.NoResponse)
            else -> Latency.of(delay.coerceIn(1, Int.MAX_VALUE.toLong()).toInt())
        }
    }
}

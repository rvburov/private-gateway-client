package com.privategateway.client.vpn

import com.privategateway.client.core.servers.LatencyReason
import com.privategateway.client.core.xray.PingBatch
import org.json.JSONArray
import org.json.JSONObject

/** Через Binder выходят только числа и вид сбоя: произвольный native-текст с секретами профиля остается в процессе. */
internal object NativeProbeResponse {
    fun safe(raw: String?): String? = runCatching {
        if (raw == null || raw.length > LatencyProbeService.MAX_REQUEST) return null
        val source = JSONObject(raw)
        val success = source.optBoolean("success", false)
        val result = JSONObject().put("success", success)
        if (!success) return result.put("error", reason(source.optString("error"))).toString()
        val rows = source.optJSONObject("data")?.optJSONArray("results") ?: return null
        if (rows.length() > PingBatch.MAX_CONFIGS) return null
        val results = JSONArray()
        repeat(rows.length()) { index ->
            val row = rows.optJSONObject(index) ?: return null
            val value = JSONObject().put("success", row.optBoolean("success", false))
            (row.opt("delay") as? Number)?.let { value.put("delay", it.toLong()) }
            if (row.has("error") && !row.isNull("error")) value.put("error", reason(row.optString("error")))
            results.put(value)
        }
        result.put("data", JSONObject().put("results", results)).toString()
    }.getOrNull()

    private fun reason(raw: String?): String = when (LatencyReason.of(raw)) {
        LatencyReason.Timeout -> "timeout"
        LatencyReason.Dns -> "dns"
        LatencyReason.Tls -> "tls"
        LatencyReason.Connection -> "connection refused"
        null -> "probe failed"
    }
}

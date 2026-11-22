package com.privategateway.client.core.servers

import com.privategateway.client.core.links.JsonText
import com.privategateway.client.core.links.JsonTree
import java.util.Base64

/** Ограниченные параметры Xray из подписки: без готового конфига, файлов и управления сокетами. */
object TransportParameterRules {
    const val MAX_EXTRA_BYTES = 32768

    /** Заголовки extra — учетные данные, включая отдельную загрузку XHTTP. Только проверенный ограниченный объект. */
    fun sensitiveExtraValues(server: ServerDefinition): List<String> {
        val fields = extraObject(server.transport.extraJson) ?: return emptyList()
        val values = linkedSetOf<String>()
        fun visit(node: Any?) {
            val fields = node as? Map<*, *> ?: return
            (fields["headers"] as? Map<*, *>)?.values?.filterIsInstance<String>()?.filter { it.isNotEmpty() }?.let(values::addAll)
            fields.filterKeys { it != "headers" }.values.forEach(::visit)
        }
        visit(fields)
        return values.toList()
    }

    private val xhttpFields = setOf(
        "headers", "xPaddingBytes", "xPaddingObfsMode", "xPaddingKey", "xPaddingHeader", "xPaddingPlacement", "xPaddingMethod",
        "uplinkHTTPMethod", "sessionIDPlacement", "sessionIDKey", "sessionIDTable", "sessionIDLength", "seqPlacement", "seqKey",
        "uplinkDataPlacement", "uplinkDataKey", "uplinkChunkSize", "noGRPCHeader", "noSSEHeader", "scMaxEachPostBytes",
        "scMinPostsIntervalMs", "scMaxBufferedPosts", "scStreamUpServerSecs", "serverMaxHeaderBytes", "xmux", "downloadSettings",
    )
    private val streamFields = setOf("address", "port", "network", "security", "tlsSettings", "realitySettings", "xhttpSettings", "splithttpSettings")
    private val tlsFields = setOf("serverName", "fingerprint", "alpn", "pinnedPeerCertSha256", "verifyPeerCertByName", "minVersion", "maxVersion")
    private val realityFields = setOf("serverName", "fingerprint", "publicKey", "password", "shortId", "spiderX", "mldsa65Verify")
    private val xmuxFields = setOf("maxConcurrency", "maxConnections", "cMaxReuseTimes", "hMaxRequestTimes", "hMaxReusableSecs", "hKeepAlivePeriod")

    /** Шифрование VLESS закрепленного Xray 26.10.21; неизвестное значение не заменяется none. */
    fun validEncryption(text: String?): Boolean {
        if (text == null || text == "none") return true
        if (text.length > 4096) return false
        val parts = text.split('.')
        if (parts.size !in 4..12 || parts[0] != "mlkem768x25519plus" || parts[1] !in setOf("native", "xorpub", "random") || parts[2] !in setOf("1rtt", "0rtt")) return false
        var key = false
        for (part in parts.drop(3)) {
            if (part.length < 20) {
                if (key || part.isEmpty() || part.any { it !in '0'..'9' && it != '-' }) return false
                continue
            }
            if (part.any { it !in 'a'..'z' && it !in 'A'..'Z' && it !in '0'..'9' && it != '-' && it != '_' }) return false
            val bytes = try { Base64.getUrlDecoder().decode(part) } catch (_: IllegalArgumentException) { return false }
            if (bytes.size != 32 && bytes.size != 1184) return false
            key = true
        }
        return key
    }

    /** null — параметр отсутствует или отклонен. Указанный неверный extra обязан завершить импорт ошибкой. */
    fun normalizeExtra(text: String?): String? = extraObject(text)?.let(JsonText::value)

    internal fun configExtra(node: Any?): String? {
        if (node is String) return node
        fun coerce(value: Any?, key: String? = null): Any? = when {
            key == "headers" -> value
            value is Map<*, *> -> value.entries.associate { it.key.toString() to coerce(it.value, it.key.toString()) }
            value is String && key in setOf("noGRPCHeader", "noSSEHeader", "xPaddingObfsMode") -> when (value.lowercase()) { "true" -> true; "false" -> false; else -> value }
            value is String && key in setOf("port", "scMaxBufferedPosts", "serverMaxHeaderBytes", "hKeepAlivePeriod") -> value.toLongOrNull()?.let { JsonTree.JsonNumber(it.toString()) } ?: value
            else -> value
        }
        return encodeExtra(coerce(node))
    }

    internal fun encodeExtra(node: Any?): String? = if (validObject(node, xhttpFields, 0)) JsonText.value(canonical(node)) else null

    fun extraObject(text: String?): Map<String, Any?>? {
        if (text == null || text.toByteArray(Charsets.UTF_8).size > MAX_EXTRA_BYTES) return null
        val root = try { JsonTree.parse(text).value } catch (_: JsonTree.SyntaxError) { return null }
        if (!validObject(root, xhttpFields, 0)) return null
        @Suppress("UNCHECKED_CAST")
        return canonical(root) as Map<String, Any?>
    }

    /** UDP к серверу нельзя проверить через вход SOCKS, разрешающий только TCP. REALITY всегда выбирает HTTP/2. */
    fun requiresUdpProbe(server: ServerDefinition): Boolean {
        if (server.protocol in setOf(ProxyProtocol.Hysteria2, ProxyProtocol.Tuic, ProxyProtocol.WireGuard) || server.transport.kind == TransportKind.Quic) return true
        if (server.transport.kind != TransportKind.Xhttp) return false
        if (server.security.kind == TransportSecurity.Tls && server.security.alpn == listOf("h3")) return true
        fun downloadUsesUdp(fields: Map<*, *>): Boolean {
            val download = fields["downloadSettings"] as? Map<*, *> ?: return false
            if (download["security"] == "tls" && (download["tlsSettings"] as? Map<*, *>)?.get("alpn") == listOf("h3")) return true
            return listOf("xhttpSettings", "splithttpSettings").any { key ->
                val nested = download[key] as? Map<*, *> ?: return@any false
                downloadUsesUdp(nested["extra"] as? Map<*, *> ?: nested)
            }
        }
        return extraObject(server.transport.extraJson)?.let(::downloadUsesUdp) == true
    }

    /** Ядру нужен полный адрес отдельной загрузки XHTTP; пустые поля наследуют основной узел. */
    fun extraForServer(server: ServerDefinition): Map<String, Any?>? = extraObject(server.transport.extraJson)?.let { prepare(it, server.host, server.port) }

    private fun prepare(fields: Map<String, Any?>, host: String, port: Int): Map<String, Any?> {
        val download = fields["downloadSettings"] as? Map<*, *> ?: return fields
        val address = (download["address"] as? String)?.takeIf { it.isNotEmpty() } ?: host
        val targetPort = (download["port"] as? Number)?.toInt() ?: port
        val prepared = download.entries.associate { it.key.toString() to it.value }.toMutableMap()
        prepared["address"] = address
        prepared["port"] = targetPort
        for (key in listOf("xhttpSettings", "splithttpSettings")) {
            val nested = prepared[key] as? Map<*, *> ?: continue
            val child = nested.entries.associate { it.key.toString() to it.value }.toMutableMap()
            val extra = child["extra"] as? Map<*, *>
            if (extra != null) child["extra"] = prepare(extra.entries.associate { it.key.toString() to it.value }, address, targetPort)
            else prepared[key] = prepare(child, address, targetPort)
            if (extra != null) prepared[key] = child
        }
        return fields.toMutableMap().also { it["downloadSettings"] = prepared }
    }

    private fun canonical(value: Any?): Any? = when (value) {
        is Map<*, *> -> value.entries.sortedBy { it.key.toString() }.associate { it.key.toString() to canonical(it.value) }
        is List<*> -> value.map(::canonical)
        is JsonTree.JsonNumber -> value.text.toLong()
        else -> value
    }

    private fun validObject(node: Any?, fields: Set<String>, depth: Int): Boolean {
        if (depth > 8 || node !is Map<*, *>) return false
        return node.all { (rawName, value) ->
            val name = rawName as? String ?: return@all false
            if (name !in fields) return@all false
            when (name) {
                "downloadSettings" -> validObject(value, streamFields, depth + 1)
                "xhttpSettings", "splithttpSettings" -> value is Map<*, *> && value.all { (key, item) ->
                    if (key == "extra") validObject(item, xhttpFields, depth + 2)
                    else validObject(mapOf(key to item), xhttpFields + setOf("path", "host", "mode"), depth + 1)
                }
                "tlsSettings" -> validObject(value, tlsFields, depth + 1)
                "realitySettings" -> validObject(value, realityFields, depth + 1)
                "xmux" -> validObject(value, xmuxFields, depth + 1)
                "headers" -> value is Map<*, *> && value.all { (key, item) -> key is String && !key.equals("host", ignoreCase = true) && item is String }
                "alpn" -> value is List<*> && value.all { it is String }
                "network" -> value == "xhttp" || value == "splithttp"
                "security" -> value == "none" || value == "tls" || value == "reality"
                "address" -> value is String && value.isNotEmpty() && value.length <= 253 && value.none { it.isWhitespace() || it.isISOControl() || it in "/\\@#?" }
                "port" -> (value as? JsonTree.JsonNumber)?.text?.toIntOrNull()?.let { it in 1..65535 } == true
                else -> value is String || value is Boolean || value is JsonTree.JsonNumber && value.text.toLongOrNull() != null
            }
        }
    }
}

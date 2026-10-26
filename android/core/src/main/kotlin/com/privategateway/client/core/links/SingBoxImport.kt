package com.privategateway.client.core.links

import com.privategateway.client.core.links.ConfigTree.flag
import com.privategateway.client.core.links.ConfigTree.header
import com.privategateway.client.core.links.ConfigTree.joined
import com.privategateway.client.core.links.ConfigTree.obj
import com.privategateway.client.core.links.ConfigTree.put
import com.privategateway.client.core.links.ConfigTree.server
import com.privategateway.client.core.links.ConfigTree.text
import com.privategateway.client.core.servers.ProxyProtocol
import java.util.Locale

/**
 * Конфиг или подписка sing-box — как на десктопе (`SingBoxImport`): JSON с массивами outbounds и endpoints (WireGuard с
 * версии 1.11) или просто массив выходов. Служебные выходы (direct, block, dns, selector, urltest) пропускаются; номер
 * строки — строка, где начинается выход.
 */
internal object SingBoxImport {
    private val lists = listOf("outbounds", "endpoints")

    fun parse(text: String): ImportBatch {
        val parsed = try {
            JsonTree.parse(text)
        } catch (e: JsonTree.SyntaxError) {
            return ConfigTree.broken(ImportParser.FORMAT_SINGBOX, e.line)
        }

        val root = parsed.value
        val lines = ArrayList<ImportLine>()
        val sources = if (root is List<*>) listOf(root) else lists.map { (root as? Map<*, *>)?.get(it) }
        for (items in sources) {
            if (items !is List<*>) {
                continue
            }

            val lineOf = parsed.itemLines[items] ?: emptyList()
            for ((i, item) in items.withIndex()) {
                outbound(item)?.let { lines.add(ImportLine(lineOf.getOrElse(i) { 1 }, it)) }
            }
        }

        return ImportBatch(ImportParser.FORMAT_SINGBOX, lines.sortedBy { it.number })
    }

    /** Сервер из выхода; null — служебный выход, не сервер. */
    private fun outbound(node: Any?): LinkResult? {
        val type = text(node, "type")?.lowercase(Locale.ROOT)
        val name = text(node, "tag")
        val host = text(node, "server")
        val port = text(node, "server_port")
        val query = query()
        tls(obj(node, "tls"), query)
        transport(obj(node, "transport"), query)
        return when (type) {
            "direct", "block", "dns", "selector", "urltest" -> null
            "vless" -> {
                put(query, "flow", text(node, "flow"))
                server(ProxyProtocol.Vless, listOf(text(node, "uuid")), host, port, query, name)
            }

            "vmess" -> LinkParser.vmessFrom(name, host, port, text(node, "alter_id"), text(node, "security"), null, query, text(node, "uuid"))
            "trojan" -> server(ProxyProtocol.Trojan, listOf(text(node, "password")), host, port, query, name)
            "shadowsocks" -> {
                put(query, "plugin", text(node, "plugin"))
                server(ProxyProtocol.Shadowsocks, listOf(text(node, "method"), text(node, "password")), host, port, query, name)
            }

            "hysteria2" -> {
                // У Hysteria2 и TUIC TLS всегда: поля tls действуют и без enabled.
                query["security"] = "tls"
                val obfs = obj(node, "obfs")
                put(query, "obfs", text(obfs, "type"))
                put(query, "obfs-password", text(obfs, "password"))
                server(ProxyProtocol.Hysteria2, listOf(text(node, "password")), host, port, query, name)
            }

            "tuic" -> {
                query["security"] = "tls"
                put(query, "congestion_control", text(node, "congestion_control"))
                server(ProxyProtocol.Tuic, listOf(text(node, "uuid"), text(node, "password")), host, port, query, name)
            }

            "wireguard" -> wireGuard(node, name)
            null -> ConfigTree.fail(ImportErrors.BAD_LINK)
            else -> ConfigTree.fail(ImportErrors.UNKNOWN_SCHEME)
        }
    }

    /** WireGuard: выход до 1.11 (server, peer_public_key) или endpoint с одним peers. */
    private fun wireGuard(node: Any?, name: String?): LinkResult {
        val peers = (node as? Map<*, *>)?.get("peers") as? List<*>
        if (peers != null && peers.size > 1) {
            return ConfigTree.fail(ImportErrors.UNSUPPORTED_PARAMETER)
        }

        val peer = if (peers != null && peers.size == 1) peers[0] else null
        val query = query()
        put(query, "publickey", if (peer == null) text(node, "peer_public_key") else text(peer, "public_key"))
        put(query, "presharedkey", if (peer == null) text(node, "pre_shared_key") else text(peer, "pre_shared_key"))
        put(query, "address", joined(node, "address") ?: joined(node, "local_address"))
        put(query, "mtu", text(node, "mtu"))
        return if (peer == null) {
            server(ProxyProtocol.WireGuard, listOf(text(node, "private_key")), text(node, "server"), text(node, "server_port"), query, name)
        } else {
            server(ProxyProtocol.WireGuard, listOf(text(node, "private_key")), text(peer, "address"), text(peer, "port"), query, name)
        }
    }

    /** tls → параметры ссылки: без tls или enabled=false — явно без защиты. */
    private fun tls(tls: Map<*, *>?, query: Query) {
        val reality = obj(tls, "reality")
        query["security"] = if (flag(reality, "enabled")) "reality" else if (flag(tls, "enabled")) "tls" else "none"
        put(query, "sni", text(tls, "server_name"))
        put(query, "alpn", joined(tls, "alpn"))
        put(query, "allowInsecure", if (flag(tls, "insecure")) "1" else null)
        val utls = obj(tls, "utls")
        put(query, "fp", if (flag(utls, "enabled")) text(utls, "fingerprint") ?: "chrome" else null)
        put(query, "pbk", text(reality, "public_key"))
        put(query, "sid", text(reality, "short_id"))
    }

    /** transport → параметры ссылки; «http» и «quic» разбор ссылок не принимает — так и задумано. */
    private fun transport(transport: Map<*, *>?, query: Query) {
        if (transport == null) {
            return
        }

        put(query, "type", text(transport, "type"))
        put(query, "path", text(transport, "path"))
        put(query, "host", header(obj(transport, "headers"), "Host") ?: text(transport, "host"))
        put(query, "serviceName", text(transport, "service_name"))
    }
}

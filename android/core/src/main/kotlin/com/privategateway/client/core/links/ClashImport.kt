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
 * Конфиг или подписка Clash/Mihomo — как на десктопе (`ClashImport`): YAML со списком proxies. Группы, правила и
 * провайдеры не разбираются — только серверы. Служебные direct и dns пропускаются; номер строки — строка, где начинается
 * сервер.
 */
internal object ClashImport {
    fun parse(text: String): ImportBatch {
        val parsed = YamlTree.parse(text)
        if (parsed.errorLine > 0) {
            return ConfigTree.broken(ImportParser.FORMAT_CLASH, parsed.errorLine)
        }

        val proxies = (parsed.root as? Map<*, *>)?.get("proxies") as? List<*> ?: return ImportBatch(ImportParser.FORMAT_CLASH, emptyList())
        val listLine = parsed.lines[proxies] ?: 1
        val lines = ArrayList<ImportLine>()
        for (item in proxies) {
            proxy(item)?.let { lines.add(ImportLine(if (item == null) listLine else parsed.lines[item] ?: listLine, it)) }
        }

        return ImportBatch(ImportParser.FORMAT_CLASH, lines)
    }

    /** Сервер из элемента proxies; null — служебный, не сервер. */
    private fun proxy(node: Any?): LinkResult? {
        val type = text(node, "type")?.lowercase(Locale.ROOT)
        val name = text(node, "name")
        val host = text(node, "server")
        val port = text(node, "port")
        val query = query()
        put(query, "sni", text(node, "sni") ?: text(node, "servername"))
        put(query, "alpn", joined(node, "alpn"))
        put(query, "allowInsecure", if (flag(node, "skip-cert-verify")) "1" else null)
        put(query, "fp", text(node, "client-fingerprint"))
        // fingerprint у Mihomo — SHA-256 сертификата сервера, тот же смысл, что «pcs» у Xray (решение 480).
        put(query, "pcs", text(node, "fingerprint"))
        val reality = obj(node, "reality-opts")
        put(query, "pbk", text(reality, "public-key"))
        put(query, "sid", text(reality, "short-id"))
        transport(node, query)
        return when (type) {
            "direct", "dns" -> null
            "vless" -> {
                query["security"] = security(node, reality, "none")
                put(query, "flow", text(node, "flow"))
                put(query, "encryption", text(node, "encryption"))
                server(ProxyProtocol.Vless, listOf(text(node, "uuid")), host, port, query, name)
            }

            "vmess" -> {
                query["security"] = security(node, reality, "none")
                LinkParser.vmessFrom(name, host, port, text(node, "alterId"), text(node, "cipher"), null, query, text(node, "uuid"))
            }

            "trojan" -> {
                // Trojan в Clash — всегда TLS; trojan-go с шифрованием Shadowsocks ядро не умеет.
                query["security"] = security(node, reality, "tls")
                if (flag(obj(node, "ss-opts"), "enabled")) {
                    ConfigTree.fail(ImportErrors.UNSUPPORTED_PARAMETER)
                } else {
                    server(ProxyProtocol.Trojan, listOf(text(node, "password")), host, port, query, name)
                }
            }

            "ss" -> {
                put(query, "plugin", text(node, "plugin"))
                server(ProxyProtocol.Shadowsocks, listOf(text(node, "cipher"), text(node, "password")), host, port, query, name)
            }

            "hysteria2" -> {
                query["security"] = "tls"
                put(query, "obfs", text(node, "obfs"))
                put(query, "obfs-password", text(node, "obfs-password"))
                server(ProxyProtocol.Hysteria2, listOf(text(node, "password")), host, port, query, name)
            }

            "tuic" -> {
                query["security"] = "tls"
                put(query, "congestion_control", text(node, "congestion-controller"))
                server(ProxyProtocol.Tuic, listOf(text(node, "uuid"), text(node, "password")), host, port, query, name)
            }

            "wireguard" -> wireGuard(node, name)
            null -> ConfigTree.fail(ImportErrors.BAD_LINK)
            else -> ConfigTree.fail(ImportErrors.UNKNOWN_SCHEME)
        }
    }

    /** tls: true — TLS, с reality-opts — REALITY; без tls — защита протокола по умолчанию. */
    private fun security(node: Any?, reality: Map<*, *>?, byDefault: String): String = when {
        reality != null -> "reality"
        text(node, "tls") == null -> byDefault
        flag(node, "tls") -> "tls"
        else -> "none"
    }

    /** network и *-opts → параметры ссылки; ws с v2ray-http-upgrade — это HTTPUpgrade. */
    private fun transport(node: Any?, query: Query) {
        val network = text(node, "network")?.lowercase(Locale.ROOT)
        val ws = obj(node, "ws-opts")
        val xhttp = obj(node, "xhttp-opts")
        put(query, "type", if (network == "ws" && flag(ws, "v2ray-http-upgrade")) "httpupgrade" else network)
        put(query, "path", text(ws, "path") ?: text(xhttp, "path"))
        put(query, "host", header(obj(ws, "headers"), "Host") ?: text(xhttp, "host"))
        put(query, "serviceName", text(obj(node, "grpc-opts"), "grpc-service-name"))
        put(query, "mode", text(xhttp, "mode"))
    }

    /** WireGuard: поля сервера или один peers; AmneziaWG ядро не поддерживает. */
    private fun wireGuard(node: Any?, name: String?): LinkResult {
        val peers = (node as? Map<*, *>)?.get("peers") as? List<*>
        if (peers != null && peers.size > 1 || obj(node, "amnezia-wg-option") != null) {
            return ConfigTree.fail(ImportErrors.UNSUPPORTED_PARAMETER)
        }

        val peer = if (peers != null && peers.size == 1) peers[0] else node
        val query = query()
        put(query, "publickey", text(peer, "public-key"))
        put(query, "presharedkey", text(peer, "pre-shared-key"))
        put(query, "address", listOfNotNull(text(node, "ip"), text(node, "ipv6")).joinToString(","))
        put(query, "mtu", text(node, "mtu"))
        return server(ProxyProtocol.WireGuard, listOf(text(node, "private-key")), text(peer, "server"), text(peer, "port"), query, name)
    }
}

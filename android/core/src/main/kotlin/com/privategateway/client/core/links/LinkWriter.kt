package com.privategateway.client.core.links

import com.privategateway.client.core.servers.ProxyProtocol
import com.privategateway.client.core.servers.ServerDefinition
import com.privategateway.client.core.servers.TransportKind
import com.privategateway.client.core.servers.TransportSecurity
import java.util.Base64
import java.util.Locale

/**
 * Ссылка из описания сервера — для «Поделиться» (QR-код и текст), как на десктопе (`LinkWriter`). Обратная к
 * [LinkParser]: разбор записанной ссылки дает тот же сервер (тест — на всех эталонах shared/testdata). В ссылке — секрет
 * сервера: показывается только по явному действию пользователя.
 */
object LinkWriter {
    fun write(server: ServerDefinition): String {
        val credentials = server.credentials
        val address = (if (server.host.contains(':')) "[${server.host}]" else server.host) + ":" + server.port
        val name = "#" + escape(nameWithFlag(server))
        return when (server.protocol) {
            ProxyProtocol.Vless -> {
                val vless = mutableListOf<Pair<String, String?>>("encryption" to "none", "flow" to server.options.flow)
                vless.addAll(stream(server))
                "vless://${escape(credentials.uuid)}@$address${query(vless)}$name"
            }

            ProxyProtocol.Vmess -> "vmess://" + Base64.getEncoder().encodeToString(vmess(server).toByteArray(Charsets.UTF_8))
            ProxyProtocol.Trojan -> "trojan://${escape(credentials.password)}@$address${query(stream(server))}$name"
            ProxyProtocol.Shadowsocks -> {
                val method = server.options.cipher ?: ""

                // SIP002: обычные шифры — base64url(метод:пароль), Shadowsocks 2022 — открытым текстом.
                val userInfo = if (method.startsWith("2022-")) {
                    escape(method) + ":" + escape(credentials.password)
                } else {
                    Base64.getUrlEncoder().withoutPadding().encodeToString("$method:${credentials.password}".toByteArray(Charsets.UTF_8))
                }

                "ss://$userInfo@$address$name"
            }

            ProxyProtocol.Hysteria2 -> {
                val hysteria = tls(server) + listOf("obfs" to server.options.obfs, "obfs-password" to credentials.obfsPassword)
                "hysteria2://${escape(credentials.password)}@$address${query(hysteria)}$name"
            }

            ProxyProtocol.Tuic -> {
                val tuic = tls(server) + listOf("congestion_control" to server.options.congestionControl)
                "tuic://${escape(credentials.uuid)}:${escape(credentials.password)}@$address${query(tuic)}$name"
            }

            ProxyProtocol.WireGuard -> {
                val wireguard = listOf(
                    "publickey" to server.options.peerPublicKey,
                    "presharedkey" to credentials.presharedKey,
                    "address" to server.options.addresses?.takeIf { it.isNotEmpty() }?.joinToString(","),
                    "mtu" to server.options.mtu?.toString(),
                )
                "wireguard://${escape(credentials.privateKey)}@$address${query(wireguard)}$name"
            }
        }
    }

    /** Название с флагом страны впереди — так его снова разберет [CountryFlag]. */
    private fun nameWithFlag(server: ServerDefinition): String {
        val code = server.countryCode
        if (code.length != 2 || !code.all { it in 'A'..'Z' }) {
            return server.name
        }

        val flag = String(Character.toChars(0x1F1E6 + (code[0] - 'A'))) + String(Character.toChars(0x1F1E6 + (code[1] - 'A')))
        return if (server.name.isEmpty()) flag else "$flag ${server.name}"
    }

    private fun kind(kind: TransportKind) = when (kind) {
        TransportKind.WebSocket -> "ws"
        TransportKind.Grpc -> "grpc"
        TransportKind.HttpUpgrade -> "httpupgrade"
        TransportKind.Xhttp -> "xhttp"
        else -> "tcp"
    }

    /** Транспорт и защита — параметры ссылки VLESS и Trojan. */
    private fun stream(server: ServerDefinition): List<Pair<String, String?>> {
        val transport = server.transport
        return listOf(
            "type" to kind(transport.kind),
            "path" to transport.path,
            "host" to transport.host,
            "serviceName" to transport.serviceName,
            "mode" to transport.mode,
            "security" to server.security.kind.name.lowercase(Locale.ROOT),
        ) + tls(server)
    }

    /** Параметры TLS и REALITY; у Hysteria2 и TUIC защита — всегда TLS, ее имя не пишется. */
    private fun tls(server: ServerDefinition): List<Pair<String, String?>> {
        val security = server.security
        return listOf(
            "sni" to security.sni,
            "fp" to security.fingerprint,
            "alpn" to security.alpn?.takeIf { it.isNotEmpty() }?.joinToString(","),
            "allowInsecure" to if (security.allowInsecure) "1" else null,
            "pcs" to security.pinnedPeerCertSha256?.takeIf { it.isNotEmpty() }?.joinToString(","),
            "vcn" to security.verifyPeerCertByName?.takeIf { it.isNotEmpty() }?.joinToString(","),
            "pbk" to security.realityPublicKey,
            "sid" to security.realityShortId,
            "spx" to security.realitySpiderX,
        )
    }

    /** vmess:// — base64 JSON в виде v2rayN: поля, которые читает [LinkParser]. */
    private fun vmess(server: ServerDefinition): String {
        val transport = server.transport
        val security = server.security
        val fields = mutableListOf<Pair<String, Any?>>(
            "v" to "2",
            "ps" to nameWithFlag(server),
            "add" to server.host,
            "port" to server.port.toString(),
            "id" to (server.credentials.uuid ?: ""),
            "aid" to "0",
            "scy" to (server.options.cipher ?: "auto"),
            "net" to kind(transport.kind),
            "type" to "none",
            "host" to (transport.host ?: ""),
            // В vmess-ссылке имя сервиса gRPC лежит в path.
            "path" to ((if (transport.kind == TransportKind.Grpc) transport.serviceName else transport.path) ?: ""),
            "tls" to if (security.kind == TransportSecurity.None) "" else security.kind.name.lowercase(Locale.ROOT),
            "sni" to (security.sni ?: ""),
            "fp" to (security.fingerprint ?: ""),
            "alpn" to (security.alpn?.joinToString(",") ?: ""),
        )
        // Закрепленный сертификат (решение 480) — только когда он есть: у v2rayN таких полей нет.
        security.pinnedPeerCertSha256?.takeIf { it.isNotEmpty() }?.let { fields += "pcs" to it.joinToString(",") }
        security.verifyPeerCertByName?.takeIf { it.isNotEmpty() }?.let { fields += "vcn" to it.joinToString(",") }
        return JsonText.obj(*fields.toTypedArray())
    }

    private fun query(parameters: List<Pair<String, String?>>): String {
        val pairs = parameters.filter { !it.second.isNullOrEmpty() }.map { it.first + "=" + escape(it.second) }
        return if (pairs.isEmpty()) "" else "?" + pairs.joinToString("&")
    }

    private fun escape(text: String?) = LinkParser.escape(text ?: "")
}

/** Запись JSON для конфигов и ссылок: строки экранируются, числа и логические значения — как есть. */
object JsonText {
    /** Объект из пар «ключ — значение»: значение — строка, число, логическое, список, словарь или null. */
    fun obj(vararg pairs: Pair<String, Any?>): String = value(linkedMapOf(*pairs))

    fun value(value: Any?): String = StringBuilder().also { write(it, value) }.toString()

    private fun write(out: StringBuilder, value: Any?) {
        when (value) {
            null -> out.append("null")
            is String -> string(out, value)
            is Boolean, is Int, is Long -> out.append(value.toString())
            is Map<*, *> -> {
                out.append('{')
                var first = true
                for ((key, item) in value) {
                    if (!first) {
                        out.append(',')
                    }

                    first = false
                    string(out, key.toString())
                    out.append(':')
                    write(out, item)
                }

                out.append('}')
            }

            is Iterable<*> -> {
                out.append('[')
                var first = true
                for (item in value) {
                    if (!first) {
                        out.append(',')
                    }

                    first = false
                    write(out, item)
                }

                out.append(']')
            }

            else -> string(out, value.toString())
        }
    }

    private fun string(out: StringBuilder, text: String) {
        out.append('"')
        for (c in text) {
            when {
                c == '"' -> out.append("\\\"")
                c == '\\' -> out.append("\\\\")
                c == '\n' -> out.append("\\n")
                c == '\r' -> out.append("\\r")
                c == '\t' -> out.append("\\t")
                c < ' ' -> out.append("\\u%04x".format(c.code))
                else -> out.append(c)
            }
        }

        out.append('"')
    }
}

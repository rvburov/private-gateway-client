package com.privategateway.client.core.links

import com.privategateway.client.core.servers.ProtocolOptions
import com.privategateway.client.core.servers.ProxyProtocol
import com.privategateway.client.core.servers.SecurityOptions
import com.privategateway.client.core.servers.ServerCredentials
import com.privategateway.client.core.servers.ServerDefinition
import com.privategateway.client.core.servers.TransportKind
import com.privategateway.client.core.servers.TransportOptions
import com.privategateway.client.core.servers.TransportSecurity
import java.nio.ByteBuffer
import java.nio.charset.CharacterCodingException
import java.nio.charset.CodingErrorAction
import java.util.Base64
import java.util.Locale
import java.util.TreeMap

/**
 * Коды ошибок разбора. Одинаковые на всех платформах: эталоны в shared/testdata проверяют их и на десктопе, и на
 * телефонах.
 */
object ImportErrors {
    /** Строка не похожа на ссылку: нет «схема://». */
    const val BAD_LINK = "bad_link"

    /** Протокол ссылки программа не знает. */
    const val UNKNOWN_SCHEME = "unknown_scheme"

    const val BAD_HOST = "bad_host"

    /** Порт не число, вне 1–65535 или диапазон портов (не поддерживается). */
    const val BAD_PORT = "bad_port"

    /** Нет UUID, пароля или ключа — подключиться нечем. */
    const val NO_CREDENTIALS = "no_credentials"

    const val BAD_ENCODING = "bad_encoding"

    const val UNSUPPORTED_TRANSPORT = "unsupported_transport"

    const val UNSUPPORTED_METHOD = "unsupported_method"

    /** Параметр, без которого сервер не заработает так, как задумано (plugin, alterId…). */
    const val UNSUPPORTED_PARAMETER = "unsupported_parameter"

    /** Все коды — тест следит, чтобы у каждого был эталон. */
    val all = listOf(
        BAD_LINK, UNKNOWN_SCHEME, BAD_HOST, BAD_PORT, NO_CREDENTIALS, BAD_ENCODING, UNSUPPORTED_TRANSPORT, UNSUPPORTED_METHOD,
        UNSUPPORTED_PARAMETER,
    )
}

/** Результат разбора одной ссылки: сервер или код ошибки из [ImportErrors]. */
data class LinkResult(val server: ServerDefinition?, val error: String?) {
    val isOk: Boolean get() = server != null
}

/** Параметры ссылки: регистр ключей не важен. */
internal typealias Query = TreeMap<String, String>

internal fun query(): Query = TreeMap(String.CASE_INSENSITIVE_ORDER)

/**
 * Разбор ссылок vless://, vmess://, trojan://, ss://, hysteria2:// (hy2://), tuic://, wireguard:// (wg://) — так же,
 * как на десктопе (`LinkParser`): ссылка разбирается вручную, без java.net.URI, base64 и «+» в данных не портятся.
 * Неизвестные параметры пропускаются; параметры, без которых сервер работал бы не так, — ошибка.
 */
object LinkParser {
    internal val shadowsocksMethods = setOf(
        "aes-128-gcm", "aes-256-gcm", "chacha20-ietf-poly1305", "xchacha20-ietf-poly1305",
        "2022-blake3-aes-128-gcm", "2022-blake3-aes-256-gcm", "2022-blake3-chacha20-poly1305",
    )

    internal val vmessCiphers = setOf("auto", "aes-128-gcm", "chacha20-poly1305", "none", "zero")

    fun parse(link: String?): LinkResult {
        val text = (link ?: "").trim()
        val schemeEnd = text.indexOf("://")
        if (schemeEnd <= 0) {
            return fail(ImportErrors.BAD_LINK)
        }

        val rest = text.substring(schemeEnd + 3)
        val protocol = when (text.substring(0, schemeEnd).lowercase(Locale.ROOT)) {
            "vless" -> ProxyProtocol.Vless
            "vmess" -> ProxyProtocol.Vmess
            "trojan" -> ProxyProtocol.Trojan
            "ss" -> ProxyProtocol.Shadowsocks
            "hysteria2", "hy2" -> ProxyProtocol.Hysteria2
            "tuic" -> ProxyProtocol.Tuic
            "wireguard", "wg" -> ProxyProtocol.WireGuard
            else -> return fail(ImportErrors.UNKNOWN_SCHEME)
        }

        if (protocol == ProxyProtocol.Vmess) {
            return vmess(rest)
        }

        return split(rest)?.let { fromParts(protocol, it) } ?: fail(ImportErrors.BAD_LINK)
    }

    /**
     * Сервер из частей ссылки — общий путь для ссылок и для форматов sing-box и Clash: те же проверки и те же коды
     * ошибок. VMess — через [vmessFrom].
     */
    internal fun fromParts(protocol: ProxyProtocol, parts: LinkParts): LinkResult = when (protocol) {
        ProxyProtocol.Vless -> vless(parts)
        ProxyProtocol.Trojan -> trojan(parts)
        ProxyProtocol.Shadowsocks -> shadowsocks(parts)
        ProxyProtocol.Hysteria2 -> hysteria2(parts)
        ProxyProtocol.Tuic -> tuic(parts)
        ProxyProtocol.WireGuard -> wireGuard(parts)
        ProxyProtocol.Vmess -> fail(ImportErrors.UNKNOWN_SCHEME)
    }

    /**
     * Части ссылки из полей конфига: секреты (через «:», каждый экранируется как в ссылке), адрес и порт (IPv6 — и без
     * скобок: порт отделяется по последнему двоеточию), параметры и название — как после разбора ссылки.
     */
    internal fun parts(secrets: List<String?>, host: String?, port: String?, query: Query, name: String?): LinkParts =
        LinkParts(
            secrets.joinToString(":") { escape(it ?: "") },
            (host ?: "") + ":" + (port ?: ""),
            query,
            (name ?: "").trim(),
        )

    // ── Протоколы ────────────────────────────────────────────────────────────

    private fun vless(parts: LinkParts): LinkResult {
        val endpoint = endpoint(parts)
        val transport = transport(parts.query)
        val security = security(parts.query, TransportSecurity.None)
        firstError(endpoint.error, transport.error, security.error)?.let { return fail(it) }

        val encryption = query(parts.query, "encryption")
        if (encryption != null && encryption != "none") {
            return fail(ImportErrors.UNSUPPORTED_PARAMETER)
        }

        val uuid = unescape(parts.userInfo)
        return if (uuid.isEmpty()) {
            fail(ImportErrors.NO_CREDENTIALS)
        } else {
            ok(
                parts.name, ProxyProtocol.Vless, endpoint.host, endpoint.port, transport.value, security.value,
                ProtocolOptions.Empty.copy(flow = query(parts.query, "flow")), ServerCredentials(uuid = uuid),
            )
        }
    }

    private fun vmess(rest: String): LinkResult {
        val body = rest.split('#')[0].trim()
        val json = decodeBase64(body) ?: return fail(ImportErrors.BAD_ENCODING)
        val root = try {
            JsonTree.parse(json).value
        } catch (_: JsonTree.SyntaxError) {
            return fail(ImportErrors.BAD_ENCODING)
        }

        if (root !is Map<*, *>) {
            return fail(ImportErrors.BAD_ENCODING)
        }

        val query = query()
        query["type"] = ConfigTree.text(root, "net") ?: "tcp"
        query["path"] = ConfigTree.text(root, "path") ?: ""
        query["host"] = ConfigTree.text(root, "host") ?: ""
        query["security"] = ConfigTree.text(root, "tls") ?: ""
        query["sni"] = ConfigTree.text(root, "sni") ?: ""
        query["fp"] = ConfigTree.text(root, "fp") ?: ""
        query["alpn"] = ConfigTree.text(root, "alpn") ?: ""
        query["pcs"] = ConfigTree.text(root, "pcs") ?: ""
        query["vcn"] = ConfigTree.text(root, "vcn") ?: ""
        // В vmess-ссылке имя сервиса gRPC лежит в path.
        query["serviceName"] = query["path"]!!
        return vmessFrom(
            ConfigTree.text(root, "ps"), ConfigTree.text(root, "add"), ConfigTree.text(root, "port"), ConfigTree.text(root, "aid"),
            ConfigTree.text(root, "scy"), ConfigTree.text(root, "type"), query, ConfigTree.text(root, "id"),
        )
    }

    /** VMess из полей: alterId, шифр и тип заголовка проверяются так же, как в vmess-ссылке. */
    internal fun vmessFrom(
        name: String?,
        hostText: String?,
        portText: String?,
        alterId: String?,
        cipherText: String?,
        header: String?,
        query: Query,
        uuid: String?,
    ): LinkResult {
        val host = hostText ?: ""
        if (!isHost(host)) {
            return fail(ImportErrors.BAD_HOST)
        }

        val port = port(portText) ?: return fail(ImportErrors.BAD_PORT)

        // alterId больше нуля — старый VMess без AEAD, ядро его не поддерживает.
        if (!alterId.isNullOrEmpty() && alterId != "0") {
            return fail(ImportErrors.UNSUPPORTED_PARAMETER)
        }

        val cipher = (cipherText ?: "auto").lowercase(Locale.ROOT)
        if (cipher !in vmessCiphers) {
            return fail(ImportErrors.UNSUPPORTED_METHOD)
        }

        if (header != null && header != "none" && header != "") {
            return fail(ImportErrors.UNSUPPORTED_PARAMETER)
        }

        val transport = transport(query)
        val security = security(query, TransportSecurity.None)
        firstError(transport.error, security.error)?.let { return fail(it) }

        return if (uuid.isNullOrEmpty()) {
            fail(ImportErrors.NO_CREDENTIALS)
        } else {
            ok(
                name ?: "", ProxyProtocol.Vmess, host, port, transport.value, security.value,
                ProtocolOptions.Empty.copy(cipher = cipher), ServerCredentials(uuid = uuid),
            )
        }
    }

    private fun trojan(parts: LinkParts): LinkResult {
        // Trojan без TLS не работает — защита по умолчанию TLS.
        val endpoint = endpoint(parts)
        val transport = transport(parts.query)
        val security = security(parts.query, TransportSecurity.Tls)
        firstError(endpoint.error, transport.error, security.error)?.let { return fail(it) }

        val password = unescape(parts.userInfo)
        return if (password.isEmpty()) {
            fail(ImportErrors.NO_CREDENTIALS)
        } else {
            ok(
                parts.name, ProxyProtocol.Trojan, endpoint.host, endpoint.port, transport.value, security.value, ProtocolOptions.Empty,
                ServerCredentials(password = password),
            )
        }
    }

    private fun shadowsocks(original: LinkParts): LinkResult {
        var parts = original

        // Старый вид: ss://base64(метод:пароль@адрес:порт)#имя.
        if (parts.userInfo.isEmpty()) {
            val legacy = decodeBase64(parts.authority)
            if (legacy == null || !legacy.contains('@')) {
                return fail(ImportErrors.BAD_ENCODING)
            }

            val at = legacy.lastIndexOf('@')
            parts = parts.copy(userInfo = legacy.substring(0, at), authority = legacy.substring(at + 1))
        }

        val endpoint = endpoint(parts)
        endpoint.error?.let { return fail(it) }

        if (query(parts.query, "plugin") != null) {
            return fail(ImportErrors.UNSUPPORTED_PARAMETER)
        }

        // SIP002: base64url(метод:пароль) или метод:пароль в процентной кодировке (Shadowsocks 2022).
        var userInfo = unescape(parts.userInfo)
        if (!userInfo.contains(':')) {
            userInfo = decodeBase64(userInfo) ?: ""
        }

        val colon = userInfo.indexOf(':')
        if (colon <= 0 || colon == userInfo.length - 1) {
            return fail(ImportErrors.NO_CREDENTIALS)
        }

        val method = userInfo.substring(0, colon).lowercase(Locale.ROOT)
        return if (method !in shadowsocksMethods) {
            fail(ImportErrors.UNSUPPORTED_METHOD)
        } else {
            ok(
                parts.name, ProxyProtocol.Shadowsocks, endpoint.host, endpoint.port, TransportOptions.Tcp, SecurityOptions.None,
                ProtocolOptions.Empty.copy(cipher = method), ServerCredentials(password = userInfo.substring(colon + 1)),
            )
        }
    }

    private fun hysteria2(parts: LinkParts): LinkResult {
        val endpoint = endpoint(parts)
        val security = security(parts.query, TransportSecurity.Tls)
        firstError(endpoint.error, security.error)?.let { return fail(it) }

        val obfs = query(parts.query, "obfs")
        val obfsPassword = query(parts.query, "obfs-password")
        if (obfs != null && obfs != "salamander" || obfs != null && obfsPassword == null) {
            return fail(ImportErrors.UNSUPPORTED_PARAMETER)
        }

        val password = unescape(parts.userInfo)
        return if (password.isEmpty()) {
            fail(ImportErrors.NO_CREDENTIALS)
        } else {
            ok(
                parts.name, ProxyProtocol.Hysteria2, endpoint.host, endpoint.port, TransportOptions.Quic,
                security.value.copy(kind = TransportSecurity.Tls), ProtocolOptions.Empty.copy(obfs = obfs),
                ServerCredentials(password = password, obfsPassword = obfsPassword),
            )
        }
    }

    private fun tuic(parts: LinkParts): LinkResult {
        val endpoint = endpoint(parts)
        val security = security(parts.query, TransportSecurity.Tls)
        firstError(endpoint.error, security.error)?.let { return fail(it) }

        val userInfo = parts.userInfo
        val colon = userInfo.indexOf(':')
        val uuid = if (colon > 0) unescape(userInfo.substring(0, colon)) else ""
        val password = if (colon > 0) unescape(userInfo.substring(colon + 1)) else ""
        return if (uuid.isEmpty() || password.isEmpty()) {
            fail(ImportErrors.NO_CREDENTIALS)
        } else {
            ok(
                parts.name, ProxyProtocol.Tuic, endpoint.host, endpoint.port, TransportOptions.Quic,
                security.value.copy(kind = TransportSecurity.Tls),
                ProtocolOptions.Empty.copy(congestionControl = query(parts.query, "congestion_control")),
                ServerCredentials(uuid = uuid, password = password),
            )
        }
    }

    private fun wireGuard(parts: LinkParts): LinkResult {
        val endpoint = endpoint(parts)
        endpoint.error?.let { return fail(it) }

        val privateKey = unescape(parts.userInfo)
        val peerKey = query(parts.query, "publickey") ?: query(parts.query, "public_key") ?: query(parts.query, "peer_public_key")
        if (privateKey.isEmpty() || peerKey == null) {
            return fail(ImportErrors.NO_CREDENTIALS)
        }

        val addresses = (query(parts.query, "address") ?: query(parts.query, "ip"))
            ?.split(',')?.map { it.trim() }?.filter { it.isNotEmpty() }
        var mtu: Int? = null
        query(parts.query, "mtu")?.let { mtuText ->
            val value = digits(mtuText)
            if (value == null || value < 576 || value > 65535) {
                return fail(ImportErrors.UNSUPPORTED_PARAMETER)
            }

            mtu = value
        }

        return ok(
            parts.name, ProxyProtocol.WireGuard, endpoint.host, endpoint.port, TransportOptions.Udp, SecurityOptions.None,
            ProtocolOptions.Empty.copy(peerPublicKey = peerKey, addresses = addresses, mtu = mtu),
            ServerCredentials(
                privateKey = privateKey,
                presharedKey = query(parts.query, "presharedkey") ?: query(parts.query, "preshared_key"),
            ),
        )
    }

    // ── Общие части ссылки ───────────────────────────────────────────────────

    /** Части ссылки «данные@адрес:порт?параметры#имя»; имя и параметры уже раскодированы. */
    internal data class LinkParts(val userInfo: String, val authority: String, val query: Query, val name: String)

    private fun split(original: String): LinkParts? {
        var rest = original
        var name = ""
        val hash = rest.indexOf('#')
        if (hash >= 0) {
            name = unescape(rest.substring(hash + 1)).trim()
            rest = rest.substring(0, hash)
        }

        val query = query()
        val question = rest.indexOf('?')
        if (question >= 0) {
            for (pair in rest.substring(question + 1).split('&').filter { it.isNotEmpty() }) {
                val eq = pair.indexOf('=')
                val key = unescape(if (eq >= 0) pair.substring(0, eq) else pair)
                val value = if (eq >= 0) unescape(pair.substring(eq + 1)) else ""
                query.putIfAbsent(key, value)
            }

            rest = rest.substring(0, question)
        }

        rest = rest.trimEnd('/')
        val at = rest.lastIndexOf('@')
        return if (rest.isEmpty()) {
            null
        } else {
            LinkParts(if (at >= 0) rest.substring(0, at) else "", if (at >= 0) rest.substring(at + 1) else rest, query, name)
        }
    }

    private class Endpoint(val host: String, val port: Int, val error: String?)

    /** Адрес и порт из «адрес:порт» или «[IPv6]:порт». */
    private fun endpoint(parts: LinkParts): Endpoint {
        val authority = parts.authority
        val host: String
        val portText: String
        if (authority.startsWith('[')) {
            val close = authority.indexOf(']')
            if (close < 0 || close + 1 >= authority.length || authority[close + 1] != ':') {
                return Endpoint("", 0, ImportErrors.BAD_HOST)
            }

            host = authority.substring(1, close)
            portText = authority.substring(close + 2)
        } else {
            val colon = authority.lastIndexOf(':')
            if (colon <= 0) {
                return Endpoint("", 0, if (colon == 0) ImportErrors.BAD_HOST else ImportErrors.BAD_PORT)
            }

            host = authority.substring(0, colon)
            portText = authority.substring(colon + 1)
        }

        if (!isHost(host)) {
            return Endpoint(host, 0, ImportErrors.BAD_HOST)
        }

        val port = port(portText) ?: return Endpoint(host, 0, ImportErrors.BAD_PORT)
        return Endpoint(host, port, null)
    }

    private class Checked<T>(val value: T, val error: String?)

    private fun transport(query: Query): Checked<TransportOptions> {
        val kind = when ((query(query, "type") ?: "tcp").lowercase(Locale.ROOT)) {
            "tcp", "raw" -> TransportKind.Tcp
            "ws", "websocket" -> TransportKind.WebSocket
            "grpc" -> TransportKind.Grpc
            "httpupgrade" -> TransportKind.HttpUpgrade
            "xhttp", "splithttp" -> TransportKind.Xhttp
            else -> return Checked(TransportOptions.Tcp, ImportErrors.UNSUPPORTED_TRANSPORT)
        }

        val grpc = kind == TransportKind.Grpc
        return Checked(
            if (kind == TransportKind.Tcp) {
                TransportOptions.Tcp
            } else {
                TransportOptions(
                    kind,
                    if (grpc) null else query(query, "path"),
                    if (grpc) null else query(query, "host"),
                    if (grpc) query(query, "serviceName") else null,
                )
            },
            null,
        )
    }

    private fun security(query: Query, byDefault: TransportSecurity): Checked<SecurityOptions> {
        val kind = when (query(query, "security")?.lowercase(Locale.ROOT)) {
            null -> byDefault
            "none" -> TransportSecurity.None
            "tls" -> TransportSecurity.Tls
            "reality" -> TransportSecurity.Reality
            else -> return Checked(SecurityOptions.None, ImportErrors.UNSUPPORTED_PARAMETER)
        }

        val alpn = query(query, "alpn")?.split(',')?.map { it.trim() }?.filter { it.isNotEmpty() }
        val reality = kind == TransportSecurity.Reality
        val tls = kind == TransportSecurity.Tls
        val pinned = if (tls) pinned(query(query, "pcs")) else emptyList()
        val names = if (tls) query(query, "vcn")?.split(',')?.map { it.trim() }?.filter { it.isNotEmpty() } else null
        val security = if (kind == TransportSecurity.None) {
            SecurityOptions.None
        } else {
            SecurityOptions(
                kind,
                query(query, "sni") ?: query(query, "peer"),
                query(query, "fp"),
                alpn?.takeIf { it.isNotEmpty() },
                isTrue(query(query, "allowInsecure") ?: query(query, "insecure") ?: query(query, "allow_insecure")),
                if (reality) query(query, "pbk") else null,
                if (reality) query(query, "sid") else null,
                if (reality) query(query, "spx") else null,
                pinned?.takeIf { it.isNotEmpty() },
                names?.takeIf { it.isNotEmpty() },
            )
        }

        // Закрепленный сертификат с ошибкой: без него сервер проверялся бы иначе, чем задумано (решение 480).
        if (pinned == null) {
            return Checked(security, ImportErrors.UNSUPPORTED_PARAMETER)
        }

        // REALITY без открытого ключа сервера не подключится.
        return Checked(security, if (reality && security.realityPublicKey == null) ImportErrors.NO_CREDENTIALS else null)
    }

    /**
     * «pcs» — SHA-256 сертификатов через запятую, как у Xray: 64 знака hex, двоеточия OpenSSL допустимы. Результат — строчные
     * без двоеточий; пусто — пустой список; хоть одно значение не SHA-256 — null.
     */
    internal fun pinned(text: String?): List<String>? {
        val hashes = mutableListOf<String>()
        for (part in (text ?: "").split(',').map { it.trim() }.filter { it.isNotEmpty() }) {
            val hash = part.replace(":", "").lowercase(Locale.ROOT)
            if (hash.length != 64 || !hash.all { it in '0'..'9' || it in 'a'..'f' }) {
                return null
            }

            hashes += hash
        }

        return hashes
    }

    // ── Мелочи ───────────────────────────────────────────────────────────────

    private fun ok(
        name: String,
        protocol: ProxyProtocol,
        host: String,
        port: Int,
        transport: TransportOptions,
        security: SecurityOptions,
        options: ProtocolOptions,
        credentials: ServerCredentials,
    ): LinkResult {
        val (country, cleanName) = CountryFlag.split(name)
        return LinkResult(
            ServerDefinition(cleanName.ifEmpty { host }, country, protocol, host, port, transport, security, options, credentials),
            null,
        )
    }

    private fun fail(error: String) = LinkResult(null, error)

    /** Первая ошибка из проверок по порядку; проверки уже выполнены все. */
    private fun firstError(vararg errors: String?): String? = errors.firstOrNull { it != null }

    /** Значение параметра; пустое — как отсутствующее. */
    private fun query(query: Query, key: String): String? = query[key]?.takeIf { it.isNotEmpty() }

    private fun isTrue(value: String?) = value == "1" || value.equals("true", ignoreCase = true)

    private fun isHost(host: String) =
        host.length in 1..253 && host.none { it.isWhitespace() || it == '/' || it == '@' || it == '?' || it == '#' || it == '[' || it == ']' }

    /** Порт: только цифры (без знака и пробелов), 1–65535. */
    private fun port(text: String?): Int? = digits(text)?.takeIf { it in 1..65535 }

    /** Число из одних цифр, как `int.TryParse(…, NumberStyles.None)` десктопа; иначе null. */
    private fun digits(text: String?): Int? =
        if (text.isNullOrEmpty() || text.any { it !in '0'..'9' }) null else text.toIntOrNull()

    /**
     * «%XX» — в символы UTF-8, как `Uri.UnescapeDataString` десктопа: «+» не превращается в пробел (в base64 ключах он —
     * часть данных), неверная последовательность остается как есть.
     */
    internal fun unescape(text: String): String {
        if (!text.contains('%')) {
            return text
        }

        val result = StringBuilder(text.length)
        var i = 0
        while (i < text.length) {
            if (text[i] != '%') {
                result.append(text[i++])
                continue
            }

            // Подряд идущие «%XX» — одна последовательность байтов UTF-8.
            val start = i
            val bytes = java.io.ByteArrayOutputStream()
            while (i + 2 < text.length && text[i] == '%' && hex(text[i + 1]) >= 0 && hex(text[i + 2]) >= 0) {
                bytes.write(hex(text[i + 1]) * 16 + hex(text[i + 2]))
                i += 3
            }

            if (i == start) {
                result.append(text[i++])
                continue
            }

            result.append(utf8(bytes.toByteArray()) ?: text.substring(start, i))
        }

        return result.toString()
    }

    /** Экранирование, как `Uri.EscapeDataString`: остаются только A–Z, a–z, 0–9 и «-._~». */
    internal fun escape(text: String): String {
        val result = StringBuilder(text.length)
        for (byte in text.toByteArray(Charsets.UTF_8)) {
            val c = (byte.toInt() and 0xFF).toChar()
            if (c in 'A'..'Z' || c in 'a'..'z' || c in '0'..'9' || c == '-' || c == '.' || c == '_' || c == '~') {
                result.append(c)
            } else {
                result.append('%').append("%02X".format(byte.toInt() and 0xFF))
            }
        }

        return result.toString()
    }

    private fun hex(c: Char): Int = when (c) {
        in '0'..'9' -> c - '0'
        in 'a'..'f' -> c - 'a' + 10
        in 'A'..'F' -> c - 'A' + 10
        else -> -1
    }

    /** Строгий UTF-8: неверные байты — null. */
    private fun utf8(bytes: ByteArray): String? = try {
        Charsets.UTF_8.newDecoder()
            .onMalformedInput(CodingErrorAction.REPORT)
            .onUnmappableCharacter(CodingErrorAction.REPORT)
            .decode(ByteBuffer.wrap(bytes))
            .toString()
    } catch (_: CharacterCodingException) {
        null
    }

    /** base64 обычный или url-safe, с выравниванием «=» или без; null — не base64 или не UTF-8. */
    internal fun decodeBase64(text: String): String? {
        val compact = StringBuilder(text.length)
        for (c in text) {
            if (!c.isWhitespace()) {
                compact.append(
                    when (c) {
                        '-' -> '+'
                        '_' -> '/'
                        else -> c
                    },
                )
            }
        }

        while (compact.length % 4 != 0) {
            compact.append('=')
        }

        val bytes = try {
            Base64.getDecoder().decode(compact.toString())
        } catch (_: IllegalArgumentException) {
            return null
        }

        return utf8(bytes)
    }
}

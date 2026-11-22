package com.privategateway.client.core.text

import com.privategateway.client.core.links.JsonText
import java.net.URLEncoder

/**
 * Скрывает секреты в строке перед записью в журнал — как `SecretMasker` десктопа, те же правила: ссылки на серверы,
 * пути и параметры адресов подписок, UUID, ключи. Журнал пользователь может отправить в отчете, поэтому в нем не должно
 * быть ничего, по чему можно подключиться к его серверу.
 */
object SecretMasker {
    /** Чем заменяется скрытая часть. */
    const val HIDDEN = "[скрыто]"

    /** Известные учетные данные с формами из ошибок URL и JSON; длинные формы скрываются раньше частей. */
    fun knownVariants(secrets: Iterable<String>): List<String> {
        val values = linkedSetOf<String>()
        fun add(value: String) {
            if (value.isEmpty()) return
            values += value
            val json = JsonText.value(value).drop(1).dropLast(1)
            values += json
            values += json.replace("/", "\\/")
            val uri = URLEncoder.encode(value, Charsets.UTF_8.name()).replace("+", "%20").replace("*", "%2A").replace("%7E", "~")
            values += uri
            values += uri.replace("%20", "+")
            values += Regex("%[A-Fa-f0-9]{2}").replace(uri) { it.value.lowercase() }
        }
        for (secret in secrets) {
            add(secret)
            val trimmed = secret.trim()
            add(trimmed)
            if (trimmed.startsWith("Bearer ", ignoreCase = true) || trimmed.startsWith("Basic ", ignoreCase = true)) add(trimmed.substringAfter(' ').trim())
        }
        return values.sortedByDescending { it.length }
    }

    fun mask(text: String?, knownSecrets: Iterable<String>): String {
        var result = text.orEmpty()
        for (secret in knownVariants(knownSecrets)) {
            result = if (secret.length >= 4) result.replace(secret, HIDDEN)
            else Regex("(?<![\\p{L}\\p{N}_])${Regex.escape(secret)}(?![\\p{L}\\p{N}_])").replace(result) { HIDDEN }
        }
        return mask(result)
    }

    // Ссылки серверов: все после схемы — учетные данные и параметры.
    private val proxyLink = Regex("""\b(?<scheme>vless|vmess|trojan|ss|ssr|hysteria2|hy2|tuic|wireguard|wg)://[^\s"'<>]+""", RegexOption.IGNORE_CASE)

    // Адрес подписки: путь и параметры часто и есть токен доступа, хост оставляем.
    private val httpUrl = Regex("""(?<origin>\bhttps?://[^\s/"'<>?#]+)(?<rest>[/?#][^\s"'<>]*)""", RegexOption.IGNORE_CASE)

    private val uuid = Regex("""\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b""", RegexOption.IGNORE_CASE)

    // Кандидаты в ключи REALITY и WireGuard — base64 длиной от 32 знаков; решает looksLikeKey.
    private val longKey = Regex("""(?<![A-Za-z0-9+/_-])[A-Za-z0-9+/_-]{32,}={0,2}(?![A-Za-z0-9+/_=-])""")

    /** Строка без секретов. Порядок правил важен: сначала ссылки целиком. */
    fun mask(text: String?): String {
        if (text.isNullOrEmpty()) {
            return ""
        }

        var result = proxyLink.replace(text) { it.groups["scheme"]!!.value + "://" + HIDDEN }
        result = httpUrl.replace(result) { if (it.groups["rest"]!!.value.length > 1) it.groups["origin"]!!.value + "/" + HIDDEN else it.value }
        result = uuid.replace(result, HIDDEN)
        result = longKey.replace(result) { if (looksLikeKey(it.value)) HIDDEN else it.value }
        return result
    }

    // Случайный ключ почти всегда содержит цифру, строчную и заглавную букву; путь к файлу начинается с «/» или состоит
    // из слов — его не трогаем.
    private fun looksLikeKey(value: String) =
        value[0] != '/' &&
            value.count { it == '/' } <= 4 &&
            value.any { it in '0'..'9' } &&
            value.any { it in 'A'..'Z' } &&
            value.any { it in 'a'..'z' }
}

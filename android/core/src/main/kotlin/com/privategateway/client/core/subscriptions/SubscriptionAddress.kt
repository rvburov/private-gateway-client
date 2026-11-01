package com.privategateway.client.core.subscriptions

import java.net.URI
import java.net.URISyntaxException
import java.util.Locale

/**
 * Адрес подписки — секрет (токен доступа обычно в пути или в запросе): для подписи только «https://example.com/sub/» и скрытая часть «****»,
 * как `SubscriptionAddress` десктопа; целиком он хранится запечатанным.
 */
object SubscriptionAddress {
    const val HIDDEN = "****"

    /** Скрытая часть адресов, сохраненных до звездочек (просьба пользователя 2026-11-01). */
    private const val LEGACY_HIDDEN = "\u2022\u2022\u2022\u2022"

    /** Сегмент пути длиннее — скорее токен, чем слово вроде «sub» или «clash». */
    private const val WORD_LENGTH = 12

    /** Адрес подписки: http или https с сервером; иначе null. */
    fun parse(text: String): URI? {
        val uri = try {
            URI(text.trim())
        } catch (_: URISyntaxException) {
            return null
        }

        val scheme = uri.scheme?.lowercase(Locale.ROOT)
        return if ((scheme == "https" || scheme == "http") && !uri.host.isNullOrEmpty()) uri else null
    }

    /** Подписка без шифрования: ключи серверов шли открытым текстом (import.insecure). */
    fun insecure(url: URI) = url.scheme.equals("http", ignoreCase = true)

    /**
     * Схема, сервер и первый сегмент пути, если он похож на слово; остальное — «****». Имя и пароль перед сервером,
     * остаток пути, запрос и якорь не показываются никогда.
     */
    /** Сохраненный ранее адрес для подписи: скрытая часть — звездочками, как у новых. */
    fun shown(stored: String): String = stored.replace(LEGACY_HIDDEN, HIDDEN)

    fun mask(url: URI): String {
        val head = url.scheme.lowercase(Locale.ROOT) + "://" + url.host + (if (url.port >= 0 && url.port != defaultPort(url)) ":" + url.port else "")
        val segments = (url.rawPath ?: "").split('/').filter { it.isNotEmpty() }
        val hidden = segments.size > 1 || !url.rawQuery.isNullOrEmpty() || !url.rawFragment.isNullOrEmpty()
        if (segments.isEmpty()) {
            return if (hidden) "$head/$HIDDEN" else head
        }

        return if (word(segments[0])) head + "/" + segments[0] + (if (hidden) "/$HIDDEN" else "") else "$head/$HIDDEN"
    }

    /**
     * Тот же адрес подписки (решение 528, как `SubscriptionAddress.Same` десктопа): целиком — с путем, токеном и запросом;
     * регистр схемы и сервера, порт по умолчанию и последний «/» пути не важны. Другой токен — другая подписка.
     */
    fun same(left: URI, right: URI): Boolean = canonical(left) == canonical(right)

    private fun canonical(url: URI): String {
        val scheme = url.scheme?.lowercase(Locale.ROOT) ?: ""
        val port = if (url.port < 0 || url.port == defaultPort(url)) "" else ":" + url.port
        val user = url.rawUserInfo?.let { "$it@" } ?: ""
        return scheme + "://" + user + (url.host ?: "").lowercase(Locale.ROOT) + port + (url.rawPath ?: "").trimEnd('/') +
            (url.rawQuery?.let { "?$it" } ?: "") + (url.rawFragment?.let { "#$it" } ?: "")
    }

    private fun defaultPort(url: URI) = if (url.scheme.equals("https", ignoreCase = true)) 443 else 80

    private fun word(segment: String) =
        segment.length <= WORD_LENGTH && segment.all { it in 'a'..'z' || it in 'A'..'Z' || it == '-' || it == '_' }
}

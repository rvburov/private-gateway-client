package com.privategateway.client.core.subscriptions

import com.privategateway.client.core.links.LinkParser

/**
 * Название подписки из заголовка ответа Profile-Title — как `SubscriptionContent.TitleFrom` десктопа: «base64:…» —
 * раскодировать; иначе только печатный ASCII (заголовки HTTP — латиница, кириллица без base64 приходит испорченной);
 * управляющие символы — нет названия; длина — не больше [MAX].
 */
object SubscriptionTitle {
    const val MAX = 100

    fun from(header: String?): String? {
        var value = header?.trim()
        if (value.isNullOrEmpty()) {
            return null
        }

        if (value.startsWith("base64:", ignoreCase = true)) {
            value = LinkParser.decodeBase64(value.substring("base64:".length))?.trim()
        } else if (value.any { it > '~' }) {
            return null
        }

        if (value.isNullOrEmpty() || value.any { it.isISOControl() }) {
            return null
        }

        return if (value.length > MAX) value.substring(0, MAX).trimEnd() else value
    }
}

/** Почему подписка не загрузилась — те же причины, что у десктопа; тексты — import.download.*. */
enum class DownloadFailure {
    Network,
    NameResolution,
    Connection,
    Tls,
    Timeout,

    /** Сервер ответил не 2xx; код ответа хранится рядом. */
    Status,
    TooLarge,

    /** Загрузилось, но ни одного сервера не разобрано — прошлый список оставлен. */
    NoServers,

    /** Адрес подписки не открылся: запечатанный адрес не расшифровать (ключ Keystore потерян). */
    NoAddress,
    ;

    companion object {
        /**
         * Причина, сохраненная у подписки: null — неудачи не было; неизвестное имя (из будущей версии) — [Network], как
         * у десктопа.
         */
        fun parse(name: String?): DownloadFailure? = name?.let { value -> entries.firstOrNull { it.name == value } ?: Network }
    }
}

/** Пределы загрузки подписки — как у десктопа: 20 секунд и 5 МБ после распаковки. */
object DownloadLimits {
    const val TIMEOUT_MS = 20_000
    const val MAX_BYTES = 5 * 1024 * 1024
}

package com.privategateway.client.core.servers

/** Почему у сервера нет задержки — как `LatencyFailure` десктопа. */
enum class LatencyFailure {
    /** Ядро Xray за этот сервер не возьмется (Hysteria2, TUIC, нет ключа…) — замер не делался. */
    Unsupported,

    /** Через сервер не пришел ответ за отведенное время. */
    NoResponse,

    /** Ядро не запустилось или отказало целиком — проверка не состоялась. */
    CoreFailed,
}

/**
 * Почему через сервер не пришел ответ — `LatencyDiagnostic` десктопа («Timeout», «NameResolution», «Tls», «Connection»):
 * по тексту ошибки ядра. Сам текст (в нем бывают адреса) не хранится — только вид.
 */
enum class LatencyReason {
    /** Время ожидания истекло. */
    Timeout,

    /** Не удалось определить адрес сервера или проверочного сайта. */
    Dns,

    /** TLS-соединение через сервер не установилось. */
    Tls,

    /** Соединение не установилось: отказ, сброс, сеть недоступна. */
    Connection,
    ;

    companion object {
        /** Вид по тексту ошибки libXray; непонятный — null («нет ответа»). Таймаут ядра без текста — [Timeout]. */
        fun of(error: String?, timedOut: Boolean = false): LatencyReason? {
            val text = error?.lowercase().orEmpty()
            return when {
                "no such host" in text || "lookup " in text || "server misbehaving" in text || "dns" in text -> Dns
                "tls" in text || "x509" in text || "handshake" in text || "reality" in text || "certificate" in text -> Tls
                "deadline exceeded" in text || "timeout" in text || "timed out" in text -> Timeout
                "refused" in text || "reset" in text || "unreachable" in text || "eof" in text || "broken pipe" in text ||
                    "no route" in text -> Connection
                timedOut -> Timeout
                else -> null
            }
        }

        /** Из записи хранилища «NoResponse/Timeout»: часть после «/»; нет или неизвестная — null. */
        fun read(stored: String?): LatencyReason? = stored?.substringAfter('/', "")?.let { name -> entries.firstOrNull { it.name == name } }
    }
}

/**
 * Последний замер задержки через сервер (как `LatencyResult` десктопа): миллисекунды или причина, почему их нет. Сервер,
 * который ни разу не мерили, — не [Latency], а null у [ServerItem.latency].
 */
data class Latency(val ms: Int?, val failure: LatencyFailure?, val reason: LatencyReason? = null) {
    /** Запись отказа для хранилища: «NoResponse» или «NoResponse/Timeout» — прежние записи без «/» читаются как были. */
    val stored: String? get() = failure?.let { it.name + (reason?.let { r -> "/" + r.name } ?: "") }

    companion object {
        /** Ответ пришел; меньше миллисекунды — 1, чтобы не показывать «0 мс». */
        fun of(ms: Int) = Latency(maxOf(1, ms), null)

        fun failed(failure: LatencyFailure, reason: LatencyReason? = null) = Latency(null, failure, reason)

        /** Отказ из записи хранилища ([stored]); неизвестный вид — «нет ответа». */
        fun read(stored: String): Latency = failed(
            LatencyFailure.entries.firstOrNull { it.name == stored.substringBefore('/') } ?: LatencyFailure.NoResponse,
            LatencyReason.read(stored),
        )
    }
}

/** Как показать задержку: быстрый — зеленым, медленный — серым, нет ответа — «нет» оранжевым, не мерили — «—». */
enum class LatencyKind {
    Good,
    Slow,
    NoAnswer,
    Never,
    ;

    companion object {
        /** Задержка больше этой — медленно (у десктопа `ServersPageViewModel.SlowLatencyMs`). */
        const val SLOW_MS = 120

        fun of(latency: Latency?): LatencyKind {
            val ms = latency?.ms
            return when {
                latency == null -> Never
                ms == null -> NoAnswer
                ms > SLOW_MS -> Slow
                else -> Good
            }
        }
    }
}

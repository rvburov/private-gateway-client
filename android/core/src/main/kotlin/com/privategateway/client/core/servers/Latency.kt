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
 * Последний замер задержки через сервер (как `LatencyResult` десктопа): миллисекунды или причина, почему их нет. Сервер,
 * который ни разу не мерили, — не [Latency], а null у [ServerItem.latency].
 */
data class Latency(val ms: Int?, val failure: LatencyFailure?) {
    companion object {
        /** Ответ пришел; меньше миллисекунды — 1, чтобы не показывать «0 мс». */
        fun of(ms: Int) = Latency(maxOf(1, ms), null)

        fun failed(failure: LatencyFailure) = Latency(null, failure)
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

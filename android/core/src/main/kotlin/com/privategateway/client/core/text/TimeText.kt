package com.privategateway.client.core.text

import java.time.Clock
import java.time.Duration
import java.time.Instant
import java.time.LocalDate
import java.time.LocalTime
import java.time.temporal.ChronoUnit

/** Когда — для подписей «обновлено в 18:10», «вчера», «22 окт.». */
sealed interface Day {
    /** Сегодня — время с точностью до минуты (when.at, «HH:mm»). */
    data class Today(val time: LocalTime) : Day

    /** Вчера (when.yesterday). */
    data object Yesterday : Day

    /** Раньше (или позже — часы перевели назад) — дата («d MMM» на языке интерфейса). */
    data class Date(val date: LocalDate) : Day
}

/** Сколько прошло — «только что», «5 мин назад», «2 ч назад», «3 дн назад» (ago.*). */
sealed interface Ago {
    data object Now : Ago

    data class Minutes(val count: Int) : Ago

    data class Hours(val count: Int) : Ago

    data class Days(val count: Int) : Ago
}

/**
 * Время в подписях — как `TimeText` десктопа, но значения, а не текст: строку своего языка собирает приложение. Время —
 * миллисекунды от эпохи (как в базе), «сейчас» и часовой пояс — из [Clock].
 */
object TimeText {
    /** Сегодня — время, вчера — «вчера», иначе — дата; сутки — по часовому поясу [clock]. */
    fun day(at: Long, clock: Clock): Day {
        val local = Instant.ofEpochMilli(at).atZone(clock.zone)
        val today = LocalDate.now(clock)
        return when (local.toLocalDate()) {
            today -> Day.Today(local.toLocalTime().truncatedTo(ChronoUnit.MINUTES))
            today.minusDays(1) -> Day.Yesterday
            else -> Day.Date(local.toLocalDate())
        }
    }

    /** Минуты, часы или дни, отброшенные вниз; меньше минуты (и время из будущего) — «только что». */
    fun ago(at: Long, clock: Clock): Ago {
        val span = Duration.ofMillis(clock.millis() - at)
        return when {
            span < Duration.ofMinutes(1) -> Ago.Now
            span < Duration.ofHours(1) -> Ago.Minutes(span.toMinutes().toInt())
            span < Duration.ofDays(1) -> Ago.Hours(span.toHours().toInt())
            else -> Ago.Days(span.toDays().toInt())
        }
    }
}

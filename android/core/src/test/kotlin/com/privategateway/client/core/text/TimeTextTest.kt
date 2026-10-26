package com.privategateway.client.core.text

import org.junit.jupiter.api.Test
import org.junit.jupiter.params.ParameterizedTest
import org.junit.jupiter.params.provider.CsvSource
import java.time.Clock
import java.time.LocalDate
import java.time.LocalDateTime
import java.time.LocalTime
import java.time.ZoneId
import java.time.ZoneOffset
import kotlin.test.assertEquals

/**
 * Время в подписях — как `TimeText` десктопа: «обновлено в 15:42», «вчера», «2 ч назад» на доске 12
 * (`SubscriptionsPageTests`). Часы стоят: 24 октября 2026, 15:42 по Москве.
 */
class TimeTextTest {
    private val moscow = ZoneId.of("Europe/Moscow")
    private val now = LocalDateTime.of(2026, 10, 24, 15, 42, 30)
    private val clock = clock(now, moscow)

    private fun clock(at: LocalDateTime, zone: ZoneId) = Clock.fixed(at.atZone(zone).toInstant(), zone)

    private fun at(time: LocalDateTime, zone: ZoneId = moscow) = time.atZone(zone).toInstant().toEpochMilli()

    @Test
    fun `today is the time yesterday is a word earlier is the date`() {
        assertEquals(Day.Today(LocalTime.of(15, 42)), TimeText.day(at(now), clock))
        assertEquals(Day.Today(LocalTime.of(13, 42)), TimeText.day(at(now.minusHours(2)), clock))
        assertEquals(Day.Today(LocalTime.MIDNIGHT), TimeText.day(at(LocalDateTime.of(2026, 10, 24, 0, 0)), clock))
        assertEquals(Day.Yesterday, TimeText.day(at(LocalDateTime.of(2026, 10, 23, 23, 59)), clock))
        assertEquals(Day.Yesterday, TimeText.day(at(LocalDateTime.of(2026, 10, 23, 0, 0)), clock))
        assertEquals(Day.Date(LocalDate.of(2026, 10, 22)), TimeText.day(at(LocalDateTime.of(2026, 10, 22, 23, 59)), clock))
        assertEquals(Day.Date(LocalDate.of(2026, 10, 25)), TimeText.day(at(now.plusDays(1)), clock), "часы перевели назад — дата")
    }

    @Test
    fun `yesterday starts at local midnight not 24 hours ago`() {
        val justAfterMidnight = clock(LocalDateTime.of(2026, 10, 24, 0, 5), moscow)

        assertEquals(Day.Yesterday, TimeText.day(at(LocalDateTime.of(2026, 10, 23, 23, 55)), justAfterMidnight), "10 минут назад, но до полуночи")
        assertEquals(Day.Today(LocalTime.of(0, 1)), TimeText.day(at(LocalDateTime.of(2026, 10, 24, 0, 1)), justAfterMidnight))
    }

    @Test
    fun `days follow the time zone of the clock`() {
        // 22:30 по UTC 23 октября — это 01:30 24 октября по Москве.
        val instant = at(LocalDateTime.of(2026, 10, 23, 22, 30), ZoneOffset.UTC)

        assertEquals(Day.Today(LocalTime.of(1, 30)), TimeText.day(instant, clock))
        assertEquals(Day.Yesterday, TimeText.day(instant, clock.withZone(ZoneOffset.UTC)))
    }

    @ParameterizedTest
    @CsvSource(
        // прошло секунд → что сказать
        "0, Now, 0",
        "59, Now, 0",
        "60, Minutes, 1",
        "3599, Minutes, 59",
        "3600, Hours, 1",
        "7200, Hours, 2",
        "86399, Hours, 23",
        "86400, Days, 1",
        "277200, Days, 3",
        "-120, Now, 0",
    )
    fun `ago counts whole minutes hours and days`(seconds: Long, kind: String, count: Int) {
        val expected = when (kind) {
            "Now" -> Ago.Now
            "Minutes" -> Ago.Minutes(count)
            "Hours" -> Ago.Hours(count)
            else -> Ago.Days(count)
        }

        assertEquals(expected, TimeText.ago(clock.millis() - seconds * 1000, clock))
    }
}

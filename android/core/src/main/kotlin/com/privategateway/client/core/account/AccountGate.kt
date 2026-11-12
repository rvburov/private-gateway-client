package com.privategateway.client.core.account

import java.time.Instant

/** Что делать со входом после проверки доступа. */
sealed interface AccessVerdict {
    /** Доступ есть до [expiresAt]: сайт подтвердил ([confirmed]) или не ответил, а срок еще идет. */
    data class Keep(val expiresAt: Instant, val confirmed: Boolean) : AccessVerdict

    /** Сайт ответил 401: стереть токен, выключить защиту, экран входа с «Доступ отозван». */
    data object Revoked : AccessVerdict

    /** Сайт не ответил, а подтвержденный срок прошел: выход как при истечении («Срок входа истек»), защита выключается. */
    data object Expired : AccessVerdict
}

/**
 * Правило доступа приложения (одинаково с десктопом, решение 618): решает сайт. Отзыв — только ответ 401; сеть, таймаут и
 * 5xx — не отзыв: действует последний подтвержденный срок, иначе телефон под «Блокировать подключения без VPN» или без
 * сети остался бы без защиты из-за сбоя сайта. Срок прошел без подтверждения — выход.
 */
object AccountGate {
    /** Проверка, пока приложение работает: раз в 30 минут (и при запуске, и при открытии окна). */
    const val CHECK_EVERY_MS = 30 * 60_000L

    /** Открытие окна чаще этого не проверяет снова: запуск и показ окна идут подряд. */
    const val RECHECK_AFTER_MS = 60_000L

    /** Можно ли подключаться: вход есть и его срок не прошел. */
    fun allowed(expiresAt: Instant?, now: Instant): Boolean = expiresAt != null && now.isBefore(expiresAt)

    /** Итог проверки [status] для входа со сроком [expiresAt]. */
    fun verdict(expiresAt: Instant, status: StatusResult, now: Instant): AccessVerdict = when (status) {
        is StatusResult.Ok -> AccessVerdict.Keep(status.expiresAt, confirmed = true)
        StatusResult.Revoked -> AccessVerdict.Revoked
        StatusResult.Unreachable -> if (allowed(expiresAt, now)) AccessVerdict.Keep(expiresAt, confirmed = false) else AccessVerdict.Expired
    }
}

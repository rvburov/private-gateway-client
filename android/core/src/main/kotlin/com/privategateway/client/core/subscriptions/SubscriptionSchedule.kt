package com.privategateway.client.core.subscriptions

/** Как часто обновлять подписки сами — как у десктопа («Профили и подписки», доска 12); по умолчанию — 12 часов. */
enum class RefreshInterval(val hours: Long?) {
    /** Только кнопкой «Обновить». */
    Off(null),
    Every6Hours(6),
    Every12Hours(12),
    Every24Hours(24),
}

/** Когда подписку обновляли: успешно и последняя попытка (мс от эпохи); null — ни разу. */
data class SubscriptionTimes(val id: String, val updatedAt: Long?, val lastAttemptAt: Long?)

/**
 * Каким подпискам пора обновиться — правило `SubscriptionUpdater.Due` десктопа: успех старше промежутка из настроек,
 * неудача — не чаще раза в час, чтобы не стучаться в недоступный сервер подписки каждую проверку.
 */
object SubscriptionSchedule {
    const val RETRY_AFTER_FAILURE_MS = 60L * 60 * 1000

    fun due(subscriptions: List<SubscriptionTimes>, interval: RefreshInterval, now: Long): List<String> {
        val period = (interval.hours ?: return emptyList()) * 60 * 60 * 1000
        return subscriptions.filter { subscription ->
            (subscription.updatedAt == null || now - subscription.updatedAt >= period) &&
                (subscription.lastAttemptAt == null || now - subscription.lastAttemptAt >= RETRY_AFTER_FAILURE_MS)
        }.map { it.id }
    }
}

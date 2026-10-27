package com.privategateway.client.core.settings

import com.privategateway.client.core.subscriptions.RefreshInterval

/**
 * Настройки «Профили и подписки» (доска 12) — как у десктопа (`AppSettings`): обновлять каждые 12 часов, только через
 * VPN, сообщать, если выбранный сервер пропал из подписки или изменился.
 */
data class SubscriptionSettings(
    val refresh: RefreshInterval = RefreshInterval.Every12Hours,
    val onlyViaVpn: Boolean = true,
    val notifySelected: Boolean = true,
) {
    /**
     * Нужна ли проверка в фоне, когда процесс приложения не живет (WorkManager, решение 443): автообновление включено, а
     * загрузка не ждет защиты. С «только через VPN» без защиты загрузка не идет, а под защитой процесс жив — служба VPN.
     */
    val background: Boolean get() = refresh != RefreshInterval.Off && !onlyViaVpn

    companion object {
        /**
         * Из сохраненных значений: нет значения или неизвестное имя промежутка (из будущей версии) — значение по
         * умолчанию, как `SqliteSettingsStore` десктопа.
         */
        fun read(refresh: String?, onlyViaVpn: Boolean?, notifySelected: Boolean?): SubscriptionSettings {
            val defaults = SubscriptionSettings()
            return SubscriptionSettings(
                refresh = RefreshInterval.entries.firstOrNull { it.name == refresh } ?: defaults.refresh,
                onlyViaVpn = onlyViaVpn ?: defaults.onlyViaVpn,
                notifySelected = notifySelected ?: defaults.notifySelected,
            )
        }
    }
}

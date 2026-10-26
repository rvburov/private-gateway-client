package com.privategateway.client.core.subscriptions

/** Почему обновление подписки сейчас не начинается — как `UpdateBlock` десктопа; «службы нет» на Android не бывает. */
enum class UpdateBlock {
    None,

    /** «Обновлять только через VPN», а защита не включена. */
    ProtectionOff,

    /** Эта подписка уже обновляется. */
    Busy,
}

/**
 * Можно ли обновлять подписку сейчас — правило `SubscriptionUpdater.Block` десктопа: с «только через VPN» адрес подписки
 * уходит в сеть только через туннель — пока защита не подключена, ничего не загружается; потом — не обновляется ли
 * подписка уже. Проверка по расписанию смотрит только на защиту ([busy] = false).
 */
object UpdateGate {
    fun block(onlyViaVpn: Boolean, connected: Boolean, busy: Boolean = false): UpdateBlock = when {
        onlyViaVpn && !connected -> UpdateBlock.ProtectionOff
        busy -> UpdateBlock.Busy
        else -> UpdateBlock.None
    }
}

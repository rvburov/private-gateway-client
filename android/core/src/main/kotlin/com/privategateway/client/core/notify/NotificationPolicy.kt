package com.privategateway.client.core.notify

import com.privategateway.client.core.subscriptions.FailureReason
import com.privategateway.client.core.subscriptions.SelectedChange
import com.privategateway.client.core.vpn.TunnelErrors
import com.privategateway.client.core.vpn.TunnelState
import com.privategateway.client.core.vpn.TunnelStatus

/** Какие уведомления показывать (доска 22) — как у десктопа; по умолчанию «Только важные». */
enum class NotificationLevel {
    /** Важные и обычные. */
    All,

    /** Обрыв, сервер не отвечает, защита не включилась. */
    ImportantOnly,

    /** Никаких, кроме постоянного уведомления «Защита»: Android требует его, пока VPN работает. */
    Off,
    ;

    companion object {
        val DEFAULT = ImportantOnly

        /** Из сохраненного имени: нет значения или неизвестное (из будущей версии) — [DEFAULT]. */
        fun read(name: String?): NotificationLevel = entries.firstOrNull { it.name == name } ?: DEFAULT
    }
}

/**
 * Какое уведомление дает событие — правила `NotificationPolicy` десктопа и событий подписок из `NotificationCenter`, без
 * окна, Android и часов. «Блокировки» на Android нет: сдавшийся туннель — «Ошибка», а держит ли трафик система
 * («Блокировать подключения без VPN»), говорит `lockdown`.
 */
object NotificationPolicy {
    /**
     * Уведомление о смене состояния туннеля — по двум состояниям подряд; свежие цифры и новые попытки в том же состоянии
     * уведомлений не дают.
     *
     * @param server название сервера защиты.
     * @param lockdown включена ли системная «Блокировать подключения без VPN» (последнее известное; неизвестно — false:
     *   честнее сказать «трафик идет напрямую», чем обещать блок).
     * @return уведомление; null — сказать нечего.
     */
    fun forChange(before: TunnelStatus, after: TunnelStatus, server: String?, lockdown: Boolean): Notice? {
        val moved = before.state != after.state
        val changed = moved || before.error != after.error
        val code = after.error
        val traffic = if (lockdown) "blocked" else "direct"
        val notice = when {
            // Пока идут попытки, карта туннеля остается: трафик ждет — «Интернет заблокирован» при любой настройке Android.
            after.state == TunnelState.Reconnecting && moved && before.state == TunnelState.Connected ->
                Notice(NoticeKind.ConnectionLost, "notify.lost.blocked", listOf(NoticeAction.ChangeServer, NoticeAction.Open))
            after.state == TunnelState.Error && changed && code == TunnelErrors.SERVER_NOT_RESPONDING ->
                Notice(NoticeKind.ServerDown, "notify.down.$traffic", listOf(NoticeAction.Retry, NoticeAction.ChooseServer))
            after.state == TunnelState.Error && changed ->
                Notice(NoticeKind.ProtectionFailed, "notify.failed.$traffic", listOf(NoticeAction.Open))
            after.state == TunnelState.Connected && moved ->
                Notice(NoticeKind.Connected, if (before.state == TunnelState.Reconnecting) "notify.on.back" else "notify.on.text", emptyList())
            after.state == TunnelState.Off && moved ->
                Notice(NoticeKind.Disconnected, "notify.off.text", listOf(NoticeAction.Connect))
            else -> return null
        }

        return notice.copy(server = server, code = if (notice.kind == NoticeKind.ProtectionFailed) code ?: Notice.UNKNOWN_CODE else null)
    }

    /** Выбранный сервер пропал из подписки или изменился; на месте — сказать нечего. */
    fun forSelected(change: SelectedChange, server: String, subscription: String): Notice? = when (change) {
        SelectedChange.None -> null
        SelectedChange.Changed -> Notice(
            NoticeKind.ServerChanged, "notify.changed.text", listOf(NoticeAction.ChooseServer, NoticeAction.Later),
            server = server, subscription = subscription,
        )

        SelectedChange.Missing -> Notice(
            NoticeKind.ServerGone, "notify.gone.text", listOf(NoticeAction.ChooseOther, NoticeAction.Later),
            server = server, subscription = subscription,
        )
    }

    /** Подписка не обновилась по расписанию — прошлый список оставлен. */
    fun forStale(subscription: String, failure: FailureReason): Notice =
        Notice(NoticeKind.ServersNotUpdated, "notify.stale.text", listOf(NoticeAction.OpenSubscriptions), subscription = subscription, failure = failure)

    /** Пускает ли уровень из настроек уведомление в шторку. */
    fun allowed(level: NotificationLevel, notice: Notice): Boolean = when (level) {
        NotificationLevel.All -> true
        NotificationLevel.ImportantOnly -> notice.important
        NotificationLevel.Off -> false
    }
}

/**
 * Повтор того же события — не чаще [REPEAT_AFTER_MS] (10 минут), как `NotificationCenter.RepeatAfter` десктопа: событие
 * узнается по виду и подписке, сервер не в счет. Отмечается только показанное ([shown]) — уведомление, которое скрыли
 * уровень или открытое окно, следующее не задерживает. Время — явно, мс по одним часам (в приложении — elapsedRealtime:
 * перевод часов телефона паузу не ломает).
 */
class NoticeThrottle {
    private val shown = HashMap<Pair<NoticeKind, String>, Long>()

    /** Можно показать: того же события не было последние 10 минут. */
    @Synchronized
    fun allows(notice: Notice, now: Long): Boolean = shown[key(notice)]?.let { now - it >= REPEAT_AFTER_MS } ?: true

    /** Уведомление показано в [now]. */
    @Synchronized
    fun shown(notice: Notice, now: Long) {
        shown[key(notice)] = now
    }

    private fun key(notice: Notice) = notice.kind to (notice.subscription ?: "")

    companion object {
        const val REPEAT_AFTER_MS = 10L * 60 * 1000
    }
}

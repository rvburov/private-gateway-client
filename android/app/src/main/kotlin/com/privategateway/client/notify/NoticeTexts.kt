package com.privategateway.client.notify

import androidx.annotation.StringRes
import com.privategateway.client.R
import com.privategateway.client.core.notify.NoticeAction
import com.privategateway.client.core.notify.NoticeKind

/**
 * Тексты уведомлений — ключи notify.* из shared/specifications/localization, явными соответствиями: опечатка в ключе —
 * ошибка компиляции, а ключ из NotificationPolicy, которого здесь нет, ловит тест.
 */
object NoticeTexts {
    @StringRes
    fun title(kind: NoticeKind): Int = when (kind) {
        NoticeKind.ConnectionLost -> R.string.notify_lost_title
        NoticeKind.ServerDown -> R.string.notify_down_title
        NoticeKind.ProtectionFailed -> R.string.notify_failed_title
        NoticeKind.Connected -> R.string.notify_on_title
        NoticeKind.Disconnected -> R.string.notify_off_title
        NoticeKind.ServerGone -> R.string.notify_gone_title
        NoticeKind.ServerChanged -> R.string.notify_changed_title
        NoticeKind.ServersNotUpdated -> R.string.notify_stale_title
    }

    /** Текст по ключу уведомления; в нем {0} — сервер, {1} — подписка, {2} — причина. Неизвестный ключ — null. */
    @StringRes
    fun text(key: String): Int? = when (key) {
        "notify.lost.blocked" -> R.string.notify_lost_blocked
        "notify.lost.direct" -> R.string.notify_lost_direct
        "notify.down.blocked" -> R.string.notify_down_blocked
        "notify.down.direct" -> R.string.notify_down_direct
        "notify.failed.blocked" -> R.string.notify_failed_blocked
        "notify.failed.direct" -> R.string.notify_failed_direct
        "notify.on.text" -> R.string.notify_on_text
        "notify.on.back" -> R.string.notify_on_back
        "notify.off.text" -> R.string.notify_off_text
        "notify.gone.text" -> R.string.notify_gone_text
        "notify.changed.text" -> R.string.notify_changed_text
        "notify.stale.text" -> R.string.notify_stale_text
        else -> null
    }

    @StringRes
    fun action(action: NoticeAction): Int = when (action) {
        NoticeAction.Open -> R.string.notify_action_open
        NoticeAction.ChangeServer -> R.string.notify_action_changeServer
        NoticeAction.ChooseServer -> R.string.notify_action_chooseServer
        NoticeAction.ChooseOther -> R.string.notify_action_chooseOther
        NoticeAction.Retry -> R.string.notify_action_retry
        NoticeAction.Later -> R.string.notify_action_later
        NoticeAction.Connect -> R.string.notify_action_connect
        NoticeAction.OpenSubscriptions -> R.string.notify_action_openSubscriptions
    }
}

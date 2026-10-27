package com.privategateway.client.core.notify

import com.privategateway.client.core.subscriptions.FailureReason

/**
 * О чем бывают уведомления (доски 19 и 22) — как `NoticeKind` десктопа, кроме входа в Wi-Fi (портал входа Android
 * открывает сам), пропажи службы (она в процессе приложения) и обновления программы. Важные — со звуком (обрыв, сервер
 * не отвечает, защита не включилась), остальные — обычные, без звука; [titleKey] — заголовок.
 */
enum class NoticeKind(val important: Boolean, val titleKey: String) {
    /** «Защищено» сменилось «Переподключением»: связь с сервером пропала. */
    ConnectionLost(true, "notify.lost.title"),

    /** Попытки кончились: «Ошибка» с PGC-NET-001. */
    ServerDown(true, "notify.down.title"),

    /** Защита не включилась или остановилась по другой причине — текст по коду ошибки. */
    ProtectionFailed(true, "notify.failed.title"),

    /** Защита включилась или вернулась. */
    Connected(false, "notify.on.title"),

    /** Защиту выключили. */
    Disconnected(false, "notify.off.title"),

    /** Выбранный сервер пропал из подписки. */
    ServerGone(false, "notify.gone.title"),

    /** У выбранного сервера в подписке новый адрес или ключи. */
    ServerChanged(false, "notify.changed.title"),

    /** Подписка не обновилась по расписанию — прошлый список оставлен. */
    ServersNotUpdated(false, "notify.stale.title"),
}

/** Кнопки уведомления; надписи — notify.action.* ([textKey]). */
enum class NoticeAction {
    /** «Открыть»: окно приложения. */
    Open,

    /** «Сменить сервер»: экран «Серверы». */
    ChangeServer,

    /** «Выбрать сервер»: экран «Серверы». */
    ChooseServer,

    /** «Выбрать другой»: экран «Серверы». */
    ChooseOther,

    /** «Повторить»: подключиться к выбранному серверу снова. */
    Retry,

    /** «Позже»: закрыть уведомление. */
    Later,

    /** «Подключить»: включить защиту. */
    Connect,

    /** «Открыть» у подписок: экран «Профили и подписки». */
    OpenSubscriptions,
    ;

    val textKey: String get() = "notify.action." + name.replaceFirstChar { it.lowercase() }
}

/**
 * Что сказать: событие, ключ текста и кнопки (не больше двух — доска 22). В тексте `{0}` — сервер, `{1}` — подписка,
 * `{2}` — причина: код ошибки словами и сам код («Ядро не запустилось · PGC-SVC-002») или почему подписка не загрузилась
 * ([failure], тексты import.download.*). IP-адресов и имен сетей Wi-Fi в уведомлениях нет.
 */
data class Notice(
    val kind: NoticeKind,
    val textKey: String,
    val actions: List<NoticeAction>,
    val server: String? = null,
    val subscription: String? = null,
    val code: String? = null,
    val failure: FailureReason? = null,
) {
    val important: Boolean get() = kind.important

    /**
     * Метка: новое уведомление с той же меткой заменяет прежнее — о защите в шторке одно, последнее; у каждой подписки
     * свое.
     */
    val tag: String
        get() = when (kind) {
            NoticeKind.ServerGone, NoticeKind.ServerChanged, NoticeKind.ServersNotUpdated -> "servers-" + (subscription ?: "")
            else -> PROTECTION_TAG
        }

    companion object {
        /** Метка уведомлений о защите: обрыв, сервер, сбой, подключение. */
        const val PROTECTION_TAG = "protection"

        /** Код сбоя, у которого своего кода нет, — как у десктопа. */
        const val UNKNOWN_CODE = "PGC-SVC"
    }
}

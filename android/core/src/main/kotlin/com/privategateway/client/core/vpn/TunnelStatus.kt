package com.privategateway.client.core.vpn

/** Состояние туннеля — его меняет только служба VPN приложения; как `ConnectionState` десктопа, без «Блокировки». */
enum class TunnelState {
    /** VPN выключен: трафик идет напрямую или стоит под системной «Блокировать подключения без VPN». */
    Off,

    /** Туннель поднимается. */
    Connecting,

    /** Туннель работает. */
    Connected,

    /** Связь с сервером пропала, служба пробует снова; карта туннеля остается — трафик ждет, а не идет мимо. */
    Reconnecting,

    /** Туннель закрывается. */
    Stopping,

    /** Защита не включилась или остановилась: ядра нет, код — в [TunnelStatus.error]. */
    Error,
}

/**
 * Что показывают окно и уведомления: состояние, сервер, с какого момента в сети (elapsedRealtime), код ошибки (PGC-…,
 * тексты — error.<код>), номер попытки переподключения (1, 2, … в [TunnelState.Reconnecting], вне него 0 — как `Attempt`
 * десктопа) и системные «Постоянная VPN» и «Блокировать подключения без VPN» — Android сообщает их только работающей
 * службе VPN и только с Android 10, иначе null.
 */
data class TunnelStatus(
    val state: TunnelState = TunnelState.Off,
    val serverId: String? = null,
    val since: Long? = null,
    val error: String? = null,
    val alwaysOn: Boolean? = null,
    val lockdown: Boolean? = null,
    val attempt: Int = 0,
)

/** Коды ошибок туннеля — те же, что у десктопа (docs/architecture/errors.md). */
object TunnelErrors {
    /** Ядро не запустилось. */
    const val CORE_FAILED = "PGC-SVC-002"

    /** Запрос не выполнить: сервер не выбран или удален из списка. */
    const val NO_SERVER = "PGC-SVC-003"

    /** Ядро не умеет работать с этим сервером. */
    const val UNSUPPORTED = "PGC-SVC-005"

    /** Сервер не отвечает: попытки переподключения кончились по политике («До 5 минут», «Не повторять»). */
    const val SERVER_NOT_RESPONDING = "PGC-NET-001"

    /** Android не дал поднять VPN: VPN другой программы занял место или разрешение отозвано. */
    const val OTHER_VPN = "PGC-NET-010"
}

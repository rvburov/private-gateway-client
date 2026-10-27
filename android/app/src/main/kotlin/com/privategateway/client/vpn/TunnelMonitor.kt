package com.privategateway.client.vpn

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import javax.inject.Inject
import javax.inject.Singleton

/** Состояние туннеля — его меняет только [GatewayVpnService]. */
enum class TunnelState {
    Off,
    Connecting,
    Connected,
    Stopping,
    Error,
}

/**
 * Что показывает интерфейс: состояние, сервер, с какого момента в сети (elapsedRealtime), код ошибки (PGC-…, тексты —
 * error.<код>) и системные «Постоянная VPN» и «Блокировать подключения без VPN» — Android сообщает их только работающей
 * службе VPN и только с Android 10, иначе null.
 */
data class TunnelStatus(
    val state: TunnelState = TunnelState.Off,
    val serverId: String? = null,
    val since: Long? = null,
    val error: String? = null,
    val alwaysOn: Boolean? = null,
    val lockdown: Boolean? = null,
)

/** Общий для службы и окна (один процесс) поток состояния туннеля. */
@Singleton
class TunnelMonitor @Inject constructor() {
    private val current = MutableStateFlow(TunnelStatus())

    val status: StateFlow<TunnelStatus> = current.asStateFlow()

    fun set(status: TunnelStatus) {
        current.value = status
    }

    fun update(change: (TunnelStatus) -> TunnelStatus) = current.update(change)
}

/** Коды ошибок туннеля — те же, что у десктопа (docs/architecture/errors.md). */
object TunnelErrors {
    /** Ядро не запустилось. */
    const val CORE_FAILED = "PGC-SVC-002"

    /** Запрос не выполнить: сервер не выбран или удален из списка. */
    const val NO_SERVER = "PGC-SVC-003"

    /** Ядро не умеет работать с этим сервером. */
    const val UNSUPPORTED = "PGC-SVC-005"

    /** Android не дал поднять VPN: VPN другой программы занял место или разрешение отозвано. */
    const val OTHER_VPN = "PGC-NET-010"
}

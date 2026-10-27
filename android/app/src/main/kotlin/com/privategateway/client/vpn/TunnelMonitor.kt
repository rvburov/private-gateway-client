package com.privategateway.client.vpn

import com.privategateway.client.core.vpn.TunnelStatus
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import javax.inject.Inject
import javax.inject.Singleton

/**
 * Общий для службы и окна (один процесс) поток состояния туннеля и того, закрыта ли сеть приложению самим Android
 * ([blocked], пишет `NetworkAccess`).
 */
@Singleton
class TunnelMonitor @Inject constructor() {
    private val current = MutableStateFlow(TunnelStatus())
    private val closed = MutableStateFlow(false)

    val status: StateFlow<TunnelStatus> = current.asStateFlow()

    /** Android закрыл сеть приложению: «Блокировать подключения без VPN», а туннеля нет (Android 10+). */
    val blocked: StateFlow<Boolean> = closed.asStateFlow()

    fun setBlocked(value: Boolean) {
        closed.value = value
    }

    fun set(status: TunnelStatus) {
        current.value = status
    }

    fun update(change: (TunnelStatus) -> TunnelStatus) = current.update(change)
}

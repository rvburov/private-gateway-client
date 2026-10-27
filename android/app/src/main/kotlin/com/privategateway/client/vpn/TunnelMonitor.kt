package com.privategateway.client.vpn

import com.privategateway.client.core.vpn.TunnelStatus
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import javax.inject.Inject
import javax.inject.Singleton

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

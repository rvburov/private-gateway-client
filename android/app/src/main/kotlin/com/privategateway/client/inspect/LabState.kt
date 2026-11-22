package com.privategateway.client.inspect

import com.privategateway.client.core.inspect.PhoneNetwork
import com.privategateway.client.core.inspect.ProbeResult
import com.privategateway.client.core.vpn.TunnelState

/** Снимки лаборатории содержат лишь реально полученные данные и живут в памяти экрана. */
data class LabSnapshot(val at: Long, val state: TunnelState, val networks: List<PhoneNetwork>, val address: String?, val localOnly: Boolean = false)

data class LabUi(
    val target: String = "",
    val running: Boolean = false,
    val results: List<ProbeResult> = emptyList(),
    val changed: Boolean = false,
    val invalid: Boolean = false,
    val failed: Boolean = false,
    val snapshots: List<LabSnapshot> = emptyList(),
)

package com.privategateway.client.vpn

import android.app.PendingIntent
import android.content.Intent
import android.graphics.drawable.Icon
import android.net.VpnService
import android.os.Build
import android.service.quicksettings.Tile
import android.service.quicksettings.TileService
import androidx.annotation.StringRes
import com.privategateway.client.MainActivity
import com.privategateway.client.R
import com.privategateway.client.data.SettingsRepository
import dagger.hilt.android.AndroidEntryPoint
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.launch
import javax.inject.Inject

/** Как плитка показывает состояние: включена ли и подпись под названием (Android 10+). */
data class TileLook(val active: Boolean, @param:StringRes val subtitle: Int) {
    companion object {
        fun of(state: TunnelState, hasServer: Boolean): TileLook = when (state) {
            TunnelState.Connected -> TileLook(true, R.string.status_connected_title)
            TunnelState.Connecting -> TileLook(true, R.string.power_label_connecting)
            TunnelState.Stopping -> TileLook(false, R.string.power_label_off)
            TunnelState.Error -> TileLook(false, R.string.power_label_error)
            TunnelState.Off -> TileLook(false, if (hasServer) R.string.power_label_off else R.string.power_label_noServer)
        }
    }
}

/**
 * Плитка в шторке (доска 26): включает и выключает VPN, не открывая приложение. Сервер — только выбранный; не выбран
 * или Android еще не дал разрешения на VPN — открывается окно. На заблокированном экране — только после разблокировки:
 * выключить защиту с экрана блокировки не должен посторонний.
 */
@AndroidEntryPoint
class GatewayTileService : TileService() {
    @Inject lateinit var monitor: TunnelMonitor

    @Inject lateinit var settings: SettingsRepository

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private var listening: Job? = null

    @Volatile
    private var hasServer = false

    override fun onStartListening() {
        super.onStartListening()
        listening?.cancel()
        listening = scope.launch {
            monitor.status.combine(settings.selectedServerId) { status, selected -> status.state to (selected != null) }.collect { (state, server) ->
                hasServer = server
                show(TileLook.of(state, server))
            }
        }
    }

    override fun onStopListening() {
        listening?.cancel()
        listening = null
        super.onStopListening()
    }

    override fun onDestroy() {
        scope.cancel()
        super.onDestroy()
    }

    override fun onClick() {
        super.onClick()
        if (isLocked) {
            unlockAndRun { toggle() }
        } else {
            toggle()
        }
    }

    private fun toggle() {
        when (monitor.status.value.state) {
            TunnelState.Connected, TunnelState.Connecting -> GatewayVpnService.disconnect(this)
            TunnelState.Stopping -> Unit
            TunnelState.Off, TunnelState.Error ->
                if (hasServer && VpnService.prepare(this) == null) GatewayVpnService.connect(this) else openApp()
        }
    }

    private fun show(look: TileLook) {
        val tile = qsTile ?: return
        tile.state = if (look.active) Tile.STATE_ACTIVE else Tile.STATE_INACTIVE
        tile.label = getString(R.string.app_title)
        tile.icon = Icon.createWithResource(this, R.drawable.pg_notification)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            tile.subtitle = getString(look.subtitle)
        }

        tile.updateTile()
    }

    private fun openApp() {
        val intent = Intent(this, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
            startActivityAndCollapse(PendingIntent.getActivity(this, 2, intent, PendingIntent.FLAG_IMMUTABLE))
        } else {
            @Suppress("DEPRECATION")
            startActivityAndCollapse(intent)
        }
    }
}

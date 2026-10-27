package com.privategateway.client.ui.settings

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.privategateway.client.core.settings.TunnelSettings
import com.privategateway.client.core.vpn.SystemVpn
import com.privategateway.client.core.vpn.SystemVpnSource
import com.privategateway.client.core.vpn.SystemVpnView
import com.privategateway.client.data.SettingsRepository
import com.privategateway.client.vpn.TunnelMonitor
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch
import javax.inject.Inject

/** Настройки окна «Настройки». Под защитой их применяет служба сама (переподключение без окна утечки, решение 410). */
@HiltViewModel
class SettingsViewModel @Inject constructor(private val settings: SettingsRepository, monitor: TunnelMonitor) : ViewModel() {
    val tunnel: StateFlow<TunnelSettings> = settings.tunnel.stateIn(viewModelScope, SharingStarted.Eagerly, TunnelSettings())

    /** «Постоянная VPN» и блок без VPN: под защитой — что сообщил Android, без нее — запомненное (решение 445). */
    val systemVpn: StateFlow<SystemVpnView> = monitor.status.combine(settings.systemVpn) { status, remembered -> SystemVpnView.of(status, remembered) }
        .stateIn(viewModelScope, SharingStarted.Eagerly, SystemVpnView(SystemVpn(), SystemVpnSource.Unknown))

    /** Уведомления и переподключение; новая политика переподключения действует со следующей неудачи. */
    val behavior: StateFlow<Behavior> = settings.notifications.combine(settings.reconnect) { notifications, reconnect -> Behavior(notifications, reconnect) }
        .stateIn(viewModelScope, SharingStarted.Eagerly, Behavior())

    private val page = MutableStateFlow<SettingsPage?>(null)

    /** Страница, которую открыть сразу (кнопка уведомления о подписке), — до [pageShown]. */
    val requestedPage: StateFlow<SettingsPage?> = page.asStateFlow()

    fun setTunnel(next: TunnelSettings) {
        viewModelScope.launch { settings.setTunnel(next) }
    }

    fun setBehavior(next: Behavior) {
        val before = behavior.value
        viewModelScope.launch {
            if (next.notifications != before.notifications) {
                settings.setNotifications(next.notifications)
            }

            if (next.reconnect != before.reconnect) {
                settings.setReconnect(next.reconnect)
            }
        }
    }

    fun request(next: SettingsPage) {
        page.value = next
    }

    fun pageShown() {
        page.value = null
    }
}

package com.privategateway.client.ui.settings

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.privategateway.client.core.settings.TunnelSettings
import com.privategateway.client.data.SettingsRepository
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch
import javax.inject.Inject

/** Настройки окна «Настройки». Под защитой их применяет служба сама (переподключение без окна утечки, решение 410). */
@HiltViewModel
class SettingsViewModel @Inject constructor(private val settings: SettingsRepository) : ViewModel() {
    val tunnel: StateFlow<TunnelSettings> = settings.tunnel.stateIn(viewModelScope, SharingStarted.Eagerly, TunnelSettings())

    fun setTunnel(next: TunnelSettings) {
        viewModelScope.launch { settings.setTunnel(next) }
    }
}

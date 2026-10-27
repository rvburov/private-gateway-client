package com.privategateway.client.ui.subscriptions

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.privategateway.client.R
import com.privategateway.client.core.settings.SubscriptionSettings
import com.privategateway.client.core.subscriptions.SubscriptionAddress
import com.privategateway.client.core.subscriptions.UpdateBlock
import com.privategateway.client.core.vpn.TunnelState
import com.privategateway.client.data.GroupItem
import com.privategateway.client.data.ServerRepository
import com.privategateway.client.data.SettingsRepository
import com.privategateway.client.subscriptions.SubscriptionUpdater
import com.privategateway.client.subscriptions.UpdateOutcome
import com.privategateway.client.ui.Message
import com.privategateway.client.vpn.TunnelControl
import com.privategateway.client.vpn.TunnelMonitor
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch
import javax.inject.Inject

/** «Профили и подписки»: группы, сколько серверов в «Моих серверах», настройки, что обновляется, подключена ли защита. */
data class SubscriptionsUi(
    val groups: List<GroupItem> = emptyList(),
    val manualCount: Int = 0,
    val settings: SubscriptionSettings = SubscriptionSettings(),
    val busy: Set<String> = emptySet(),
    val connected: Boolean = false,
)

@HiltViewModel
class SubscriptionsViewModel @Inject constructor(
    private val servers: ServerRepository,
    private val settings: SettingsRepository,
    private val updater: SubscriptionUpdater,
    monitor: TunnelMonitor,
    private val control: TunnelControl,
) : ViewModel() {
    val ui: StateFlow<SubscriptionsUi> = combine(
        servers.groups,
        servers.manualCount,
        settings.subscriptions,
        updater.busy,
        monitor.status,
    ) { groups, manual, subscriptions, busy, status ->
        SubscriptionsUi(groups, manual, subscriptions, busy, status.state == TunnelState.Connected)
    }.stateIn(viewModelScope, SharingStarted.Eagerly, SubscriptionsUi())

    private val notices = MutableSharedFlow<Message>(extraBufferCapacity = 8)

    /** Итоги действий — коротким сообщением, как у десктопа. */
    val messages: SharedFlow<Message> = notices.asSharedFlow()

    fun setSettings(value: SubscriptionSettings) {
        viewModelScope.launch { settings.setSubscriptions(value) }
    }

    /** «Обновить»: «обновлена: новых N, удалено M», «не обновилась», «сначала включите защиту». */
    fun update(group: GroupItem) {
        viewModelScope.launch {
            val message = when (val outcome = updater.update(group.id)) {
                is UpdateOutcome.Updated -> Message(R.string.subs_updated, group.name, outcome.added.toString(), outcome.removed.toString())
                is UpdateOutcome.Failed -> Message(R.string.subs_failed, group.name)
                is UpdateOutcome.Blocked -> if (outcome.block == UpdateBlock.ProtectionOff) Message(R.string.subs_blocked_off) else null
                UpdateOutcome.Gone -> null
            }

            message?.let { notices.emit(it) }
        }
    }

    /**
     * «Изменить»: название и, если введен, новый адрес — он сразу проверяется загрузкой, как у десктопа. Неверный адрес
     * (не http(s)) — false, ничего не сохраняется; пустое поле адреса — адрес прежний.
     */
    fun edit(group: GroupItem, name: String, url: String): Boolean {
        val address = url.trim().takeIf { it.isNotEmpty() }?.let { SubscriptionAddress.parse(it) ?: return false }
        viewModelScope.launch {
            servers.edit(group.id, name, address)
            if (address != null) {
                update(group.copy(name = name.trim().ifEmpty { group.name }))
            }
        }

        return true
    }

    /**
     * Удалить группу со всеми серверами; null — «Мои серверы». Защита идет через ее сервер — VPN отключается, другой
     * сервер пользователь выберет сам; выбранный из группы — выбор снимается.
     */
    fun remove(group: String?, name: String, inUse: Boolean) {
        if (inUse) {
            control.disconnect()
        }

        viewModelScope.launch {
            val removed = servers.remove(group)
            if (settings.selectedServerId.first() in removed) {
                settings.select(null)
            }

            notices.emit(Message(R.string.subs_removed, name))
        }
    }
}

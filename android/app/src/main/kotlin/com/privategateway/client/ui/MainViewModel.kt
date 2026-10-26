package com.privategateway.client.ui

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.privategateway.client.core.links.ImportBatch
import com.privategateway.client.core.links.ImportParser
import com.privategateway.client.data.ServerItem
import com.privategateway.client.data.ServerRepository
import com.privategateway.client.data.SettingsRepository
import com.privategateway.client.vpn.CoreEngine
import com.privategateway.client.vpn.TunnelMonitor
import com.privategateway.client.vpn.TunnelState
import com.privategateway.client.vpn.TunnelStatus
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import javax.inject.Inject

/**
 * Состояние окна: туннель, список серверов, выбранный и рабочий сервер. Рабочий — тот, через который идет защита (его
 * назвала служба); защита выключена — выбранный (как ServerFacts.InUse у десктопа).
 */
data class MainUi(
    val status: TunnelStatus = TunnelStatus(),
    val servers: List<ServerItem> = emptyList(),
    val selectedId: String? = null,
) {
    val inUse: ServerItem?
        get() = status.serverId?.takeIf { status.state != TunnelState.Off }?.let { id -> servers.firstOrNull { it.id == id } }
            ?: servers.firstOrNull { it.id == selectedId }
}

/** Разобранный текст импорта и итог добавления (null — еще не добавляли). */
data class ImportUi(val batch: ImportBatch? = null, val added: Int? = null, val duplicates: Int = 0)

@HiltViewModel
class MainViewModel @Inject constructor(
    monitor: TunnelMonitor,
    private val servers: ServerRepository,
    private val settings: SettingsRepository,
    private val engine: CoreEngine,
) : ViewModel() {
    val ui: StateFlow<MainUi> = combine(monitor.status, servers.servers, settings.selectedServerId) { status, list, selected ->
        MainUi(status, list, selected)
    }.stateIn(viewModelScope, SharingStarted.Eagerly, MainUi())

    private val importState = MutableStateFlow(ImportUi())
    val importing: StateFlow<ImportUi> = importState.asStateFlow()

    private val coreVersion = MutableStateFlow<String?>(null)
    val core: StateFlow<String?> = coreVersion.asStateFlow()

    /** Выбор сервера — только нажатием пользователя; работающая защита переходит на него при следующем подключении. */
    fun select(id: String) {
        viewModelScope.launch { settings.select(id) }
    }

    fun delete(id: String) {
        viewModelScope.launch {
            servers.delete(id)
            if (settings.selectedServerId.first() == id) {
                settings.select(null)
            }
        }
    }

    /** Разобрать текст: ссылки, base64, sing-box или Clash — тем же разбором, что у десктопа (shared/testdata). */
    fun preview(text: String) {
        viewModelScope.launch {
            val batch = withContext(Dispatchers.Default) { ImportParser.parse(text) }
            importState.value = ImportUi(batch)
        }
    }

    /** Добавить разобранные серверы; ничего не было выбрано — выбрать первый новый, иначе выбор не меняется. */
    fun add() {
        val batch = importState.value.batch ?: return
        viewModelScope.launch {
            val parsed = batch.lines.mapNotNull { it.result.server }
            val before = servers.servers.first().map { it.id }.toSet()
            val result = servers.add(parsed)
            if (settings.selectedServerId.first() == null) {
                servers.servers.first().firstOrNull { it.id !in before }?.let { settings.select(it.id) }
            }

            importState.value = ImportUi(batch, result.added, result.duplicates)
        }
    }

    fun closeImport() {
        importState.value = ImportUi()
    }

    /** Версия ядра Xray — для «Проверки» и «О программе»; ядро грузится в процессе, поэтому не в главном потоке. */
    fun loadCoreVersion() {
        if (coreVersion.value != null) {
            return
        }

        viewModelScope.launch {
            coreVersion.value = withContext(Dispatchers.IO) { runCatching { engine.version() }.getOrNull() }
        }
    }
}

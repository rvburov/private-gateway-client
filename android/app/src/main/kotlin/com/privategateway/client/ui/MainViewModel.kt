package com.privategateway.client.ui

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.privategateway.client.core.links.ImportBatch
import com.privategateway.client.core.links.ImportParser
import com.privategateway.client.core.subscriptions.DownloadFailure
import com.privategateway.client.core.subscriptions.FailureReason
import com.privategateway.client.core.subscriptions.SubscriptionAddress
import com.privategateway.client.core.subscriptions.SubscriptionDownloadException
import com.privategateway.client.core.subscriptions.SubscriptionFetch
import com.privategateway.client.core.subscriptions.SubscriptionSource
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
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.net.URI
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

    /** Что делает кнопка-щит: подключаться не к чему — серверов нет (добавить) или ни один не выбран (к списку). */
    val power: PowerAction
        get() {
            val off = status.state == TunnelState.Off || status.state == TunnelState.Error
            return when {
                !off || inUse != null -> PowerAction.Toggle
                servers.isEmpty() -> PowerAction.Add
                else -> PowerAction.Choose
            }
        }
}

enum class PowerAction {
    /** Включить или выключить защиту. */
    Toggle,

    /** Открыть добавление сервера. */
    Add,

    /** Открыть список серверов: сервер выбирает только пользователь. */
    Choose,
}

/**
 * Текст импорта и его разбор; адрес подписки — загруженная подписка ([url], название из Profile-Title, http без
 * шифрования), идет ли загрузка и почему не вышло; название профиля; идет ли добавление и его итог (null — еще не
 * добавляли). Адрес подписки — секрет: живет только здесь, в памяти окна.
 */
data class ImportUi(
    val text: String = "",
    val batch: ImportBatch? = null,
    val adding: Boolean = false,
    val added: Int? = null,
    val duplicates: Int = 0,
    val url: URI? = null,
    val insecure: Boolean = false,
    val downloading: Boolean = false,
    val failure: FailureReason? = null,
    val name: String = "",
)

@HiltViewModel
class MainViewModel @Inject constructor(
    monitor: TunnelMonitor,
    private val servers: ServerRepository,
    private val settings: SettingsRepository,
    private val engine: CoreEngine,
    private val source: SubscriptionSource,
) : ViewModel() {
    val ui: StateFlow<MainUi> = combine(monitor.status, servers.servers, settings.selectedServerId) { status, list, selected ->
        MainUi(status, list, selected)
    }.stateIn(viewModelScope, SharingStarted.Eagerly, MainUi())

    private val importState = MutableStateFlow(ImportUi())
    val importing: StateFlow<ImportUi> = importState.asStateFlow()

    private val tabRequest = MutableStateFlow<Tab?>(null)

    /** Раздел, который окно должно открыть (из уведомления); окно сбрасывает его, когда открыло. */
    val requestedTab: StateFlow<Tab?> = tabRequest.asStateFlow()

    private val coreVersion = MutableStateFlow<String?>(null)
    val core: StateFlow<String?> = coreVersion.asStateFlow()

    fun show(tab: Tab) {
        tabRequest.value = tab
    }

    fun tabShown() {
        tabRequest.value = null
    }

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

    fun edit(text: String) {
        importState.update { it.copy(text = text) }
    }

    private var previews = 0

    fun name(name: String) {
        importState.update { it.copy(name = name) }
    }

    /**
     * Разобрать текст: ссылки, base64, sing-box или Clash — тем же разбором, что у десктопа (shared/testdata). Одна строка
     * с адресом http(s) — подписка: сначала загрузить, потом разобрать ответ; название профиля — из Profile-Title или сервер
     * подписки. Ответ на прежнюю проверку, пришедший после новой, отбрасывается.
     */
    fun preview(text: String) {
        val attempt = ++previews
        val url = text.trim().takeIf { !it.contains('\n') }?.let { SubscriptionAddress.parse(it) }
        viewModelScope.launch {
            if (url == null) {
                val batch = withContext(Dispatchers.Default) { ImportParser.parse(text) }
                if (attempt == previews) {
                    importState.update { ImportUi(it.text, batch, name = it.name) }
                }

                return@launch
            }

            importState.update { ImportUi(it.text, downloading = true, name = it.name) }
            val downloaded = withContext(Dispatchers.IO) {
                try {
                    Result.success(source.download(url))
                } catch (e: SubscriptionDownloadException) {
                    Result.failure(e)
                } catch (e: Exception) {
                    Result.failure(SubscriptionDownloadException(DownloadFailure.Network).also { it.initCause(e) })
                }
            }

            if (attempt != previews) {
                return@launch
            }

            val content = downloaded.getOrNull()
            if (content == null) {
                val error = downloaded.exceptionOrNull() as? SubscriptionDownloadException
                importState.update {
                    ImportUi(it.text, failure = FailureReason.of(error?.failure ?: DownloadFailure.Network, error?.status), name = it.name)
                }

                return@launch
            }

            val batch = withContext(Dispatchers.Default) { ImportParser.parse(content.text) }
            importState.update {
                ImportUi(
                    it.text,
                    batch,
                    url = url,
                    insecure = SubscriptionAddress.insecure(url),
                    name = it.name.ifBlank { SubscriptionFetch.suggestName(content.title, url) },
                )
            }
        }
    }

    /**
     * Добавить разобранные серверы; ничего не было выбрано — выбрать первый новый, иначе выбор не меняется. Повторное
     * нажатие, пока идет добавление, не считается: второе добавление нашло бы одни дубли и затерло итог первого.
     */
    fun add() {
        val current = importState.value
        val batch = current.batch ?: return
        if (current.added != null || current.adding) {
            return
        }

        importState.value = current.copy(adding = true)
        viewModelScope.launch {
            val parsed = batch.lines.mapNotNull { it.result.server }
            val before = servers.servers.first().map { it.id }.toSet()
            // С названием — свой профиль (подписка, если загружено по адресу); без названия — «Мои серверы».
            val result = servers.add(parsed, current.name, current.url)
            if (settings.selectedServerId.first() == null) {
                servers.servers.first().firstOrNull { it.id !in before }?.let { settings.select(it.id) }
            }

            importState.update { if (it.batch === batch) it.copy(adding = false, added = result.added, duplicates = result.duplicates) else it }
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

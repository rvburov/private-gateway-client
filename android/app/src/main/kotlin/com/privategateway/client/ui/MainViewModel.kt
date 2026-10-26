package com.privategateway.client.ui

import androidx.annotation.StringRes
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.privategateway.client.R
import com.privategateway.client.core.links.ImportBatch
import com.privategateway.client.core.links.ImportParser
import com.privategateway.client.core.servers.ServerItem
import com.privategateway.client.core.servers.ServerNames
import com.privategateway.client.core.servers.SubscriptionRef
import com.privategateway.client.core.subscriptions.DownloadFailure
import com.privategateway.client.core.subscriptions.FailureReason
import com.privategateway.client.core.subscriptions.SubscriptionAddress
import com.privategateway.client.core.subscriptions.SubscriptionDownloadException
import com.privategateway.client.core.subscriptions.SubscriptionFetch
import com.privategateway.client.core.subscriptions.SubscriptionSource
import com.privategateway.client.data.ServerRepository
import com.privategateway.client.data.SettingsRepository
import com.privategateway.client.subscriptions.SubscriptionNotice
import com.privategateway.client.subscriptions.SubscriptionUpdater
import com.privategateway.client.vpn.CoreEngine
import com.privategateway.client.vpn.LatencyMeter
import com.privategateway.client.vpn.MeasureResult
import com.privategateway.client.vpn.TunnelControl
import com.privategateway.client.vpn.TunnelMonitor
import com.privategateway.client.vpn.TunnelState
import com.privategateway.client.vpn.TunnelStatus
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
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
 * Состояние окна: туннель, список серверов и их группы (подписки и профили), выбранный и рабочий сервер. Рабочий — тот,
 * через который идет защита (его назвала служба); защита выключена — выбранный (как ServerFacts.InUse у десктопа).
 */
data class MainUi(
    val status: TunnelStatus = TunnelStatus(),
    val servers: List<ServerItem> = emptyList(),
    val selectedId: String? = null,
    val subscriptions: List<SubscriptionRef> = emptyList(),
    val measuring: Boolean = false,
) {
    /** Защита идет или поднимается через этот сервер (строка «активен», «Используется сейчас» в карточке). */
    fun active(id: String): Boolean = status.state != TunnelState.Off && status.state != TunnelState.Error && status.serverId == id

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

/** Короткое сообщение окна: текст и подстановки. */
class Message(@param:StringRes val text: Int, vararg val args: String)

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
    private val monitor: TunnelMonitor,
    private val servers: ServerRepository,
    private val settings: SettingsRepository,
    private val engine: CoreEngine,
    private val source: SubscriptionSource,
    private val control: TunnelControl,
    private val meter: LatencyMeter,
    private val updater: SubscriptionUpdater,
) : ViewModel() {
    val ui: StateFlow<MainUi> = combine(
        monitor.status,
        servers.servers,
        settings.selectedServerId,
        servers.subscriptionRefs,
        meter.measuring,
    ) { status, list, selected, groups, measuring ->
        MainUi(status, list, selected, groups, measuring)
    }.stateIn(viewModelScope, SharingStarted.Eagerly, MainUi())

    private val notices = MutableSharedFlow<Message>(extraBufferCapacity = 8)

    /** Короткие сообщения окна: итог замера задержки, выбранный сервер пропал или изменился, подписка не обновилась. */
    val messages: SharedFlow<Message> = notices.asSharedFlow()

    init {
        viewModelScope.launch {
            updater.notices.collect { notice ->
                notices.emit(
                    when (notice) {
                        is SubscriptionNotice.Gone -> Message(R.string.subs_notice_missing, notice.server, notice.subscription)
                        is SubscriptionNotice.Changed -> Message(R.string.subs_notice_changed, notice.server, notice.subscription)
                        is SubscriptionNotice.Stale -> Message(R.string.subs_failed, notice.subscription)
                    },
                )
            }
        }
    }

    /** Окно на экране: не пора ли обновить подписки (как при открытии окна у десктопа). */
    fun windowShown() = updater.checkNow()

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

    /**
     * Выбор сервера — только нажатием пользователя. Тот же сервер — ничего не меняется; другой — запоминается для
     * «Недавних», и работающая защита сразу переходит на него, как SwitchServerAsync десктопа (путь без окна утечки,
     * решение 410).
     */
    fun select(id: String) {
        viewModelScope.launch {
            val state = monitor.status.value.state
            if (choose(id) && state != TunnelState.Off && state != TunnelState.Stopping) {
                control.connect()
            }
        }
    }

    /**
     * «Подключиться» в карточке: выбрать сервер и включить защиту через него. Защита выключена или с ошибкой — [start]
     * (разрешение на VPN спрашивает окно) после того, как выбор записан: служба читает выбранный сервер сама.
     */
    fun connectTo(id: String, start: () -> Unit) {
        viewModelScope.launch {
            val changed = choose(id)
            when (monitor.status.value.state) {
                TunnelState.Off, TunnelState.Error -> start()
                TunnelState.Connecting, TunnelState.Connected -> if (changed) control.connect()
                TunnelState.Stopping -> Unit
            }
        }
    }

    /** Записать выбор; тот же сервер — false, ничего не меняется (и «Недавние» тоже). */
    private suspend fun choose(id: String): Boolean {
        if (settings.selectedServerId.first() == id) {
            return false
        }

        servers.markUsed(id)
        settings.select(id)
        return true
    }

    /** Удалить сервер; защита идет через него — VPN отключается, другой сервер пользователь выберет сам (доска 18). */
    fun delete(id: String) {
        val status = monitor.status.value
        if (status.state != TunnelState.Off && status.serverId == id) {
            control.disconnect()
        }

        viewModelScope.launch {
            servers.delete(id)
            if (settings.selectedServerId.first() == id) {
                settings.select(null)
            }
        }
    }

    fun setFavorite(id: String, favorite: Boolean) {
        viewModelScope.launch { servers.setFavorite(id, favorite) }
    }

    /**
     * Свое название сервера: без пробелов по краям, 1–64 знака (иначе не сохраняется). Совпало с названием из ссылки —
     * своего нет: обновление подписки снова сможет его поменять.
     */
    fun rename(id: String, name: String) {
        val valid = ServerNames.validate(name) ?: return
        val server = ui.value.servers.firstOrNull { it.id == id } ?: return
        viewModelScope.launch { servers.rename(id, valid.takeIf { it != server.linkName }) }
    }

    /**
     * «Обновить»: задержка всех серверов через ядро — только без защиты (решение 430). Замер уже идет — нажатие не
     * считается; защита включена или включилась во время замера — сообщение, почему не мерили.
     */
    fun refreshLatency() {
        viewModelScope.launch {
            when (meter.measure()) {
                MeasureResult.Done -> notices.emit(Message(R.string.android_servers_ping_done))
                MeasureResult.Busy -> notices.emit(Message(R.string.android_servers_ping_busy))
                MeasureResult.Running -> Unit
            }
        }
    }

    /** Адрес сервера для карточки («адрес:порт»): открывается из запечатанной ссылки только по просьбе карточки. */
    suspend fun address(id: String): String? = servers.address(id)

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

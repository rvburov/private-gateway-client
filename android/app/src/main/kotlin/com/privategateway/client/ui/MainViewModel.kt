package com.privategateway.client.ui

import android.net.Uri
import androidx.annotation.StringRes
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.privategateway.client.R
import com.privategateway.client.core.inspect.PhoneNetworkKind
import com.privategateway.client.core.inspect.PublicAddress
import com.privategateway.client.core.links.ImportBatch
import com.privategateway.client.core.links.ImportParser
import com.privategateway.client.core.servers.ServerFacts
import com.privategateway.client.core.servers.ServerItem
import com.privategateway.client.core.servers.ServerNames
import com.privategateway.client.core.servers.SubscriptionRef
import com.privategateway.client.core.subscriptions.DownloadFailure
import com.privategateway.client.core.subscriptions.FailureReason
import com.privategateway.client.core.subscriptions.SubscriptionAddress
import com.privategateway.client.core.subscriptions.SubscriptionDownloadException
import com.privategateway.client.core.subscriptions.SubscriptionFetch
import com.privategateway.client.subscriptions.SubscriptionTransport
import com.privategateway.client.core.vpn.SystemVpnView
import com.privategateway.client.core.vpn.TunnelState
import com.privategateway.client.core.vpn.TunnelStatus
import com.privategateway.client.data.ServerRepository
import com.privategateway.client.data.SettingsRepository
import com.privategateway.client.inspect.PhoneNetworkSource
import com.privategateway.client.inspect.WebFetch
import com.privategateway.client.subscriptions.SubscriptionNotice
import com.privategateway.client.subscriptions.SubscriptionUpdater
import com.privategateway.client.ui.qr.QrImages
import com.privategateway.client.ui.qr.QrRead
import com.privategateway.client.ui.servers.FileRead
import com.privategateway.client.ui.servers.ImportFile
import com.privategateway.client.ui.servers.ImportFiles
import com.privategateway.client.vpn.ByteCounters
import com.privategateway.client.vpn.ByteSample
import com.privategateway.client.vpn.CoreEngine
import com.privategateway.client.vpn.LatencyMeter
import com.privategateway.client.vpn.MeasureResult
import com.privategateway.client.vpn.TunnelControl
import com.privategateway.client.vpn.TunnelMonitor
import com.privategateway.client.vpn.TrafficSpeed
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.flow.flowOn
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.net.URI
import javax.inject.Inject

/**
 * Состояние окна: туннель, список серверов и их группы (подписки и профили), выбранный и рабочий сервер. Рабочий — тот,
 * через который идет защита (его назвала служба); защита выключена — выбранный (как ServerFacts.InUse у десктопа).
 * [ip] — замеры внешнего IP для главной; [network] — сеть, через которую телефон выходит в интернет (null — сети нет).
 */
data class MainUi(
    val status: TunnelStatus = TunnelStatus(),
    val servers: List<ServerItem> = emptyList(),
    val selectedId: String? = null,
    val subscriptions: List<SubscriptionRef> = emptyList(),
    val measuring: Boolean = false,
    val blocked: Boolean = false,
    val ip: WebIp = WebIp(),
    val network: PhoneNetworkKind? = null,
) {
    /** Android закрыл интернет без туннеля — «Блокировать подключения без VPN» (решение 445). */
    val closedWithoutTunnel: Boolean get() = SystemVpnView.closedWithoutTunnel(status.state, blocked)

    /** Адрес выхода, измеренный в этом подключении; прежнего подключения не в счет. */
    private val exit: String? get() = ip.exit?.takeIf { status.state == TunnelState.Connected && ip.session == status.since }

    /** Защита включена, а замер видит реальный IP: выход совпал с адресом, измеренным без защиты в этой же сети. */
    val leak: Boolean get() = exit != null && exit == ip.direct

    /** Что показывает главная: состояние туннеля, блок Android и утечка (доска 09). */
    val view: ProtectionView get() = ProtectionView.of(status.state, closedWithoutTunnel, leak)

    /**
     * IP, который видят сайты (WebIp десктопа): под защитой — измеренный выход, без защиты и при ошибке без блока — реальный;
     * в подключении и блокировке — никакого: прямой путь закрыт, а выхода еще нет. Не измерен — null.
     */
    val webAddress: String?
        get() = when (view) {
            ProtectionView.Connected, ProtectionView.Leak -> exit
            ProtectionView.Off, ProtectionView.Error -> ip.direct
            else -> null
        }

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

/**
 * Короткое сообщение окна: текст и подстановки. [long] — сообщение о событии, а не итог нажатия: подсказка держится 6 с
 * вместо 1,8 с, его надо успеть прочесть (как LongDuration у десктопа).
 */
class Message(@param:StringRes val text: Int, vararg val args: String, val long: Boolean = false)

enum class PowerAction {
    /** Включить или выключить защиту. */
    Toggle,

    /** Открыть добавление сервера. */
    Add,

    /** Открыть список серверов: сервер выбирает только пользователь. */
    Choose,
}

/** Откуда текст для импорта — выбранная карточка «Добавить профиль» (ImportSource десктопа). */
enum class ImportSource {
    None,
    Clipboard,
    Qr,
    Link,
    File,
}

/**
 * Текст поля ссылки и разбор выбранного источника ([source]); до добавления — сколько из серверов уже есть ([duplicates]) и у
 * скольких сохраненных обновится защита ([renewals], решение 484). Адрес подписки — загруженная подписка ([url], название из
 * Profile-Title, http без шифрования), идет ли загрузка и почему не вышло; название профиля; идет ли добавление и его итог
 * (null — еще не добавляли: после — новых, дублей, обновленных и удаленных из подписки). Адрес подписки — секрет: живет только
 * здесь, в памяти окна.
 */
data class ImportUi(
    val text: String = "",
    val batch: ImportBatch? = null,
    val adding: Boolean = false,
    val added: Int? = null,
    val duplicates: Int = 0,
    val url: URI? = null,
    val insecure: Boolean = false,
    /** Эта подписка уже добавлена — ее название: «Добавить» обновит ее (решение 528). */
    val existing: String? = null,
    val downloading: Boolean = false,
    val failure: FailureReason? = null,
    val name: String = "",
    val qrReading: Boolean = false,
    @param:StringRes val qrProblem: Int? = null,
    val source: ImportSource = ImportSource.None,
    val renewals: Int = 0,
    /** Добавление обновило уже добавленную подписку: сколько серверов, пропавших из нее, удалено. */
    val removed: Int = 0,
    /** Что не вышло у источника: в буфере нет текста, файл не прочитан. */
    val problem: Message? = null,
) {
    /** Разобранных серверов — строки без ошибок. */
    val parsed: Int get() = batch?.let { it.found - it.failed } ?: 0

    /** Новых — разобранные без дублей: столько добавит «Добавить» (NewText десктопа). */
    val fresh: Int get() = (parsed - duplicates).coerceAtLeast(0)
}

@HiltViewModel
class MainViewModel @Inject constructor(
    private val monitor: TunnelMonitor,
    private val servers: ServerRepository,
    private val settings: SettingsRepository,
    private val engine: CoreEngine,
    private val source: SubscriptionTransport,
    private val control: TunnelControl,
    private val meter: LatencyMeter,
    private val updater: SubscriptionUpdater,
    private val qr: QrImages,
    private val web: WebFetch,
    private val networks: PhoneNetworkSource,
    private val counters: ByteCounters,
    private val files: ImportFiles,
) : ViewModel() {
    private val webIp = MutableStateFlow(WebIp())

    // Состояние замеров внешнего IP — до init: подписка в init идет сразу (Main.immediate) и уже пишет сюда.
    private var lastPath: IpPath? = null
    private var lastNetwork: List<String>? = null
    private var ipAttempt = 0

    val ui: StateFlow<MainUi> = combine(
        monitor.status.combine(monitor.blocked) { status, blocked -> status to blocked },
        servers.servers,
        settings.selectedServerId,
        servers.subscriptionRefs,
        combine(meter.measuring, webIp, networks.networks) { measuring, ip, list -> Triple(measuring, ip, HomeNetwork.pick(list)?.kind) },
    ) { (status, blocked), list, selected, groups, (measuring, ip, network) ->
        MainUi(status, list, selected, groups, measuring, blocked, ip, network)
    }.stateIn(viewModelScope, SharingStarted.Eagerly, MainUi())

    /**
     * Скорость туннеля для главной: раз в секунду, пока ее показывает окно и защита включена; иначе null («—»). Счетчики
     * читаются не в главном потоке: на новых Android это запрос к системе.
     */
    val speed: StateFlow<TrafficSpeed?> = flow<TrafficSpeed?> {
        var before: ByteSample? = null
        while (true) {
            val now = if (monitor.status.value.state == TunnelState.Connected) counters.read() else null
            emit(TrafficSpeed.between(before, now))
            before = now
            delay(SPEED_TICK_MS)
        }
    }.flowOn(Dispatchers.IO).stateIn(viewModelScope, SharingStarted.WhileSubscribed(SPEED_KEEP_MS), null)

    private val notices = MutableSharedFlow<Message>(extraBufferCapacity = 8)

    /**
     * Короткие сообщения окна: итог замера задержки, сервер удален, выбранный сервер пропал или изменился, подписка не
     * обновилась.
     */
    val messages: SharedFlow<Message> = notices.asSharedFlow()

    init {
        // Группы одной подписки, созданные до решения 528 повторным добавлением ссылки, — одна группа.
        viewModelScope.launch { servers.mergeSameSubscriptions(settings.selectedServerId.first()) }
        networks.start()
        // Внешний IP — сам, когда меняется путь запросов приложения: подключились — выход (проверка, нет ли утечки),
        // выключено без блока Android или сменилась сеть — реальный IP.
        viewModelScope.launch {
            combine(monitor.status, monitor.blocked, networks.networks) { status, blocked, list ->
                val network = HomeNetwork.identity(list)
                path(status, blocked, network) to network
            }.distinctUntilChanged().collect { (path, network) -> follow(path, network) }
        }
        viewModelScope.launch {
            updater.notices.collect { notice ->
                notices.emit(
                    when (notice) {
                        is SubscriptionNotice.Gone -> Message(R.string.subs_notice_missing, notice.server, notice.subscription, long = true)
                        is SubscriptionNotice.Changed -> Message(R.string.subs_notice_changed, notice.server, notice.subscription, long = true)
                        is SubscriptionNotice.Stale -> Message(R.string.subs_failed, notice.subscription, long = true)
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
                TunnelState.Connecting, TunnelState.Connected, TunnelState.Reconnecting -> if (changed) control.connect()
                TunnelState.Stopping -> Unit
            }
        }
    }

    /**
     * «Подключаться при запуске» (доска 4): пора ли подключить, когда окно открыли заново. Да — настройка включена, защита
     * выключена и выбран сервер, к которому телефон подключается (Hysteria2 и TUIC ядру телефона не по силам). Сервер
     * выбирает только пользователь: без выбранного не подключаемся ни к какому. Подключает окно — разрешение на VPN
     * спрашивает оно.
     */
    suspend fun connectOnLaunch(): Boolean {
        if (!settings.connectOnLaunch.first()) {
            return false
        }

        val id = settings.selectedServerId.first() ?: return false
        val server = servers.servers.first().firstOrNull { it.id == id } ?: return false
        return monitor.status.value.state == TunnelState.Off && ServerFacts.coreName(server.protocol) != null
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

    /**
     * Удалить сервер; защита идет через него — VPN отключается, другой сервер пользователь выберет сам (доска 18). Когда
     * сервер удален, — «Сервер удален» (доска 11, как DeleteAsync десктопа).
     */
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

            notices.emit(Message(R.string.card_deleted))
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
     * «Обновить серверы»: задержка всех серверов через ядро — только без защиты (решение 430). Итог — текстом десктопа
     * («Серверы обновлены · пинг проверен»: у него эта кнопка тоже перемеряет пинг). Замер уже идет — нажатие не считается;
     * защита включена или включилась во время замера — сообщение, почему не мерили.
     */
    fun refreshLatency() {
        viewModelScope.launch {
            when (meter.measure()) {
                MeasureResult.Done -> notices.emit(Message(R.string.server_refreshed))
                MeasureResult.Busy -> notices.emit(Message(R.string.android_servers_ping_busy))
                MeasureResult.Blocked -> notices.emit(Message(R.string.android_servers_ping_blocked))
                MeasureResult.Running -> Unit
            }
        }
    }

    /**
     * Путь запросов приложения сейчас: под защитой — через туннель подключения [IpPath.session]; выключено или ошибка без
     * блока Android и есть сеть — напрямую; в подключении, переподключении и под блоком — никакого: мерить нечего.
     */
    private data class IpPath(val viaTunnel: Boolean, val session: Long?)

    private fun path(status: TunnelStatus, blocked: Boolean, network: List<String>?): IpPath? = when {
        status.state == TunnelState.Connected -> IpPath(viaTunnel = true, session = status.since)
        (status.state == TunnelState.Off || status.state == TunnelState.Error) && !blocked && network != null -> IpPath(viaTunnel = false, session = null)
        else -> null
    }

    private fun currentPath(): IpPath? = path(monitor.status.value, monitor.blocked.value, HomeNetwork.identity(networks.networks.value))

    /** Другая сеть — прежний реальный IP не тот; новый путь (или прямой путь в новой сети) — замер. */
    private fun follow(path: IpPath?, network: List<String>?) {
        val moved = network != lastNetwork
        if (moved) {
            lastNetwork = network
            webIp.update { it.copy(direct = null) }
        }

        if (path != null && (path != lastPath || moved && !path.viaTunnel)) {
            measure(path, retry = path.viaTunnel)
        }

        lastPath = path
    }

    /**
     * «Проверить» на главной: внешний IP снова — под защитой это проверка выхода (совпал с реальным — утечка), время
     * проверки — в «Проверено …». Замер уже идет — нажатие не считается.
     */
    fun verify() {
        val path = currentPath() ?: return
        if (!webIp.value.measuring) {
            measure(path, retry = false)
        }
    }

    /**
     * Замер внешнего IP тем путем, что сейчас ([path]). Ответ засчитывается, только если путь за время замера не сменился:
     * иначе запрос мог уйти другим путем, и адрес выхода оказался бы реальным (или наоборот). Сразу после подключения ядро
     * может еще не ответить — [retry]: вторая попытка через 2 с. Новый замер отменяет итог прежнего.
     */
    private fun measure(path: IpPath, retry: Boolean) {
        val attempt = ++ipAttempt
        webIp.update {
            if (path.viaTunnel && it.session != path.session) {
                it.copy(exit = null, session = path.session, checkedAt = null, measuring = true)
            } else {
                it.copy(measuring = true)
            }
        }

        viewModelScope.launch {
            var address = PublicAddress.traceIp(web.get(PublicAddress.TRACE))
            if (address == null && retry && attempt == ipAttempt && currentPath() == path) {
                delay(EXIT_RETRY_MS)
                if (attempt == ipAttempt && currentPath() == path) {
                    address = PublicAddress.traceIp(web.get(PublicAddress.TRACE))
                }
            }

            if (attempt != ipAttempt) {
                return@launch
            }

            val same = currentPath() == path
            webIp.update {
                when {
                    !same -> it.copy(measuring = false)
                    path.viaTunnel -> it.copy(exit = address ?: it.exit, checkedAt = System.currentTimeMillis(), measuring = false)
                    else -> it.copy(direct = address ?: it.direct, measuring = false)
                }
            }
        }
    }

    /** Ссылка сервера для «Поделиться» — только по нажатию; ключа нет — null. */
    suspend fun shareLink(id: String): String? = servers.shareLink(id)

    /** Адрес сервера для карточки («адрес:порт»): открывается из запечатанной ссылки только по просьбе карточки. */
    suspend fun address(id: String): String? = servers.address(id)

    /** Текст поля ссылки изменился: прежний разбор и ответ подписки, который еще идет, не нужны. */
    fun edit(text: String) {
        if (text != importState.value.text) {
            ++previews
            importState.update { ImportUi(text = text, name = it.name, source = it.source) }
        }
    }

    private var previews = 0

    fun name(name: String) {
        importState.update { it.copy(name = name) }
    }

    /**
     * Выбран источник — карточка «Добавить профиль», как у десктопа: прежний предпросмотр и ответы, которые еще идут, не
     * нужны; название профиля — заново (у файла — его имя, у подписки — Profile-Title). Текст поля ссылки остается.
     */
    fun selectSource(chosen: ImportSource) {
        ++previews
        importState.update { ImportUi(text = it.text, source = chosen) }
    }

    /**
     * «Из буфера обмена»: текст разбирается как введенный, но в поле ссылки не попадает — в нем мог быть свой текст. Текста
     * нет — так и сказать.
     */
    fun fromClipboard(text: String?) {
        selectSource(ImportSource.Clipboard)
        if (text.isNullOrBlank()) {
            importState.update { it.copy(problem = Message(R.string.import_empty_clipboard)) }
        } else {
            analyze(text, suggested = null)
        }
    }

    /**
     * «Открыть файл»: текст файла — тем же разбором, название профиля — имя файла без расширения (как у десктопа). Не
     * прочитан — «Файл не прочитан: имя». Ответ на прежний файл, пришедший после нового выбора, отбрасывается.
     */
    fun previewFile(file: Uri) {
        val attempt = ++previews
        importState.update { ImportUi(text = it.text, source = ImportSource.File) }
        viewModelScope.launch {
            val read = files.read(file)
            if (attempt != previews) {
                return@launch
            }

            when (read) {
                is FileRead.Found -> analyze(read.text, suggested = read.name?.let { ImportFile.profileName(it) })
                is FileRead.Failed -> importState.update { it.copy(problem = Message(R.string.import_file_failed, read.name ?: "—")) }
            }
        }
    }

    /** «Проверить» у поля ссылки и код с камеры: текст разбирается, название профиля — прежнее (у подписки — Profile-Title). */
    fun preview(text: String) = analyze(text, suggested = null)

    /**
     * Разобрать текст: ссылки, base64, sing-box или Clash — тем же разбором, что у десктопа (shared/testdata); сразу же
     * сосчитать дубли и у скольких сохраненных обновится защита — до добавления, как ImportPreview десктопа. Одна строка с
     * адресом http(s) — подписка: сначала загрузить, потом разобрать ответ; название профиля — из Profile-Title или сервер
     * подписки. [suggested] — название по источнику (имя файла). Ответ на прежнюю проверку, пришедший после новой,
     * отбрасывается.
     */
    private fun analyze(text: String, suggested: String?) {
        val attempt = ++previews
        importState.update { ImportUi(it.text, name = it.name, source = it.source) }
        val url = text.trim().takeIf { !it.contains('\n') }?.let { SubscriptionAddress.parse(it) }
        viewModelScope.launch {
            if (url == null) {
                val batch = withContext(Dispatchers.Default) { ImportParser.parse(text) }
                val count = servers.count(batch.lines.mapNotNull { it.result.server })
                if (attempt == previews) {
                    importState.update {
                        ImportUi(
                            it.text,
                            batch,
                            duplicates = count.duplicates,
                            renewals = count.renewals,
                            name = suggested ?: it.name,
                            source = it.source,
                        )
                    }
                }

                return@launch
            }

            importState.update { ImportUi(it.text, downloading = true, name = it.name, source = it.source) }
            val downloaded = withContext(Dispatchers.IO) {
                try {
                    Result.success(source.download(url, onlyViaVpn = false))
                } catch (e: CancellationException) {
                    throw e
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
                    ImportUi(
                        it.text,
                        failure = FailureReason.of(error?.failure ?: DownloadFailure.Network, error?.status),
                        name = it.name,
                        source = it.source,
                    )
                }

                return@launch
            }

            val batch = withContext(Dispatchers.Default) { ImportParser.parse(content.text) }
            val count = servers.count(batch.lines.mapNotNull { it.result.server })
            val existing = servers.subscriptionFor(url)?.let { id -> servers.groups.first().firstOrNull { it.id == id }?.name }
            if (attempt != previews) {
                return@launch
            }
            importState.update {
                ImportUi(
                    it.text,
                    batch,
                    duplicates = count.duplicates,
                    url = url,
                    insecure = SubscriptionAddress.insecure(url),
                    existing = existing,
                    name = it.name.ifBlank { SubscriptionFetch.suggestName(content.title, url) },
                    source = it.source,
                    renewals = count.renewals,
                )
            }
        }
    }

    /**
     * QR-код с картинки: найденный текст разбирается так же, как введенный (ссылки, подписка по адресу), но в поле не
     * попадает — в нем мог быть свой текст. Ответ на прежнюю картинку, пришедший после новой, отбрасывается.
     */
    fun previewQr(image: Uri) {
        val attempt = ++previews
        importState.update { ImportUi(it.text, name = it.name, qrReading = true, source = ImportSource.Qr) }
        viewModelScope.launch {
            val read = qr.read(image)
            if (attempt != previews) {
                return@launch
            }

            when (read) {
                is QrRead.Found -> preview(read.text)
                QrRead.None -> importState.update { it.copy(qrReading = false, qrProblem = R.string.android_import_qr_none) }
                QrRead.Failed -> importState.update { it.copy(qrReading = false, qrProblem = R.string.android_import_qr_failed) }
            }
        }
    }

    /**
     * Камера для QR не открылась или к ней нет доступа: сказать под карточками. Текст с камеры разбирает [preview] — как
     * введенный, а ответ картинки, выбранной раньше, отбрасывается.
     */
    fun cameraProblem(@StringRes text: Int) {
        ++previews
        importState.update { ImportUi(text = it.text, name = it.name, qrProblem = text, source = it.source) }
    }

    /**
     * Добавить разобранные серверы; ничего не было выбрано — выбрать первый новый, иначе выбор не меняется. Повторное
     * нажатие, пока идет добавление, не считается: второе добавление нашло бы одни дубли и затерло итог первого. Итог —
     * в [ImportUi.added]: окно по нему показывает подсказку и ведет на «Серверы».
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
            // Та же подписка уже есть — обновить ее, как «Обновить»: новые — в нее, пропавшие из подписки — удалить (решение 528).
            val existing = current.url?.let { servers.subscriptionFor(it) }
            if (existing != null) {
                val update = servers.applyUpdate(existing, parsed, settings.selectedServerId.first())
                val group = servers.groups.first().firstOrNull { it.id == existing }?.name ?: current.existing
                importState.update {
                    if (it.batch === batch) {
                        it.copy(adding = false, added = update.added, duplicates = parsed.size - update.added, removed = update.removed, existing = group)
                    } else {
                        it
                    }
                }
                return@launch
            }

            // С названием — свой профиль (подписка, если загружено по адресу); без названия — «Мои серверы».
            val result = servers.add(parsed, current.name, current.url)
            if (settings.selectedServerId.first() == null) {
                servers.servers.first().firstOrNull { it.id !in before }?.let { settings.select(it.id) }
            }

            importState.update {
                if (it.batch === batch) it.copy(adding = false, added = result.added, duplicates = result.duplicates, renewals = result.renewed) else it
            }
        }
    }

    fun closeImport() {
        ++previews
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

    private companion object {
        /** Скорость туннеля — раз в секунду, как у десктопа. */
        const val SPEED_TICK_MS = 1_000L

        /** Окно на 5 с ушло (поворот экрана) — счетчики еще читаются: не начинать заново с «—». */
        const val SPEED_KEEP_MS = 5_000L

        const val EXIT_RETRY_MS = 2_000L
    }
}

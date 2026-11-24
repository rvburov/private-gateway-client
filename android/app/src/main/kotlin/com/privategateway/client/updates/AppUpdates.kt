package com.privategateway.client.updates

import android.content.Context
import android.content.Intent
import android.content.pm.PackageInstaller
import android.net.Uri
import android.provider.Settings
import androidx.annotation.VisibleForTesting
import com.privategateway.client.BuildConfig
import com.privategateway.client.account.UpdatesFeedSource
import com.privategateway.client.core.updates.ApkDownload
import com.privategateway.client.core.updates.DownloadResult
import com.privategateway.client.core.updates.SemVer
import com.privategateway.client.core.updates.UpdateCheck
import com.privategateway.client.core.updates.UpdateResult
import com.privategateway.client.data.SettingsRepository
import com.privategateway.client.data.UpdateInstallation
import com.privategateway.client.data.UpdateMemo
import com.privategateway.client.vpn.TunnelJournal
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineExceptionHandler
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import java.io.File
import java.io.IOException
import java.util.concurrent.atomic.AtomicBoolean
import javax.inject.Inject
import javax.inject.Singleton
import kotlin.random.Random

/** Итог последней проверки обновления для карточки «Обновления». */
enum class UpdateStatus {
    /** В приложении нет ключей подписи обновлений — проверять нечем. */
    NotSigned,

    /** Входа нет: ленту обновлений сайт дает только по аккаунту устройства. */
    SignedOut,

    UpToDate,

    Available,

    /** Проверить не удалось (сеть, сайт) — подробности в журнале «Проверки». */
    Failed,
}

/** Шаг установки найденного обновления: «Скачать и установить» → загрузка → установщик Android. */
enum class InstallStep {
    None,

    /** APK качается: ход — [UpdatesView.received] из [UpdatesView.total]. */
    Downloading,

    /** APK сверен и передан установщику Android: ждем подтверждения пользователя в окне Android. */
    Installing,

    /** Нет разрешения «Установка неизвестных приложений»: открыта настройка Android. */
    Allow,

    /** Скачанный файл не сошелся с подписанным манифестом и удален. */
    BadFile,

    /** Не скачалось или Android не поставил. */
    Failed,
}

/**
 * «Обновления» в «О программе»: идет ли проверка, итог последней ([status]; null — еще не проверяли), когда он получен (мс
 * эпохи), найденная версия, главное из ее заметок и страница скачивания, шаг установки и ход загрузки (байты). Ссылки-билета
 * здесь нет: она остается в [AppUpdates] и в окно не попадает.
 */
data class UpdatesView(
    val checking: Boolean = false,
    val status: UpdateStatus? = null,
    val checkedAt: Long? = null,
    val version: String? = null,
    val highlights: List<String> = emptyList(),
    val notes: String? = null,
    val step: InstallStep = InstallStep.None,
    val received: Long = 0,
    val total: Long = 0,
)

/**
 * Обновление приложения Android с сайта Private Gateway Web (0.3.0). Проверка — UpdateCheck по ленте сайта токеном входа
 * ([UpdatesFeedSource]) и ключами подписи из APK (assets/update-keys, их кладет сборка из shared/update/keys). Кнопка
 * «Проверить» — сразу и без учета частичной раскатки. Показ окна и открытие «О программе» ([windowShown]) — не чаще раза в 12
 * часов после удачного ответа сайта (его итог помнят настройки: после перезапуска карточка показывает его без запроса), после
 * неудачи — не раньше чем через 30 минут, с частичной раскаткой по номеру установки. «Скачать и установить»: нет разрешения
 * «Установка неизвестных приложений» — настройка Android ([prompts]); иначе свежая лента (билет живет 30 минут, а раскатку
 * могли остановить), APK в cacheDir с ходом, сверка размера и SHA-256 с подписанным манифестом (не сошлось — файла нет),
 * сессия [ApkInstaller] и окно подтверждения Android ([prompts]). Работа — в своей области: закрытое окно загрузку не
 * обрывает. В журнал «Проверки» — итоги без ссылок.
 */
@Singleton
class AppUpdates @Inject constructor(
    @param:ApplicationContext private val context: Context,
    private val feeds: UpdatesFeedSource,
    private val settings: SettingsRepository,
    private val installer: ApkInstaller,
    private val journal: TunnelJournal,
    private val alerts: UpdateAlerts = UpdateAlerts.NONE,
) {
    // Ошибка, которую не поймали ниже, — в журнал, а не в обработчик потока: тот завершил бы процесс вместе со службой VPN.
    private val scope = CoroutineScope(
        SupervisorJob() + Dispatchers.Default + CoroutineExceptionHandler { _, failure ->
            journal.error("update  unexpected " + failure.javaClass.simpleName)
        },
    )
    private val lock = Mutex()
    private val view = MutableStateFlow(UpdatesView())
    private val prompt = MutableStateFlow<Intent?>(null)

    /** Сессия установки, итога которой ждем; итоги других (брошенных) сессий не считаются. */
    @Volatile
    private var session: UpdateInstallation? = null

    /** Повтор того же callback не открывает второе окно в этом процессе. После холодного запуска нужен новый Intent Android. */
    private var confirmationSession: Int? = null
    private val continuing = AtomicBoolean(false)

    /** Когда в этом процессе проверка при показе окна или по кнопке последний раз спрашивала сайт. */
    @Volatile
    private var askedAt: Long? = null

    /** Прошлый итог из настроек показан (один раз за процесс). */
    @Volatile
    private var restored = false

    /** Часы, версия, ключи, начало ссылок-билетов, разрешение и загрузка — для тестов. */
    @VisibleForTesting
    internal var clock: () -> Long = System::currentTimeMillis

    @VisibleForTesting
    internal var installed: String = BuildConfig.VERSION_NAME

    @VisibleForTesting
    internal var keys: () -> List<String> = ::assetKeys

    @VisibleForTesting
    internal var files: String = UpdateCheck.FILES

    @VisibleForTesting
    internal var canInstall: () -> Boolean = ::installAllowed

    @VisibleForTesting
    internal var download: ApkDownload = ApkDownload(userAgent = "Private-Gateway-Client/" + BuildConfig.VERSION_NAME)

    val state: StateFlow<UpdatesView> = view.asStateFlow()

    /**
     * Системный экран, который окну открыть за пользователя, когда оно на экране: настройка «Установка неизвестных приложений»
     * или подтверждение установки (из фона Android их не покажет). Открыло — [prompted], не нашлось — [promptFailed].
     */
    val prompts: StateFlow<Intent?> = prompt.asStateFlow()

    fun prompted(shown: Intent) {
        prompt.compareAndSet(shown, null)
    }

    /** Экрана Android нет (редкая прошивка) — поставить отсюда нельзя: «не удалось», запасной путь — сайт. */
    fun promptFailed() {
        journal.warning("update install  system screen not found")
        view.update { it.copy(step = InstallStep.Failed) }
    }

    /** Окно показано или открыта «О программе»: прошлый итог после перезапуска и проверка, если пора. Работа — [Job]. */
    fun windowShown(): Job = scope.launch {
        lock.withLock {
            val memo = try {
                settings.updateMemo.first()
            } catch (e: IOException) {
                journal.warning("update  memo unreadable " + e.javaClass.simpleName)
                UpdateMemo()
            }

            if (!restored) {
                restored = true
                restore(memo)
                restoreInstallation()
                // Недокачанный файл прошлого запуска больше не нужен.
                File(context.cacheDir, FOLDER).listFiles()?.forEach { it.delete() }
            }

            if (session == null && view.value.step != InstallStep.Failed && due(memo)) {
                ask(auto = true)
            }
        }
    }

    /**
     * Проверка в фоне ([UpdateWork], окно не открывали): как при показе окна — только когда пора и с частичной раскаткой.
     * Найденное новее установленного — уведомление ([alerts], о каждой версии один раз). Идет загрузка или установка — пропуск.
     */
    suspend fun backgroundCheck() {
        lock.withLock {
            if (session != null || view.value.step == InstallStep.Downloading) {
                return
            }

            val memo = try {
                settings.updateMemo.first()
            } catch (e: IOException) {
                journal.warning("update  memo unreadable " + e.javaClass.simpleName)
                UpdateMemo()
            }

            if (due(memo)) {
                ask(auto = true)
            }
        }

        val found = try {
            settings.updateMemo.first().found
        } catch (_: IOException) {
            null
        } ?: return
        val newer = SemVer.parse(found)
        val current = SemVer.parse(installed)
        if (newer != null && current != null && newer > current) {
            alerts.available(found)
        }
    }

    /** «Проверить»: сразу, мимо 12 часов и без частичной раскатки — попросил сам пользователь. Уже идет — null. */
    fun check(): Job? {
        val current = view.value
        if (current.checking || current.step == InstallStep.Downloading || current.step == InstallStep.Installing) {
            return null
        }

        view.update { it.copy(checking = true) }
        return scope.launch { lock.withLock { ask(auto = false) } }
    }

    /** «Скачать и установить» найденное обновление. Уже идет или нет разрешения (открыта настройка) — null. */
    fun install(): Job? {
        val current = view.value
        if (current.checking || current.step == InstallStep.Downloading) {
            return null
        }

        if (current.step == InstallStep.Installing || session != null) {
            if (!continuing.compareAndSet(false, true)) return null
            return scope.launch {
                try {
                    lock.withLock { continueInstallation() }
                } finally {
                    continuing.set(false)
                }
            }
        }

        if (!canInstall()) {
            // Разрешение «Установка неизвестных приложений» дает только пользователь: открыть ему эту настройку.
            journal.info("update install  permission needed")
            view.update { it.copy(step = InstallStep.Allow) }
            prompt.value = Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.fromParts("package", context.packageName, null))
            return null
        }

        view.update { it.copy(step = InstallStep.Downloading, received = 0, total = 0) }
        return scope.launch { lock.withLock { fetchAndInstall() } }
    }

    /** APK уже у Android: явное продолжение запрашивает новое подтверждение, не бросает сессию и не качает файл снова. */
    private suspend fun continueInstallation() {
        try {
            val waiting = session ?: settings.updateInstallation.first() ?: run {
                journal.warning("update install  no session to continue")
                view.update { it.copy(step = InstallStep.Failed) }
                return
            }
            if (alreadyInstalled(waiting)) {
                finishInstallation()
                view.update { it.copy(status = UpdateStatus.UpToDate, version = null, step = InstallStep.None) }
            } else if (waiting.committed && installer.owns(waiting.id)) {
                session = waiting
                confirmationSession = null
                prompt.value = null
                view.update { it.copy(step = InstallStep.Installing) }
                withContext(Dispatchers.IO) { installer.resume(waiting.id) }
                journal.info("update install  confirmation requested")
            } else {
                finishInstallation()
                journal.warning("update install  session no longer available")
                view.update { it.copy(step = InstallStep.Failed) }
            }
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            journal.warning("update install  cannot continue " + e.javaClass.simpleName)
            view.update { it.copy(step = InstallStep.Failed) }
        }
    }

    /** Итог сессии установки от Android ([UpdateInstallReceiver]); итог другой, брошенной сессии — ничего. */
    fun installStatus(id: Int, status: Int, confirm: Intent?, message: String?): Job = scope.launch {
        try {
            // Повторная загрузка уже отменяет прежнюю установку. Ее приемник не должен ждать сетевой mutex и
            // удерживать goAsync до таймаута Android. При холодном запуске сначала читаем сохраненную сессию.
            if (view.value.step == InstallStep.Downloading) return@launch
            val observed = session ?: settings.updateInstallation.first() ?: return@launch
            if (observed.id != id) return@launch
            lock.withLock {
                val waiting = session ?: settings.updateInstallation.first() ?: return@withLock
                if (id != waiting.id) {
                    return@withLock
                }

                if (alreadyInstalled(waiting)) {
                    finishInstallation()
                    view.update { it.copy(status = UpdateStatus.UpToDate, version = null, step = InstallStep.None) }
                    return@withLock
                }

                if (status == PackageInstaller.STATUS_PENDING_USER_ACTION && confirm != null && installer.owns(id)) {
                    // Callback Android доказывает commit даже при смерти процесса между commit и записью фазы.
                    markCommitted(waiting)
                    view.update { it.copy(status = UpdateStatus.Available, version = waiting.version, step = InstallStep.Installing) }
                    if (confirmationSession != id) {
                        confirmationSession = id
                        prompt.value = confirm
                    }
                    return@withLock
                }

                finishInstallation()
                view.update { it.copy(status = UpdateStatus.Available, version = waiting.version) }
                when (status) {
                    PackageInstaller.STATUS_SUCCESS -> {
                        journal.info("update install  done")
                        view.update { it.copy(status = UpdateStatus.UpToDate, version = null, step = InstallStep.None) }
                    }

                    PackageInstaller.STATUS_FAILURE_ABORTED -> {
                        // Отказ в окне Android — не ошибка: карточка снова предлагает обновление.
                        journal.info("update install  cancelled")
                        view.update { it.copy(step = InstallStep.None) }
                    }

                    else -> {
                        journal.warning("update install  failed $status " + message.orEmpty().take(MESSAGE_MAX))
                        view.update { it.copy(step = InstallStep.Failed) }
                    }
                }
            }
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            journal.warning("update install  status unavailable " + e.javaClass.simpleName)
            view.update { it.copy(step = InstallStep.Failed) }
        }
    }

    /** Собственная живая сессия переживает процесс; пропавшая требует повторной попытки, если версия еще не стоит. */
    private suspend fun restoreInstallation() {
        try {
            val waiting = settings.updateInstallation.first() ?: return
            when {
                alreadyInstalled(waiting) -> {
                    finishInstallation()
                    view.update { it.copy(status = UpdateStatus.UpToDate, version = null, step = InstallStep.None) }
                }
                installer.owns(waiting.id) -> {
                    if (waiting.committed) {
                        val needsResume = session?.id != waiting.id
                        session = waiting
                        view.update { it.copy(status = UpdateStatus.Available, version = waiting.version, step = InstallStep.Installing) }
                        // Повтор commit у запечатанной сессии меняет приемник результата, не ее APK. Android заново
                        // присылает подтверждение: Intent прошлого процесса хранить и воспроизводить не требуется.
                        if (needsResume) {
                            withContext(Dispatchers.IO) { installer.resume(waiting.id) }
                        }
                    } else {
                        installer.abandon(waiting.id)
                        finishInstallation()
                        journal.warning("update install  previous session was not committed")
                        view.update { it.copy(status = UpdateStatus.Available, version = waiting.version, step = InstallStep.Failed) }
                    }
                }
                else -> {
                    finishInstallation()
                    journal.warning("update install  previous session no longer exists")
                    view.update { it.copy(status = UpdateStatus.Available, version = waiting.version, step = InstallStep.Failed) }
                }
            }
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            journal.warning("update install  session unreadable " + e.javaClass.simpleName)
            view.update { it.copy(status = if (it.version == null) UpdateStatus.Failed else it.status, step = InstallStep.Failed) }
        }
    }

    private fun alreadyInstalled(waiting: UpdateInstallation): Boolean {
        val target = SemVer.parse(waiting.version) ?: return false
        return SemVer.parse(installed)?.let { it >= target } == true
    }

    private suspend fun finishInstallation() {
        session = null
        confirmationSession = null
        prompt.value = null
        settings.setUpdateInstallation(null)
    }

    private suspend fun markCommitted(waiting: UpdateInstallation) {
        val committed = waiting.copy(committed = true)
        session = committed
        try {
            settings.setUpdateInstallation(committed)
        } catch (e: IOException) {
            journal.warning("update install  committed phase not saved " + e.javaClass.simpleName)
        }
    }

    /** Остановить работу (тесты). */
    @VisibleForTesting
    internal fun close() = scope.cancel()

    /** Прошлый удачный итог из настроек — если в этом процессе еще ничего не известно и найденное еще не стоит. */
    private fun restore(memo: UpdateMemo) {
        val at = memo.checkedAt ?: return
        val current = SemVer.parse(installed) ?: return
        val found = memo.found?.let { SemVer.parse(it) }
        view.update { shown ->
            when {
                shown.status != null -> shown
                memo.found == null -> shown.copy(status = UpdateStatus.UpToDate, checkedAt = at)
                found != null && found > current -> shown.copy(status = UpdateStatus.Available, checkedAt = at, version = memo.found)
                else -> shown
            }
        }
    }

    /**
     * Пора ли проверить при показе окна: после неудачи — не раньше [RETRY_MS]; после удачного ответа — через [PERIOD_MS]
     * (часы перевели назад — сразу); найденное тогда уже стоит (приложение обновилось) — сразу.
     */
    private fun due(memo: UpdateMemo): Boolean {
        val now = clock()
        // Подтвержденный отзыв входа важнее прежнего успешного кеша, даже если его очистка не записалась.
        if (view.value.status == UpdateStatus.SignedOut) {
            return true
        }
        askedAt?.let { asked ->
            if (now - asked in 0L until RETRY_MS) {
                return false
            }
        }

        val at = memo.checkedAt ?: return true
        val found = memo.found?.let { SemVer.parse(it) }
        val current = SemVer.parse(installed)
        if (found != null && current != null && found <= current) {
            return true
        }

        return now - at !in 0L until PERIOD_MS
    }

    /** Проверка: сама ([auto]) — с частичной раскаткой. После отзыва доступа показывается вход, без успешного кеша. */
    private suspend fun ask(auto: Boolean) {
        view.update { it.copy(checking = true) }
        try {
            val bucket = if (auto) bucket() else null
            val result = UpdateCheck.check(installed, keys(), feeds::updates, files, bucket, settings::rememberUpdatePolicy, ::rejected)
            val now = clock()
            journal.info("update check  " + (if (auto) "auto " else "") + name(result))
            if (result != UpdateResult.NotSigned && result != UpdateResult.SignedOut) {
                askedAt = now
            } else if (result == UpdateResult.SignedOut) {
                askedAt = null
            }

            memorize(result, now)
            view.update { current ->
                current.withResult(result, now).copy(step = InstallStep.None)
            }
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            askedAt = clock()
            clearMemo()
            journal.warning("update check  failed " + e.javaClass.simpleName)
            view.update { it.copy(status = UpdateStatus.Failed, checkedAt = clock(), step = InstallStep.None) }
        } finally {
            view.update { it.copy(checking = false) }
        }
    }

    /**
     * Свежая лента, загрузка APK со сверкой и передача установщику Android. Билет истек (сайт ответил ticket_expired) — лента
     * еще раз, с новым билетом, и одна повторная загрузка.
     */
    private suspend fun fetchAndInstall() {
        try {
            // Повторная попытка относится к новой сессии: старое подтверждение больше не должно открыться во время загрузки.
            (session ?: settings.updateInstallation.first())?.let { installer.abandon(it.id) }
            finishInstallation()
            for (attempt in 1..TICKET_TRIES) {
                // Билет на файл живет 30 минут, а раскатку могли остановить: перед загрузкой — лента заново.
                val fresh = UpdateCheck.check(installed, keys(), feeds::updates, files, remember = settings::rememberUpdatePolicy, report = ::rejected)
                val now = clock()
                memorize(fresh, now)
                if (fresh !is UpdateResult.Available) {
                    journal.info("update install  nothing to install: " + name(fresh))
                    // Не спросилось — «не удалось», предложение остается; новее нет или входа нет — так и показать.
                    view.update {
                        if (fresh == UpdateResult.Failed) it.copy(step = InstallStep.Failed) else it.withResult(fresh, now).copy(step = InstallStep.None)
                    }
                    return
                }

                view.update { it.withResult(fresh, now).copy(step = InstallStep.Downloading, received = 0, total = fresh.size) }
                val file = File(File(context.cacheDir, FOLDER), FILE)
                val loaded = withContext(Dispatchers.IO) {
                    download.fetch(fresh.url, file, fresh.sha256, fresh.size) { received, total ->
                        view.update { it.copy(received = received, total = total) }
                    }
                }

                when (loaded) {
                    DownloadResult.Done -> hand(fresh, file)
                    DownloadResult.Expired -> {
                        journal.warning("update download  ticket expired")
                        if (attempt < TICKET_TRIES) {
                            continue
                        }

                        view.update { it.copy(step = InstallStep.Failed) }
                    }

                    is DownloadResult.BadFile -> {
                        journal.warning("update download  file does not match the manifest: " + loaded.reason)
                        view.update { it.copy(step = InstallStep.BadFile) }
                    }

                    is DownloadResult.Failed -> {
                        journal.warning("update download  failed: " + loaded.reason)
                        view.update { it.copy(step = InstallStep.Failed) }
                    }
                }

                return
            }
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            clearMemo()
            journal.warning("update install  failed " + e.javaClass.simpleName)
            view.update { it.copy(step = InstallStep.Failed) }
        }
    }

    /** Сверенный APK — в сессию установщика Android; копия в cacheDir после этого не нужна. */
    private suspend fun hand(offer: UpdateResult.Available, file: File) {
        view.update { it.copy(step = InstallStep.Installing, received = offer.size, total = offer.size) }
        try {
            withContext(Dispatchers.IO) {
                val id = installer.open(offer.size)
                try {
                    val waiting = UpdateInstallation(id, offer.version)
                    settings.setUpdateInstallation(waiting)
                    session = waiting
                    installer.commit(id, file)
                    markCommitted(waiting)
                } catch (e: CancellationException) {
                    // Процесс мог завершиться после commit: сохраненная сессия будет сверена при следующем запуске.
                    throw e
                } catch (e: Exception) {
                    installer.abandon(id)
                    throw e
                }
            }
            journal.info("update install  " + offer.version + " handed to Android")
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            try {
                finishInstallation()
            } catch (_: IOException) {
                // Не записалось — при следующем запуске запись сверится с собственными сессиями Android.
            }
            journal.warning("update install  failed " + e.javaClass.simpleName)
            view.update { it.copy(step = InstallStep.Failed) }
        } finally {
            file.delete()
        }
    }

    /** Удачный ответ сайта — в настройки: после перезапуска его покажет карточка, а проверка при показе окна подождет. */
    private suspend fun memorize(result: UpdateResult, at: Long) {
        if (result == UpdateResult.Failed || result == UpdateResult.SignedOut) {
            clearMemo()
            return
        }
        val found = when (result) {
            is UpdateResult.Available -> result.version
            UpdateResult.UpToDate -> null
            else -> return
        }

        try {
            settings.setUpdateMemo(at, found)
        } catch (e: IOException) {
            journal.warning("update  memo not saved " + e.javaClass.simpleName)
        }
    }

    private suspend fun clearMemo() {
        try {
            settings.clearUpdateMemo()
        } catch (e: IOException) {
            journal.warning("update  memo not cleared " + e.javaClass.simpleName)
        }
    }

    /** Номер установки для раскатки; настройки не пишутся — случайный на эту проверку (доля раскатки соблюдается в среднем). */
    private suspend fun bucket(): Int = try {
        settings.updateBucket()
    } catch (e: IOException) {
        journal.warning("update  install number unavailable " + e.javaClass.simpleName)
        Random.nextInt(100)
    }

    /** Только код причины; манифест, подпись, ссылка-билет и ответы сайта в журнал не попадают. */
    private fun rejected(reason: String) = journal.warning("update check  $reason")

    private fun installAllowed(): Boolean = try {
        context.packageManager.canRequestPackageInstalls()
    } catch (_: SecurityException) {
        false
    }

    private fun assetKeys(): List<String> {
        val assets = context.assets
        return try {
            assets.list(KEYS).orEmpty().filter { it.endsWith(".pem") }.sorted().mapNotNull { name ->
                runCatching { assets.open("$KEYS/$name").bufferedReader().use { it.readText() } }.getOrNull()
            }
        } catch (e: IOException) {
            journal.warning("update  keys unreadable " + e.javaClass.simpleName)
            emptyList()
        }
    }

    private companion object {
        const val KEYS = "update-keys"
        const val FOLDER = "updates"
        const val FILE = "update.apk"

        /** Проверка при показе окна после удачного ответа сайта — не чаще. */
        const val PERIOD_MS = 12 * 60 * 60_000L

        /** После неудачи (нет сети, сайт не ответил) — снова не раньше: окно открывают часто, а сайт и так не отвечал. */
        const val RETRY_MS = 30 * 60_000L

        /** Текст ошибки установщика Android в журнале — не длиннее. */
        const val MESSAGE_MAX = 200

        /** Загрузок по свежему билету: первая и одна повторная, если билет истек. */
        const val TICKET_TRIES = 2

        fun name(result: UpdateResult): String = if (result is UpdateResult.Available) "Available " + result.version else result.javaClass.simpleName

        /** Итог проверки — в вид карточки; найденная версия, заметки и страница — только у «Доступно». */
        fun UpdatesView.withResult(result: UpdateResult, at: Long): UpdatesView {
            val offer = result as? UpdateResult.Available
            val status = when (result) {
                is UpdateResult.Available -> UpdateStatus.Available
                UpdateResult.UpToDate -> UpdateStatus.UpToDate
                UpdateResult.NotSigned -> UpdateStatus.NotSigned
                UpdateResult.SignedOut -> UpdateStatus.SignedOut
                UpdateResult.Failed -> UpdateStatus.Failed
            }
            return copy(status = status, checkedAt = at, version = offer?.version, highlights = offer?.highlights.orEmpty(), notes = offer?.notes)
        }
    }
}

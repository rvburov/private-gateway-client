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
    private var session: Int? = null

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
                // Недокачанный файл прошлого запуска больше не нужен.
                File(context.cacheDir, FOLDER).listFiles()?.forEach { it.delete() }
            }

            if (due(memo)) {
                ask(auto = true)
            }
        }
    }

    /** «Проверить»: сразу, мимо 12 часов и без частичной раскатки — попросил сам пользователь. Уже идет — null. */
    fun check(): Job? {
        val current = view.value
        if (current.checking || current.step == InstallStep.Downloading) {
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

    /** Итог сессии установки от Android ([UpdateInstallReceiver]); итог другой, брошенной сессии — ничего. */
    fun installStatus(id: Int, status: Int, confirm: Intent?, message: String?) {
        if (id != session) {
            return
        }

        if (status == PackageInstaller.STATUS_PENDING_USER_ACTION && confirm != null) {
            prompt.value = confirm
            return
        }

        session = null
        prompt.value = null
        when (status) {
            PackageInstaller.STATUS_SUCCESS -> {
                journal.info("update install  done")
                view.update { it.copy(step = InstallStep.None) }
            }

            PackageInstaller.STATUS_FAILURE_ABORTED -> {
                // Пользователь отказался в окне Android — не ошибка: карточка снова предлагает обновление.
                journal.info("update install  cancelled")
                view.update { it.copy(step = InstallStep.None) }
            }

            else -> {
                journal.warning("update install  failed $status " + message.orEmpty().take(MESSAGE_MAX))
                view.update { it.copy(step = InstallStep.Failed) }
            }
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

    /** Проверка: сама ([auto]) — с частичной раскаткой, «Входа нет» не показывает (вход появится — проверит снова). */
    private suspend fun ask(auto: Boolean) {
        view.update { it.copy(checking = true) }
        try {
            val bucket = if (auto) bucket() else null
            val result = UpdateCheck.check(installed, keys(), feeds::updates, files, bucket)
            val now = clock()
            journal.info("update check  " + (if (auto) "auto " else "") + name(result))
            if (result != UpdateResult.NotSigned && result != UpdateResult.SignedOut) {
                askedAt = now
            }

            memorize(result, now)
            view.update { current ->
                val shown = if (auto && result == UpdateResult.SignedOut) current else current.withResult(result, now)
                shown.copy(step = InstallStep.None)
            }
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
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
            for (attempt in 1..TICKET_TRIES) {
                // Билет на файл живет 30 минут, а раскатку могли остановить: перед загрузкой — лента заново.
                val fresh = UpdateCheck.check(installed, keys(), feeds::updates, files)
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
            journal.warning("update install  failed " + e.javaClass.simpleName)
            view.update { it.copy(step = InstallStep.Failed) }
        }
    }

    /** Сверенный APK — в сессию установщика Android; копия в cacheDir после этого не нужна. */
    private suspend fun hand(offer: UpdateResult.Available, file: File) {
        view.update { it.copy(step = InstallStep.Installing, received = offer.size, total = offer.size) }
        try {
            withContext(Dispatchers.IO) {
                // Прошлая сессия ждала подтверждения (нажали еще раз) — она больше не нужна.
                session?.let(installer::abandon)
                val id = installer.open(offer.size)
                session = id
                installer.commit(id, file)
            }
            journal.info("update install  " + offer.version + " handed to Android")
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            session = null
            journal.warning("update install  failed " + e.javaClass.simpleName)
            view.update { it.copy(step = InstallStep.Failed) }
        } finally {
            file.delete()
        }
    }

    /** Удачный ответ сайта — в настройки: после перезапуска его покажет карточка, а проверка при показе окна подождет. */
    private suspend fun memorize(result: UpdateResult, at: Long) {
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

    /** Номер установки для раскатки; настройки не пишутся — случайный на эту проверку (доля раскатки соблюдается в среднем). */
    private suspend fun bucket(): Int = try {
        settings.updateBucket()
    } catch (e: IOException) {
        journal.warning("update  install number unavailable " + e.javaClass.simpleName)
        Random.nextInt(100)
    }

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

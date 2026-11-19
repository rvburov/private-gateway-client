package com.privategateway.client.account

import android.os.Build
import android.security.keystore.KeyPermanentlyInvalidatedException
import androidx.annotation.VisibleForTesting
import androidx.datastore.core.DataStore
import androidx.datastore.core.handlers.ReplaceFileCorruptionHandler
import androidx.datastore.preferences.core.PreferenceDataStoreFactory
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.emptyPreferences
import androidx.datastore.preferences.core.longPreferencesKey
import androidx.datastore.preferences.core.stringPreferencesKey
import com.privategateway.client.BuildConfig
import com.privategateway.client.core.account.AccessVerdict
import com.privategateway.client.core.account.AccountApi
import com.privategateway.client.core.account.AccountError
import com.privategateway.client.core.account.AccountGate
import com.privategateway.client.core.account.AccountSession
import com.privategateway.client.core.account.AccountSource
import com.privategateway.client.core.account.DeviceInfo
import com.privategateway.client.core.account.LoginResult
import com.privategateway.client.core.account.StatusResult
import com.privategateway.client.core.account.UpdatesFeed
import com.privategateway.client.core.vpn.TunnelErrors
import com.privategateway.client.core.vpn.TunnelState
import com.privategateway.client.core.vpn.TunnelStatus
import com.privategateway.client.data.SecretBox
import com.privategateway.client.data.SettingsRepository
import com.privategateway.client.vpn.TunnelControl
import com.privategateway.client.vpn.TunnelJournal
import com.privategateway.client.vpn.TunnelMonitor
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineExceptionHandler
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.async
import kotlinx.coroutines.cancel
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.onEach
import kotlinx.coroutines.flow.retryWhen
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import java.io.File
import java.io.IOException
import java.time.Duration
import java.time.Instant
import java.util.Base64
import java.util.UUID
import javax.crypto.BadPaddingException
import javax.inject.Inject
import javax.inject.Qualifier
import javax.inject.Singleton

/** Отдельное хранилище DataStore «account»: вход и uid устройства — мимо настроек и их резервной копии (решение 617). */
@Qualifier
@Retention(AnnotationRetention.BINARY)
annotation class AccountStore

/**
 * Хранилище входа. Испорченный файл (сбой записи, чужие байты) — «входа нет»: без замены каждое чтение бросало бы
 * CorruptionException, и войти было бы нельзя никогда (решение 645). uid при этом создается заново.
 */
fun accountDataStore(scope: CoroutineScope, file: () -> File): DataStore<Preferences> =
    PreferenceDataStoreFactory.create(corruptionHandler = ReplaceFileCorruptionHandler { emptyPreferences() }, scope = scope, produceFile = file)

/** Сохраненный вход — без токена (он только запечатанным в хранилище): для «Настроек» и проверки срока. */
data class AccountInfo(
    val email: String,
    val displayName: String,
    val deviceName: String,
    val expiresAt: Instant,
    /** Когда сайт последний раз подтвердил доступ; null — после входа еще не проверяли. */
    val checkedAt: Instant?,
)

/** Почему экран входа показан снова: отзыв, истечение срока или выход, который пока не удалось сохранить. */
enum class AccountNotice {
    Revoked,
    Expired,

    /** Подключение уже запрещено, но оба способа сохранить выход пока отказали. */
    SignOutNotSaved,
}

/** Вход: [info] — сохраненный вход (null — входа нет), [notice] — почему вход нужен снова (до следующего входа). */
data class AccountState(val info: AccountInfo?, val notice: AccountNotice? = null) {
    val signedIn: Boolean get() = info != null
}

/** Снятие ограничений приложения после выхода: команда службе сама по себе не подтверждает остановку. */
enum class AccountNetworkRecovery {
    Idle,
    Pending,
    Failed,
}

/** Сайт для приложения: [AccountApi] в потоке ввода-вывода, с версией приложения в User-Agent. */
class DeviceAccountSource @Inject constructor() : AccountSource {
    private val api = AccountApi(userAgent = "Private-Gateway-Client/" + BuildConfig.VERSION_NAME)

    override suspend fun login(email: String, password: String, code: String?, recovery: String?, device: DeviceInfo): LoginResult =
        withContext(Dispatchers.IO) { api.login(email, password, code, recovery, device) }

    override suspend fun status(token: String): StatusResult = withContext(Dispatchers.IO) { api.status(token) }

    override suspend fun logout(token: String) {
        withContext(Dispatchers.IO) { api.logout(token) }
    }

    override suspend fun updates(token: String, channel: String): UpdatesFeed =
        withContext(Dispatchers.IO) { api.updates(token, channel = channel) }
}

/** Лента обновлений сайта токеном входа — шов для обновлений приложения (в приложении — [AccountRepository], в тестах подделка). */
fun interface UpdatesFeedSource {
    /** Лента для [channel] (stable или beta). */
    suspend fun updates(channel: String): UpdatesFeed
}

/**
 * Вход в аккаунт сайта Private Gateway Web (решения 616–619, 621, 623). Пользоваться защитой можно только с действующим
 * входом: решение о доступе принимает сайт. Токен — только запечатанным [SecretBox] (ключ в Android Keystore) в своем
 * DataStore; без работающего хранилища ключей вход не выполняется. Рядом, не секретом, — срок, почта, имя, имя устройства и
 * время последней проверки; uid устройства создается один раз на установку и при выходе остается.
 *
 * Проверка сайта: при запуске процесса, при открытии окна и раз в 30 минут. Известный срок отслеживается отдельно,
 * независимо от сети. Отзыв — только ответ 401: доступ запрещается, защита выключается, окно показывает вход с
 * «Доступ отозван». Без ответа сайта действует последний подтвержденный срок. В журнал — только итог, без секретов.
 *
 * Доступ запрещается всегда раньше, чем выключается защита: служба, поднимая туннель, перепроверяет вход в конце подключения, и
 * выход, который пришелся на подключение (монитор еще «Выключено» — команда выключить не уходит), туннель не оставит
 * (решение 646). Сбой хранилища и любая ошибка проверки процесс не роняют и проверки по расписанию не останавливают (645).
 */
@Singleton
class AccountRepository @Inject constructor(
    @param:AccountStore private val store: DataStore<Preferences>,
    private val box: SecretBox,
    private val source: AccountSource,
    private val control: TunnelControl,
    private val monitor: TunnelMonitor,
    private val journal: TunnelJournal,
    private val logoutFence: AccountLogoutFence,
    private val settings: SettingsRepository,
) : UpdatesFeedSource {
    // Ошибка, которую не поймали ниже, — в журнал, а не в обработчик потока: тот завершил бы процесс вместе со службой VPN.
    private val scope = CoroutineScope(
        SupervisorJob() + Dispatchers.Default + CoroutineExceptionHandler { _, failure ->
            journal.error("account  unexpected " + failure.javaClass.simpleName)
        },
    )
    private val lock = Mutex()
    private val notice = MutableStateFlow<AccountNotice?>(null)
    private val recovery = MutableStateFlow(AccountNetworkRecovery.Idle)
    val networkRecovery: StateFlow<AccountNetworkRecovery> = recovery
    private var started = false
    private val accessLock = Any()
    private var generation = 0L
    private var cacheVersion = 0L
    private val denied = MutableStateFlow(logoutFence.pending())
    private val expiryWake = Channel<Unit>(Channel.CONFLATED)

    @Volatile
    private var cachedPreferences: Preferences? = null

    @Volatile
    private var cleanupPending = denied.value

    private var logoutAttempt: Long? = null

    private var deniedReason: AccountNotice? = null

    /** Предельная пауза при изменении часов или повторе неудачной очистки; сетевой опрос от нее не зависит. */
    @VisibleForTesting
    internal var expiryPollMs = 60_000L

    /** Часы — для тестов срока. */
    @VisibleForTesting
    internal var clock: () -> Instant = Instant::now

    /** Сколько ждать ответа сайта на выход, пока защита еще идет: дольше выход не держит туннель. */
    @VisibleForTesting
    internal var logoutWaitMs = LOGOUT_WAIT_MS

    @VisibleForTesting
    internal var networkWaitMs = 5_000L

    /** Когда последний раз спрашивали сайт: открытие окна сразу после запуска не спрашивает второй раз. */
    @Volatile
    private var lastCheck: Instant? = null

    /** Засеянный для проверок на устройстве вход сайт не проверяет: настоящий сайт отозвал бы поддельный токен. */
    @Volatile
    private var seeded = false

    /** Хранилище хоть раз прочитано (или первое чтение не удалось и показан «входа нет»). */
    @Volatile
    private var readOnce = false

    /**
     * Хранилище для [state]. Сбой чтения — в журнал и повтор с нарастающей паузой: окно держит последнее прочитанное, а если
     * не прочитано ничего — экран входа (иначе пустое окно навсегда). Испорченный файл заменяет обработчик в AppModule.
     */
    private val preferences: Flow<Preferences> = store.data
        .onEach {
            readOnce = true
            // Flow может доставить прежний снимок уже после завершения новой записи. Он не должен вернуть старый срок.
            synchronized(accessLock) { if (cachedPreferences == null) cachedPreferences = it }
            expiryWake.trySend(Unit)
        }
        .retryWhen { failure, attempt ->
            if (failure !is IOException) {
                return@retryWhen false
            }

            journal.warning("account storage read failed  " + failure.javaClass.simpleName)
            if (!readOnce) {
                readOnce = true
                emit(emptyPreferences())
            }

            delay(READ_RETRY_MS shl attempt.coerceAtMost(READ_RETRY_STEPS).toInt())
            true
        }

    /** Вход; null — хранилище еще не прочитано (экран входа не мелькает у тех, кто вошел). */
    val state: StateFlow<AccountState?> = combine(preferences, notice, denied) { prefs, why, blocked ->
        AccountState(if (blocked) null else info(prefs), why)
    }
        .stateIn(scope, SharingStarted.Eagerly, null)

    /**
     * Можно ли подключаться: вход есть и срок не прошел. Читает само хранилище, а не [state]: тот обновляется позже записи, а
     * выход или отзыв, записанный только что, служба должна увидеть сразу. Хранилище не прочиталось — по последнему известному.
     */
    suspend fun allowed(): Boolean {
        if (denied.value) return false
        val prefs = read()
        val info = if (prefs != null) info(prefs) else cachedPreferences?.let(::info)
        return !denied.value && info != null && AccountGate.allowed(info.expiresAt, clock())
    }

    /** То же без ожидания — для плитки в шторке: хранилище еще не прочитано — нет. */
    fun allowedNow(): Boolean {
        if (denied.value) return false
        val info = cachedPreferences?.let(::info) ?: return false
        return AccountGate.allowed(info.expiresAt, clock())
    }

    /** uid устройства: создается один раз на установку, выход его не стирает. Хранилище не пишется — IOException. */
    suspend fun deviceUid(): String {
        var uid = ""
        store.edit { prefs ->
            uid = prefs[UID]?.takeIf { DeviceInfo.validUid(it) } ?: UUID.randomUUID().toString().also { prefs[UID] = it }
        }
        return uid
    }

    /**
     * Подтвердить аккаунт для сброса PIN-кода блока сайтов (решение 742): тот же аккаунт, что в приложении, — обычный вход, но
     * без остановки защиты: VPN и блок сайтов остаются. Другая почта — отказ без запроса к сайту: сброс не меняет аккаунт.
     */
    suspend fun confirm(email: String, password: String, code: String?): LoginResult {
        val current = state.value?.info?.email
        if (current == null || !current.equals(email.trim(), ignoreCase = true)) {
            return LoginResult.Failed(AccountError.Invalid)
        }

        return login(email.trim(), password, code, null, keepProtection = true)
    }

    /**
     * Вход: без кода — первый шаг (участнику его хватает, администратору сайт ответит [LoginResult.CodeRequired]); на втором —
     * не больше одного из [code] (2FA) и [recovery] (резервный код). Хранилище ключей или файл не работают — [AccountError.Store].
     */
    suspend fun login(email: String, password: String, code: String?, recovery: String?, keepProtection: Boolean = false): LoginResult = lock.withLock {
        val attempt = currentGeneration()
        withContext(Dispatchers.IO) {
            // Новый явный вход никогда не наследует автоматический запуск и ограничения предыдущего аккаунта.
            // Восстановление идет до запроса сайта: оставшийся туннель не должен закрывать экрану входа интернет.
            if (!keepProtection && !stopProtection()) return@withContext LoginResult.Failed(AccountError.Store)
            if (!boxWorks()) {
                journal.warning("account sign-in  key store unavailable")
                return@withContext LoginResult.Failed(AccountError.Store)
            }

            val uid = try {
                deviceUid()
            } catch (_: IOException) {
                journal.warning("account sign-in  storage unavailable")
                return@withContext LoginResult.Failed(AccountError.Store)
            }

            val device = DeviceInfo(uid, DeviceInfo.phoneName(Build.MANUFACTURER, Build.MODEL), BuildConfig.VERSION_NAME)
            val result = source.login(email, password, code, recovery, device)
            val session = when (result) {
                is LoginResult.Ok -> result.session
                LoginResult.CodeRequired -> {
                    // Пароль верный, аккаунт администратора: экран входа спросит код. Входа еще нет.
                    journal.info("account sign-in  code required")
                    return@withContext result
                }
                is LoginResult.Failed -> {
                    journal.info("account sign-in  " + result.error.name)
                    return@withContext result
                }
            }

            if (attempt != currentGeneration()) {
                // Выход во время запроса входа: поздний ответ не возвращает отмененный доступ.
                scope.launch(Dispatchers.IO) { source.logout(session.token) }
                return@withContext LoginResult.Failed(AccountError.Store)
            }
            try {
                save(session)
                logoutFence.allow()
            } catch (e: CancellationException) {
                throw e
            } catch (_: Exception) {
                // Токен не запечатать или не записать — без защищенного хранения его не держим: устройство на сайте снимаем.
                journal.warning("account sign-in  token not stored")
                source.logout(session.token)
                return@withContext LoginResult.Failed(AccountError.Store)
            }

            val accepted = synchronized(accessLock) {
                if (generation != attempt) {
                    false
                } else {
                    generation++
                    denied.value = false
                    cleanupPending = false
                    deniedReason = null
                    notice.value = null
                    true
                }
            }
            if (!accepted) {
                clearStored()
                scope.launch(Dispatchers.IO) { source.logout(session.token) }
                return@withContext LoginResult.Failed(AccountError.Store)
            }
            expiryWake.trySend(Unit)
            lastCheck = clock()
            // Защита не включилась из-за входа (PGC-ACC-001) — вход есть, этой ошибки больше нет: главная снова «Выключено», а
            // уведомление о сбое ProtectionNotifier убирает по смене состояния. Другие ошибки остаются.
            monitor.update { if (it.state == TunnelState.Error && it.error == TunnelErrors.NO_ACCESS) TunnelStatus() else it }
            journal.info("account sign-in  ok")
            result
        }
    }

    /**
     * Выход («Настройки» → «Выйти»): доступ запрещается сразу (uid остается), окно показывает вход; запись очищается,
     * отдельный маркер запрещает ее восстановление при сбое DataStore. Потом сайту
     * POST /api/app/logout, пока защита еще идет: запрос, как весь трафик приложения, идет через туннель, а во время его
     * разборки оборвался бы, и сайт не снял бы устройство. Ответа ждем не дольше [logoutWaitMs], и только потом защита
     * выключается. Ошибка записи не разрешает подключение снова; незакрепленный выход виден в [AccountNotice].
     */
    suspend fun logout() = performLogout(block(null, logout = true))

    private suspend fun performLogout(attempt: Long) {
        try {
            val token = lock.withLock {
                if (attempt != currentGeneration()) return
                seeded = false
                val sealed = read()?.get(TOKEN) ?: cachedPreferences?.get(TOKEN)
                withContext(NonCancellable + Dispatchers.IO) { clearStored() }
                journal.info("account signed out")
                when (val opened = sealed?.let(::open)) {
                    is Opened.Token -> opened.value
                    else -> null
                }
            }

            if (token != null) {
                // Запрос идет еще под защитой; неудачная запись не мешает отзыву токена на сайте.
                val sent = scope.async(Dispatchers.IO) {
                    try {
                        source.logout(token)
                    } catch (e: CancellationException) {
                        throw e
                    } catch (e: Exception) {
                        journal.warning("account sign-out  site error " + e.javaClass.simpleName)
                    }
                }
                if (withTimeoutOrNull(logoutWaitMs) { sent.join() } == null) {
                    journal.warning("account sign-out  site did not answer in time")
                }
            }
        } finally {
            withContext(NonCancellable) {
                lock.withLock {
                    synchronized(accessLock) { if (logoutAttempt == attempt) logoutAttempt = null }
                    if (attempt == currentGeneration() && denied.value) stopProtection()
                }
            }
            expiryWake.trySend(Unit)
        }
    }

    /** Выход запрещает подключение сразу; закрытие окна не отменяет сохранение выхода и отзыв токена. */
    fun signOut() {
        val attempt = block(null, logout = true)
        scope.launch { performLogout(attempt) }
    }

    /** Повтор доступен на экране входа, даже если токен уже удален или служба раньше не ответила. */
    fun retryNetworkRecovery() {
        scope.launch {
            lock.withLock {
                if (denied.value || (read() ?: cachedPreferences)?.get(TOKEN) == null) stopProtection()
            }
        }
    }

    /**
     * Спросить сайт о доступе сейчас (запуск, открытие окна, расписание); входа нет — ничего. Сбой хранилища или иной —
     * в журнал, как «сайт не ответил»: вход и защита остаются, следующая проверка — по расписанию.
     */
    suspend fun refresh() {
        if (seeded) {
            return
        }

        try {
            withContext(Dispatchers.IO) {
                if (enforceExpiry()) return@withContext
                if (!retryClear(stop = true)) check()
            }
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            journal.warning("account check  failed " + e.javaClass.simpleName)
        }
    }

    /**
     * Лента обновлений сайта (GET /api/app/updates) токеном этого входа: токен из репозитория не выходит, разбирает ленту
     * UpdateCheck. Входа нет — [UpdatesFeed.SignedOut] без запроса; засеянный для проверок на устройстве вход — без запроса
     * (настоящий сайт отозвал бы поддельный токен). 401 — отзыв, как у проверки доступа: вход стирается, если он все тот же,
     * защита выключается, окно — вход с «Доступ отозван». Хранилище или ключ не читаются — как «сайт не ответил».
     */
    override suspend fun updates(channel: String): UpdatesFeed {
        if (seeded) {
            return UpdatesFeed.Unreachable
        }

        val (sealed, attempt) = try {
            lock.withLock {
                if (denied.value) return UpdatesFeed.SignedOut
                val stored = read() ?: return UpdatesFeed.Unreachable
                (stored[TOKEN] ?: return UpdatesFeed.SignedOut) to currentGeneration()
            }
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            journal.warning("account updates  failed " + e.javaClass.simpleName)
            return UpdatesFeed.Unreachable
        }

        if (enforceExpiry() || denied.value) return UpdatesFeed.SignedOut
        val token = when (val opened = withContext(Dispatchers.IO) { open(sealed) }) {
            is Opened.Token -> opened.value
            else -> {
                journal.warning("account updates  sign-in unreadable now")
                return UpdatesFeed.Unreachable
            }
        }

        val feed = source.updates(token, channel)
        if (attempt != currentGeneration() || denied.value) {
            return if (denied.value) UpdatesFeed.SignedOut else UpdatesFeed.Unreachable
        }
        if (feed == UpdatesFeed.Revoked) {
            journal.warning("account updates  access revoked")
            try {
                finish(sealed, attempt, AccountNotice.Revoked)
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                journal.warning("account updates  failed " + e.javaClass.simpleName)
            }
        }

        return feed
    }

    /** Окно открылось: проверить, если давно не спрашивали сайт. */
    fun checkNow() {
        // Возобновление окна всегда проверяет локальный срок, даже если сайт только что спрашивали.
        expiryWake.trySend(Unit)
        scope.launch {
            if (enforceExpiry()) return@launch
            val last = lastCheck
            if (last == null || clock().toEpochMilli() - last.toEpochMilli() >= AccountGate.RECHECK_AFTER_MS) refresh()
        }
    }

    /** Сетевой опрос и локальный срок — независимые задачи: зависший сайт не продлевает доступ. */
    @Synchronized
    fun start() {
        if (started) return
        started = true
        scope.launch {
            while (true) {
                refresh()
                delay(AccountGate.CHECK_EVERY_MS)
            }
        }
        scope.launch {
            while (true) {
                try {
                    withContext(Dispatchers.IO) {
                        enforceExpiry()
                        retryClear(stop = recovery.value == AccountNetworkRecovery.Failed)
                    }
                } catch (e: CancellationException) {
                    throw e
                } catch (e: Exception) {
                    // Ошибка отдельной попытки не должна навсегда остановить контроль срока и повтор очистки.
                    journal.warning("account deadline  failed " + e.javaClass.simpleName)
                }
                val expires = if (denied.value) null else cachedPreferences?.let(::info)?.expiresAt
                val wait = expires?.let { Duration.between(clock(), it).toMillis().coerceAtLeast(1L) }
                    ?.coerceAtMost(expiryPollMs) ?: expiryPollMs
                withTimeoutOrNull(wait) { expiryWake.receive() }
            }
        }
    }

    /**
     * Вход для проверок на устройстве (androidTest, решение 623): демо-режима у телефона нет, а настоящий сайт поддельный
     * токен не примет. Засеянный вход сайт не проверяет, пока процесс жив или до выхода.
     */
    @VisibleForTesting
    suspend fun seedForTests(session: AccountSession) {
        lock.withLock {
            seeded = true
            withContext(Dispatchers.IO) {
                save(session)
                logoutFence.allow()
            }
            synchronized(accessLock) {
                generation++
                denied.value = false
                cleanupPending = false
                notice.value = null
            }
            expiryWake.trySend(Unit)
        }
    }

    /** Стереть вход без запроса к сайту и без команды службе — проверка отказа службы без входа на устройстве (androidTest). */
    @VisibleForTesting
    suspend fun signOutForTests() {
        lock.withLock {
            seeded = false
            block(null)
            clearStored()
        }
    }

    /** Остановить проверки (тесты). */
    @VisibleForTesting
    internal fun close() = scope.cancel()

    /**
     * Чтение и итог — под замком, запрос к сайту (до 15 с) — без него: выход и вход его не ждут. Итог применяется, только
     * если это все тот же вход: вышли или вошли заново, пока шел запрос, — ответ про старый токен отбрасывается.
     */
    private suspend fun check() {
        val (sealed, expires, attempt) = lock.withLock {
            if (seeded || denied.value) {
                return
            }

            // Хранилище не прочиталось — как «сайт не ответил»: вход и защита как были.
            val stored = read() ?: return
            val current = stored[TOKEN]
            if (current == null) {
                // Входа нет, а защита идет — подключение разминулось с выходом: выключить.
                stopProtection()
                return
            }

            Triple(current, stored[EXPIRES]?.let(Instant::ofEpochMilli), currentGeneration())
        }

        if (enforceExpiry() || attempt != currentGeneration()) return
        val opened = open(sealed)
        if (expires == null || opened == Opened.Lost) {
            // Ключ Keystore потерян навсегда или запись неполная: вход не прочитать — войти заново.
            journal.warning("account check  sign-in unreadable")
            finish(sealed, attempt, null)
            return
        }

        val status = if (opened is Opened.Token) {
            lastCheck = clock()
            source.status(opened.value)
        } else {
            // Хранилище ключей сейчас не отвечает (решение 648): токен не открыть, но он цел — как «сайт не ответил».
            journal.warning("account check  key store unavailable, sign-in kept")
            StatusResult.Unreachable
        }

        when (val verdict = AccountGate.verdict(expires, status, clock())) {
            is AccessVerdict.Keep -> {
                if (status is StatusResult.Ok) {
                    lock.withLock {
                        if (attempt != currentGeneration() || denied.value) return
                        val updated = store.edit { prefs ->
                            if (prefs[TOKEN] == sealed && attempt == currentGeneration() && !denied.value) {
                                prefs[EXPIRES] = verdict.expiresAt.toEpochMilli()
                                prefs[EMAIL] = status.user.email
                                prefs[NAME] = status.user.displayName
                                prefs[DEVICE] = status.device.name
                                prefs[CHECKED] = clock().toEpochMilli()
                            }
                        }
                        rememberWritten(updated)
                        expiryWake.trySend(Unit)
                    }
                }

                journal.info("account check  " + if (verdict.confirmed) "ok" else "no answer, sign-in kept")
            }

            AccessVerdict.Revoked -> {
                journal.warning("account check  access revoked")
                finish(sealed, attempt, AccountNotice.Revoked)
            }

            AccessVerdict.Expired -> {
                journal.warning("account check  sign-in expired")
                finish(sealed, attempt, AccountNotice.Expired)
            }
        }
    }

    /** Старый ответ не отзывает новый вход, даже если сайт выдал тот же токен или хранилище сейчас не читается. */
    private suspend fun finish(sealed: String, attempt: Long, why: AccountNotice?) {
        lock.withLock {
            if (attempt != currentGeneration() || denied.value) return
            val current = read() ?: cachedPreferences
            if (current?.get(TOKEN) != sealed) return
            block(why)
            try {
                withContext(NonCancellable + Dispatchers.IO) { clearStored() }
            } finally {
                stopProtection()
            }
        }
    }

    /** Известный срок проверяется и при ошибке чтения DataStore: последняя подтвержденная запись остается в памяти. */
    @VisibleForTesting
    internal suspend fun enforceExpiry(): Boolean = lock.withLock {
        if (denied.value) return@withLock false
        val current = cachedPreferences ?: return@withLock false
        val expires = info(current)?.expiresAt ?: return@withLock false
        if (clock().isBefore(expires)) return@withLock false
        journal.warning("account check  sign-in expired")
        block(AccountNotice.Expired)
        try {
            withContext(NonCancellable + Dispatchers.IO) { clearStored() }
        } finally {
            stopProtection()
        }
        true
    }

    private fun currentGeneration(): Long = synchronized(accessLock) { generation }

    /** Немедленный запрет, до любой записи на диск и ожидания текущего сетевого запроса. */
    private fun block(why: AccountNotice?, logout: Boolean = false): Long = synchronized(accessLock) {
        generation++
        if (logout) logoutAttempt = generation
        deniedReason = why
        notice.value = why
        denied.value = true
        cleanupPending = true
        generation
    }

    /** Очистка и остановка принадлежат одному поколению: новый вход не может вклиниться между ними. */
    private suspend fun retryClear(stop: Boolean = false): Boolean = lock.withLock {
        if (!denied.value) return@withLock false
        val loggingOut = synchronized(accessLock) { logoutAttempt == generation }
        if (!loggingOut) {
            if (cleanupPending) clearStored()
            if (stop) stopProtection()
        }
        true
    }

    /** Хранилище сейчас; не прочиталось — null и запись в журнал (без содержимого). */
    private suspend fun read(): Preferences? {
        val version = synchronized(accessLock) { cacheVersion }
        return try {
            val read = store.data.first()
            synchronized(accessLock) {
                if (cacheVersion == version) cachedPreferences = read
                cachedPreferences ?: read
            }
        } catch (e: IOException) {
            journal.warning("account storage read failed  " + e.javaClass.simpleName)
            null
        }
    }

    private fun rememberWritten(prefs: Preferences) = synchronized(accessLock) {
        cacheVersion++
        cachedPreferences = prefs
    }

    /** Запечатать токен и записать вход; не вышло — исключение, вход не сохранен. */
    private suspend fun save(session: AccountSession) {
        val sealed = Base64.getEncoder().encodeToString(box.seal(session.token.toByteArray(Charsets.UTF_8)))
        val saved = store.edit { prefs ->
            prefs[TOKEN] = sealed
            prefs[EXPIRES] = session.expiresAt.toEpochMilli()
            prefs[EMAIL] = session.user.email
            prefs[NAME] = session.user.displayName
            prefs[DEVICE] = session.device.name
            prefs.remove(CHECKED)
        }
        rememberWritten(saved)
    }

    /**
     * Два независимых способа закрепить запрет: маленький маркер и удаление входа из DataStore. Маркер остается до нового
     * входа. Не удалось ни то, ни другое — явно сообщаем и повторяем; токен в текущем процессе уже не дает доступа.
     */
    private suspend fun clearStored() {
        var durable = false
        try {
            logoutFence.block()
            durable = true
        } catch (e: IOException) {
            journal.warning("account sign-out  marker error " + e.javaClass.simpleName)
        }
        try {
            val cleared = store.edit { prefs ->
                prefs.remove(TOKEN)
                prefs.remove(EXPIRES)
                prefs.remove(EMAIL)
                prefs.remove(NAME)
                prefs.remove(DEVICE)
                prefs.remove(CHECKED)
            }
            rememberWritten(cleared)
            cleanupPending = false
            durable = true
        } catch (e: IOException) {
            journal.warning("account sign-out  storage error " + e.javaClass.simpleName)
        }
        notice.value = if (durable) deniedReason else AccountNotice.SignOutNotSaved
    }

    /**
     * Защита идет, поднимается или на паузе — выключить; выключена — службу не будить. Вызывать после запрета доступа:
     * подключение, которое монитор еще не показал, служба отклонит сама, перепроверив вход.
     */
    private suspend fun stopProtection(): Boolean {
        recovery.value = AccountNetworkRecovery.Pending
        val status = monitor.status.value
        val stopped = if (status.state == TunnelState.Off && !status.paused) {
            true
        } else {
            try {
                // Error тоже может удерживать карту блокировки; его нельзя считать доказательством снятого туннеля.
                control.disconnect()
                withTimeoutOrNull(networkWaitMs) {
                    monitor.status.first { it.state == TunnelState.Off && !it.paused }
                } != null
            } catch (e: Exception) {
                if (e is CancellationException) {
                    recovery.value = AccountNetworkRecovery.Failed
                    throw e
                }
                journal.warning("account  protection stop not delivered " + e.javaClass.simpleName)
                false
            }
        }
        var reset = true
        try {
            settings.resetAccountNetwork()
        } catch (e: Exception) {
            if (e is CancellationException) {
                recovery.value = AccountNetworkRecovery.Failed
                throw e
            }
            reset = false
            journal.warning("account  network settings reset failed " + e.javaClass.simpleName)
        }
        return (reset && stopped).also {
            recovery.value = if (it) AccountNetworkRecovery.Idle else AccountNetworkRecovery.Failed
        }
    }

    /**
     * Открыть токен. Потерян навсегда — только при явной потере ключа: тег GCM не сошелся (ключ пропал, на его месте
     * новый), ключ признан недействительным или запись не нашего формата. Остальное (KeyStoreException, ProviderException
     * «keystore busy» сразу после загрузки и т. п.) — хранилище ключей сейчас не отвечает (решение 648).
     */
    private fun open(sealed: String): Opened = try {
        Opened.Token(String(box.open(Base64.getDecoder().decode(sealed)), Charsets.UTF_8))
    } catch (_: IllegalArgumentException) {
        Opened.Lost
    } catch (_: BadPaddingException) {
        Opened.Lost
    } catch (_: KeyPermanentlyInvalidatedException) {
        Opened.Lost
    } catch (e: Exception) {
        journal.warning("account  key store error " + e.javaClass.simpleName)
        Opened.Unavailable
    }

    /** Хранилище ключей работает: запечатанное открывается тем же ключом. */
    private fun boxWorks(): Boolean = try {
        box.open(box.seal(PROBE)).contentEquals(PROBE)
    } catch (_: Exception) {
        false
    }

    private fun info(prefs: Preferences): AccountInfo? {
        if (prefs[TOKEN] == null) {
            return null
        }

        val expires = prefs[EXPIRES] ?: return null
        return AccountInfo(
            email = prefs[EMAIL] ?: "",
            displayName = prefs[NAME] ?: "",
            deviceName = prefs[DEVICE] ?: "",
            expiresAt = Instant.ofEpochMilli(expires),
            checkedAt = prefs[CHECKED]?.let(Instant::ofEpochMilli),
        )
    }

    /** Токен из хранилища: открыт, потерян навсегда или хранилище ключей сейчас не отвечает. */
    private sealed interface Opened {
        class Token(val value: String) : Opened

        data object Lost : Opened

        data object Unavailable : Opened
    }

    private companion object {
        val UID = stringPreferencesKey("device.uid")
        val TOKEN = stringPreferencesKey("token")
        val EXPIRES = longPreferencesKey("expires_at")
        val EMAIL = stringPreferencesKey("email")
        val NAME = stringPreferencesKey("display_name")
        val DEVICE = stringPreferencesKey("device_name")
        val CHECKED = longPreferencesKey("checked_at")
        val PROBE = "pg-account-probe".toByteArray(Charsets.UTF_8)

        /** Ответ сайта на выход под защитой обычно приходит за доли секунды; дольше туннель не держим. */
        const val LOGOUT_WAIT_MS = 3_000L

        /** Повтор чтения хранилища после сбоя: 1 с, 2 с, 4 с … до 64 с. */
        const val READ_RETRY_MS = 1_000L
        const val READ_RETRY_STEPS = 6L
    }
}

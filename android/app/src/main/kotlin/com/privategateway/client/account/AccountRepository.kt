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
import com.privategateway.client.core.vpn.TunnelErrors
import com.privategateway.client.core.vpn.TunnelState
import com.privategateway.client.core.vpn.TunnelStatus
import com.privategateway.client.data.SecretBox
import com.privategateway.client.vpn.TunnelControl
import com.privategateway.client.vpn.TunnelJournal
import com.privategateway.client.vpn.TunnelMonitor
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineExceptionHandler
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.async
import kotlinx.coroutines.cancel
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

/** Почему экран входа показан снова: доступ отозван на сайте или срок прошел без подтверждения. */
enum class AccountNotice {
    Revoked,
    Expired,
}

/** Вход: [info] — сохраненный вход (null — входа нет), [notice] — почему вход нужен снова (до следующего входа). */
data class AccountState(val info: AccountInfo?, val notice: AccountNotice? = null) {
    val signedIn: Boolean get() = info != null
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
}

/**
 * Вход в аккаунт сайта Private Gateway Web (решения 616–619, 621, 623). Пользоваться защитой можно только с действующим
 * входом: решение о доступе принимает сайт. Токен — только запечатанным [SecretBox] (ключ в Android Keystore) в своем
 * DataStore; без работающего хранилища ключей вход не выполняется. Рядом, не секретом, — срок, почта, имя, имя устройства и
 * время последней проверки; uid устройства создается один раз на установку и при выходе остается.
 *
 * Проверка доступа: при запуске процесса, при открытии окна и раз в 30 минут. Отзыв — только ответ 401: токен стирается,
 * защита выключается, окно показывает вход с «Доступ отозван». Без ответа сайта действует последний подтвержденный срок;
 * он прошел — выход как при истечении. В журнал «Проверки» — только итог, без токена, почты и кодов.
 *
 * Вход стирается всегда раньше, чем выключается защита: служба, поднимая туннель, перепроверяет вход в конце подключения, и
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
) {
    // Ошибка, которую не поймали ниже, — в журнал, а не в обработчик потока: тот завершил бы процесс вместе со службой VPN.
    private val scope = CoroutineScope(
        SupervisorJob() + Dispatchers.Default + CoroutineExceptionHandler { _, failure ->
            journal.error("account  unexpected " + failure.javaClass.simpleName)
        },
    )
    private val lock = Mutex()
    private val notice = MutableStateFlow<AccountNotice?>(null)
    private var started = false

    /** Часы — для тестов срока. */
    @VisibleForTesting
    internal var clock: () -> Instant = Instant::now

    /** Сколько ждать ответа сайта на выход, пока защита еще идет: дольше выход не держит туннель. */
    @VisibleForTesting
    internal var logoutWaitMs = LOGOUT_WAIT_MS

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
        .onEach { readOnce = true }
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
    val state: StateFlow<AccountState?> = combine(preferences, notice) { prefs, why -> AccountState(info(prefs), why) }
        .stateIn(scope, SharingStarted.Eagerly, null)

    /**
     * Можно ли подключаться: вход есть и срок не прошел. Читает само хранилище, а не [state]: тот обновляется позже записи, а
     * выход или отзыв, записанный только что, служба должна увидеть сразу. Хранилище не прочиталось — по последнему известному.
     */
    suspend fun allowed(): Boolean {
        val prefs = read()
        val info = if (prefs != null) info(prefs) else state.value?.info
        return info != null && AccountGate.allowed(info.expiresAt, clock())
    }

    /** То же без ожидания — для плитки в шторке: хранилище еще не прочитано — нет. */
    fun allowedNow(): Boolean {
        val info = state.value?.info ?: return false
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

    /** Вход: ровно один из [code] (2FA) и [recovery] (резервный код). Хранилище ключей или файл не работают — [AccountError.Store]. */
    suspend fun login(email: String, password: String, code: String?, recovery: String?): LoginResult = lock.withLock {
        withContext(Dispatchers.IO) {
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
            if (result !is LoginResult.Ok) {
                journal.info("account sign-in  " + ((result as LoginResult.Failed).error.name))
                return@withContext result
            }

            try {
                save(result.session)
            } catch (e: CancellationException) {
                throw e
            } catch (_: Exception) {
                // Токен не запечатать или не записать — без защищенного хранения его не держим: устройство на сайте снимаем.
                journal.warning("account sign-in  token not stored")
                source.logout(result.session.token)
                return@withContext LoginResult.Failed(AccountError.Store)
            }

            notice.value = null
            lastCheck = clock()
            // Защита не включилась из-за входа (PGC-ACC-001) — вход есть, этой ошибки больше нет: главная снова «Выключено», а
            // уведомление о сбое ProtectionNotifier убирает по смене состояния. Другие ошибки остаются.
            monitor.update { if (it.state == TunnelState.Error && it.error == TunnelErrors.NO_ACCESS) TunnelStatus() else it }
            journal.info("account sign-in  ok")
            result
        }
    }

    /**
     * Выход («Настройки» → «Выйти»): вход стирается сразу (uid остается) — окно тут же показывает экран входа. Потом сайту
     * POST /api/app/logout, пока защита еще идет: запрос, как весь трафик приложения, идет через туннель, а во время его
     * разборки оборвался бы, и сайт не снял бы устройство. Ответа ждем не дольше [logoutWaitMs], и только потом защита
     * выключается. Ошибка сети или хранилища выходу не мешает (решение 647).
     */
    suspend fun logout() {
        try {
            val token = lock.withLock {
                seeded = false
                val sealed = read()?.get(TOKEN)
                withContext(Dispatchers.IO) { clear(null) }
                journal.info("account signed out")
                when (val opened = sealed?.let(::open)) {
                    is Opened.Token -> opened.value
                    else -> null
                }
            }

            if (token != null) {
                // Отдельной задачей: запрос в потоке ввода-вывода по таймауту не прерывается, а выход ждать его не должен.
                val sent = scope.async(Dispatchers.IO) { source.logout(token) }
                if (withTimeoutOrNull(logoutWaitMs) { sent.join() } == null) {
                    journal.warning("account sign-out  site did not answer in time")
                }
            }
        } catch (e: IOException) {
            journal.warning("account sign-out  storage error " + e.javaClass.simpleName)
        } finally {
            stopProtection()
        }
    }

    /** Выход из окна: в своей области — окно, закрытое во время выхода, его не обрывает, а нажатие не ждет сайта. */
    fun signOut() {
        scope.launch { logout() }
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
            withContext(Dispatchers.IO) { check() }
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            journal.warning("account check  failed " + e.javaClass.simpleName)
        }
    }

    /** Окно открылось: проверить, если давно не спрашивали сайт. */
    fun checkNow() {
        val last = lastCheck
        if (last != null && clock().toEpochMilli() - last.toEpochMilli() < AccountGate.RECHECK_AFTER_MS) {
            return
        }

        scope.launch { refresh() }
    }

    /** Проверка при запуске и раз в 30 минут, пока жив процесс. Повторный вызов — ничего. */
    @Synchronized
    fun start() {
        if (started) {
            return
        }

        started = true
        scope.launch {
            while (true) {
                refresh()
                delay(AccountGate.CHECK_EVERY_MS)
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
            withContext(Dispatchers.IO) { save(session) }
            notice.value = null
        }
    }

    /** Стереть вход без запроса к сайту и без команды службе — проверка отказа службы без входа на устройстве (androidTest). */
    @VisibleForTesting
    suspend fun signOutForTests() {
        lock.withLock {
            seeded = false
            clear(null)
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
        val (sealed, expires) = lock.withLock {
            if (seeded) {
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

            current to stored[EXPIRES]?.let(Instant::ofEpochMilli)
        }

        val opened = open(sealed)
        if (expires == null || opened == Opened.Lost) {
            // Ключ Keystore потерян навсегда или запись неполная: вход не прочитать — войти заново.
            journal.warning("account check  sign-in unreadable")
            finish(sealed, null)
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
                    store.edit { prefs ->
                        // Вход стерли или сменили, пока шел запрос, — подтверждение его не возвращает.
                        if (prefs[TOKEN] == sealed) {
                            prefs[EXPIRES] = verdict.expiresAt.toEpochMilli()
                            prefs[EMAIL] = status.user.email
                            prefs[NAME] = status.user.displayName
                            prefs[DEVICE] = status.device.name
                            prefs[CHECKED] = clock().toEpochMilli()
                        }
                    }
                }

                journal.info("account check  " + if (verdict.confirmed) "ok" else "no answer, sign-in kept")
            }

            AccessVerdict.Revoked -> {
                journal.warning("account check  access revoked")
                finish(sealed, AccountNotice.Revoked)
            }

            AccessVerdict.Expired -> {
                journal.warning("account check  sign-in expired")
                finish(sealed, AccountNotice.Expired)
            }
        }
    }

    /** Вход [sealed] больше не действует: стереть его, если он все еще тот же, и только потом выключить защиту. */
    private suspend fun finish(sealed: String, why: AccountNotice?) {
        lock.withLock {
            val current = read()
            if (current != null && current[TOKEN] != sealed) {
                return
            }

            try {
                clear(why)
            } finally {
                stopProtection()
            }
        }
    }

    /** Хранилище сейчас; не прочиталось — null и запись в журнал (без содержимого). */
    private suspend fun read(): Preferences? = try {
        store.data.first()
    } catch (e: IOException) {
        journal.warning("account storage read failed  " + e.javaClass.simpleName)
        null
    }

    /** Запечатать токен и записать вход; не вышло — исключение, вход не сохранен. */
    private suspend fun save(session: AccountSession) {
        val sealed = Base64.getEncoder().encodeToString(box.seal(session.token.toByteArray(Charsets.UTF_8)))
        store.edit { prefs ->
            prefs[TOKEN] = sealed
            prefs[EXPIRES] = session.expiresAt.toEpochMilli()
            prefs[EMAIL] = session.user.email
            prefs[NAME] = session.user.displayName
            prefs[DEVICE] = session.device.name
            prefs.remove(CHECKED)
        }
    }

    /** Стереть вход (uid остается) и запомнить, почему экран входа показан снова. */
    private suspend fun clear(why: AccountNotice?) {
        // Причина — до записи: окно, увидев, что входа нет, сразу знает почему.
        notice.value = why
        store.edit { prefs ->
            prefs.remove(TOKEN)
            prefs.remove(EXPIRES)
            prefs.remove(EMAIL)
            prefs.remove(NAME)
            prefs.remove(DEVICE)
            prefs.remove(CHECKED)
        }
    }

    /**
     * Защита идет, поднимается или на паузе — выключить; выключена — службу не будить. Вызывать после того, как вход стерт:
     * подключение, которое монитор еще не показал, служба отклонит сама, перепроверив вход.
     */
    private fun stopProtection() {
        val status = monitor.status.value
        if (status.state == TunnelState.Off && !status.paused || status.state == TunnelState.Error) {
            return
        }

        try {
            control.disconnect()
        } catch (_: IllegalStateException) {
            // Android не дал запустить службу из фона: защиты тогда и нет, а следующее подключение служба не пустит.
            journal.warning("account  protection stop not delivered")
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

package com.privategateway.client.account

import android.os.Build
import androidx.annotation.VisibleForTesting
import androidx.datastore.core.DataStore
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
import com.privategateway.client.core.vpn.TunnelState
import com.privategateway.client.data.SecretBox
import com.privategateway.client.vpn.TunnelControl
import com.privategateway.client.vpn.TunnelJournal
import com.privategateway.client.vpn.TunnelMonitor
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.catch
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.filterNotNull
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import java.io.IOException
import java.time.Instant
import java.util.Base64
import java.util.UUID
import javax.inject.Inject
import javax.inject.Qualifier
import javax.inject.Singleton

/** Отдельное хранилище DataStore «account»: вход и uid устройства — мимо настроек и их резервной копии (решение 617). */
@Qualifier
@Retention(AnnotationRetention.BINARY)
annotation class AccountStore

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
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private val lock = Mutex()
    private val notice = MutableStateFlow<AccountNotice?>(null)
    private var started = false

    /** Часы — для тестов срока. */
    @VisibleForTesting
    internal var clock: () -> Instant = Instant::now

    /** Когда последний раз спрашивали сайт: открытие окна сразу после запуска не спрашивает второй раз. */
    @Volatile
    private var lastCheck: Instant? = null

    /** Засеянный для проверок на устройстве вход сайт не проверяет: настоящий сайт отозвал бы поддельный токен. */
    @Volatile
    private var seeded = false

    private val preferences = store.data.catch { failure ->
        if (failure is IOException) emit(emptyPreferences()) else throw failure
    }

    /** Вход; null — хранилище еще не прочитано (экран входа не мелькает у тех, кто вошел). */
    val state: StateFlow<AccountState?> = combine(preferences, notice) { prefs, why -> AccountState(info(prefs), why) }
        .stateIn(scope, SharingStarted.Eagerly, null)

    /** Можно ли подключаться: вход есть и срок не прошел. Ждет, пока хранилище прочитано (служба при загрузке телефона). */
    suspend fun allowed(): Boolean {
        val info = state.filterNotNull().first().info ?: return false
        return AccountGate.allowed(info.expiresAt, clock())
    }

    /** То же без ожидания — для плитки в шторке: хранилище еще не прочитано — нет. */
    fun allowedNow(): Boolean {
        val info = state.value?.info ?: return false
        return AccountGate.allowed(info.expiresAt, clock())
    }

    /** uid устройства: создается один раз на установку, выход его не стирает. */
    suspend fun deviceUid(): String {
        var uid = ""
        store.edit { prefs ->
            uid = prefs[UID]?.takeIf { DeviceInfo.validUid(it) } ?: UUID.randomUUID().toString().also { prefs[UID] = it }
        }
        return uid
    }

    /** Вход: ровно один из [code] (2FA) и [recovery] (резервный код). Хранилище ключей не работает — [AccountError.Store]. */
    suspend fun login(email: String, password: String, code: String?, recovery: String?): LoginResult = lock.withLock {
        withContext(Dispatchers.IO) {
            if (!boxWorks()) {
                journal.warning("account sign-in  key store unavailable")
                return@withContext LoginResult.Failed(AccountError.Store)
            }

            val device = DeviceInfo(deviceUid(), DeviceInfo.phoneName(Build.MANUFACTURER, Build.MODEL), BuildConfig.VERSION_NAME)
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
            journal.info("account sign-in  ok")
            result
        }
    }

    /**
     * Выход («Настройки» → «Выйти»): выключить защиту, стереть токен и сведения входа (uid остается), сообщить сайту. Ошибка
     * сети выходу не мешает.
     */
    suspend fun logout() {
        lock.withLock {
            val token = withContext(Dispatchers.IO) {
                stopProtection()
                val sealed = store.data.first()[TOKEN]
                clear(null)
                seeded = false
                journal.info("account signed out")
                sealed?.let(::open)
            }

            // Сайт снимает устройство; ошибка сети выходу не мешает — вход на телефоне уже стерт.
            token?.let { source.logout(it) }
        }
    }

    /** Спросить сайт о доступе сейчас (запуск, открытие окна, расписание); входа нет — ничего. */
    suspend fun refresh() {
        if (seeded) {
            return
        }

        lock.withLock {
            // Засеять могли, пока проверка ждала замка.
            if (seeded) {
                return
            }

            withContext(Dispatchers.IO) { check() }
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

    /** Стереть вход без запроса к сайту — проверка отказа службы без входа на устройстве (androidTest). */
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

    private suspend fun check() {
        val stored = store.data.first()
        val sealed = stored[TOKEN] ?: return
        val expires = stored[EXPIRES]?.let(Instant::ofEpochMilli)
        val token = open(sealed)
        if (expires == null || token == null) {
            // Ключ Keystore потерян или запись неполная: вход не прочитать — войти заново.
            journal.warning("account check  sign-in unreadable")
            stopProtection()
            clear(null)
            return
        }

        lastCheck = clock()
        val status = source.status(token)
        when (val verdict = AccountGate.verdict(expires, status, clock())) {
            is AccessVerdict.Keep -> {
                if (status is StatusResult.Ok) {
                    store.edit { prefs ->
                        // Вход стерли, пока шел запрос, — подтверждение его не возвращает.
                        if (prefs[TOKEN] != null) {
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
                stopProtection()
                clear(AccountNotice.Revoked)
            }

            AccessVerdict.Expired -> {
                journal.warning("account check  sign-in expired")
                stopProtection()
                clear(AccountNotice.Expired)
            }
        }
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

    /** Защита идет, поднимается или на паузе — выключить; выключена — службу не будить. */
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

    private fun open(sealed: String): String? = try {
        String(box.open(Base64.getDecoder().decode(sealed)), Charsets.UTF_8)
    } catch (e: CancellationException) {
        throw e
    } catch (_: Exception) {
        null
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

    private companion object {
        val UID = stringPreferencesKey("device.uid")
        val TOKEN = stringPreferencesKey("token")
        val EXPIRES = longPreferencesKey("expires_at")
        val EMAIL = stringPreferencesKey("email")
        val NAME = stringPreferencesKey("display_name")
        val DEVICE = stringPreferencesKey("device_name")
        val CHECKED = longPreferencesKey("checked_at")
        val PROBE = "pg-account-probe".toByteArray(Charsets.UTF_8)
    }
}

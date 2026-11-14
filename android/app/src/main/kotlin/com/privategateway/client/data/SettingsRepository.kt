package com.privategateway.client.data

import androidx.datastore.core.DataStore
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.booleanPreferencesKey
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.intPreferencesKey
import androidx.datastore.preferences.core.longPreferencesKey
import androidx.datastore.preferences.core.stringPreferencesKey
import com.privategateway.client.core.backup.BackupSettings
import com.privategateway.client.core.notify.NotificationLevel
import com.privategateway.client.core.settings.ConnectionSettings
import com.privategateway.client.core.settings.DnsChoice
import com.privategateway.client.core.settings.RoutingSettings
import com.privategateway.client.core.settings.SubscriptionSettings
import com.privategateway.client.core.settings.TunnelSettings
import com.privategateway.client.core.updates.SemVer
import com.privategateway.client.core.updates.UpdatePolicy
import com.privategateway.client.core.vpn.ReconnectPolicy
import com.privategateway.client.core.vpn.SystemVpn
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.io.IOException
import javax.inject.Inject
import javax.inject.Singleton
import kotlin.random.Random

/**
 * Последняя удачная проверка обновления: когда сайт ответил (мс эпохи; null — ни разу) и что нашлось ([found] — версия; null —
 * новее нет). По ней окно после перезапуска показывает итог, а проверка при показе окна ждет 12 часов.
 */
data class UpdateMemo(val checkedAt: Long? = null, val found: String? = null)

/** Сессия APK, переданная Android; подтверждение Intent на диск не записывается. */
data class UpdateInstallation(val id: Int, val version: String, val committed: Boolean = false)

/**
 * Настройки приложения (DataStore): выбранный сервер (меняется только нажатием пользователя), настройки туннеля — DNS,
 * IPv6 через сервер, локальная сеть мимо туннеля, подключение и ядро, маршрутизация, — подписок, уведомлений,
 * переподключения и подключения при запуске. Неизвестное значение (из будущей версии) — значение по умолчанию.
 */
@Singleton
class SettingsRepository @Inject constructor(private val store: DataStore<Preferences>) {
    private val updatePolicyLock = Mutex()

    /** Увиденный стоп не забывается в этом процессе при ошибке записи; следующий edit сначала сохраняет эти политики. */
    private val pendingUpdatePolicies = mutableMapOf<String, UpdatePolicy>()

    val selectedServerId: Flow<String?> = store.data.map { it[SELECTED] }

    suspend fun select(id: String?) {
        store.edit { preferences ->
            if (id == null) {
                preferences.remove(SELECTED)
            } else {
                preferences[SELECTED] = id
            }
        }
    }

    val tunnel: Flow<TunnelSettings> = store.data.map { preferences ->
        TunnelSettings(
            dns = preferences[DNS]?.let { name -> DnsChoice.entries.firstOrNull { it.name == name } } ?: DnsChoice.Profile,
            customDns = preferences[CUSTOM_DNS],
            ipv6 = preferences[IPV6] ?: false,
            allowLan = preferences[ALLOW_LAN] ?: false,
            connection = ConnectionSettings.read(
                preferences[CORE_LOG],
                preferences[MTU],
                preferences[STRICT_ROUTES],
                preferences[SNIFFING],
                preferences[FINGERPRINT],
                preferences[MUX],
                preferences[TLS_FRAGMENT],
                preferences[CONNECT_TIMEOUT],
            ),
            routing = RoutingSettings.read(preferences[ROUTING_MODE], preferences[ROUTING_APPS], preferences[ROUTING_RULES]),
        )
    }

    suspend fun setTunnel(settings: TunnelSettings) {
        store.edit { preferences ->
            preferences[DNS] = settings.dns.name
            val custom = settings.customDns?.trim()?.takeIf { it.isNotEmpty() }
            if (custom == null) {
                preferences.remove(CUSTOM_DNS)
            } else {
                preferences[CUSTOM_DNS] = custom
            }

            preferences[IPV6] = settings.ipv6
            preferences[ALLOW_LAN] = settings.allowLan

            // «Подключение и ядро»: «Авто» у MTU — ключа нет.
            val connection = settings.connection
            preferences[CORE_LOG] = connection.coreLog.name
            val mtu = connection.mtu?.takeIf { ConnectionSettings.validMtu(it) }
            if (mtu == null) {
                preferences.remove(MTU)
            } else {
                preferences[MTU] = mtu
            }

            preferences[STRICT_ROUTES] = connection.strictRoutes
            preferences[SNIFFING] = connection.sniffing
            preferences[FINGERPRINT] = connection.fingerprint.name
            preferences[MUX] = connection.mux
            preferences[TLS_FRAGMENT] = connection.tlsFragment
            preferences[CONNECT_TIMEOUT] = connection.connectTimeoutSeconds

            // «Маршрутизация»: списки — текстом JSON.
            preferences[ROUTING_MODE] = settings.routing.mode.name
            preferences[ROUTING_APPS] = settings.routing.appsJson()
            preferences[ROUTING_RULES] = settings.routing.rulesJson()
        }
    }

    /** «Профили и подписки»: автообновление, только через VPN, сообщать о выбранном сервере. */
    val subscriptions: Flow<SubscriptionSettings> = store.data.map { preferences ->
        SubscriptionSettings.read(preferences[SUBS_REFRESH], preferences[SUBS_ONLY_VPN], preferences[SUBS_NOTIFY])
    }

    suspend fun setSubscriptions(settings: SubscriptionSettings) {
        store.edit { preferences ->
            preferences[SUBS_REFRESH] = settings.refresh.name
            preferences[SUBS_ONLY_VPN] = settings.onlyViaVpn
            preferences[SUBS_NOTIFY] = settings.notifySelected
        }
    }

    /** «Уведомления» (доска 22): что идет в шторку; по умолчанию «Только важные». */
    val notifications: Flow<NotificationLevel> = store.data.map { NotificationLevel.read(it[NOTIFY_LEVEL]) }

    suspend fun setNotifications(level: NotificationLevel) {
        store.edit { it[NOTIFY_LEVEL] = level.name }
    }

    /** «Переподключение»: что делать, когда связь с сервером пропала; по умолчанию «Экспоненц. до 60 с». */
    val reconnect: Flow<ReconnectPolicy> = store.data.map { ReconnectPolicy.read(it[RECONNECT]) }

    suspend fun setReconnect(policy: ReconnectPolicy) {
        store.edit { it[RECONNECT] = policy.name }
    }

    /**
     * «Подключаться при запуске» (доска 4): окно открыли заново — подключить к выбранному серверу; по умолчанию выключено.
     * Имя ключа — для iOS то же: ее настройки — файл JSON с именами ключей DataStore (решение 459).
     */
    val connectOnLaunch: Flow<Boolean> = store.data.map { it[CONNECT_ON_LAUNCH] ?: false }

    suspend fun setConnectOnLaunch(on: Boolean) {
        store.edit { it[CONNECT_ON_LAUNCH] = on }
    }

    /**
     * Первый запуск (доска 10) пройден: приветствие и «Добавьте первый сервер» больше не показываются. Ставится, когда появился
     * первый сервер или пользователь выбрал «Позже»; приложение, где серверы уже есть (обновление), приветствие не показывает.
     */
    val onboardingDone: Flow<Boolean> = store.data.map { it[ONBOARDING_DONE] ?: false }

    suspend fun setOnboardingDone() {
        store.edit { it[ONBOARDING_DONE] = true }
    }

    /** «Постоянная VPN» и блок без VPN, как их сообщил Android при последнем подключении (решение 445). */
    val systemVpn: Flow<SystemVpn> = store.data.map { SystemVpn(it[SYSTEM_ALWAYS_ON], it[SYSTEM_LOCKDOWN]) }

    /** Запомнить то, что Android сообщил туннелю; Android 9 не сообщает ничего — прежнее остается. */
    suspend fun setSystemVpn(flags: SystemVpn) {
        if (!flags.known) {
            return
        }

        store.edit { preferences ->
            flags.alwaysOn?.let { preferences[SYSTEM_ALWAYS_ON] = it }
            flags.lockdown?.let { preferences[SYSTEM_LOCKDOWN] = it }
        }
    }

    /** Последняя удачная проверка обновления — состояние телефона, а не настройка: в резервную копию не идет. */
    val updateMemo: Flow<UpdateMemo> = store.data.map { UpdateMemo(it[UPDATE_CHECKED], it[UPDATE_FOUND]) }

    /** Сайт ответил в [at] (мс эпохи): нашлась версия [found] или (null) новее нет. */
    suspend fun setUpdateMemo(at: Long, found: String?) {
        store.edit { preferences ->
            preferences[UPDATE_CHECKED] = at
            if (found == null) {
                preferences.remove(UPDATE_FOUND)
            } else {
                preferences[UPDATE_FOUND] = found
            }
        }
    }

    /** Неудача проверки не должна восстанавливаться после перезапуска как прежнее «последняя версия». */
    suspend fun clearUpdateMemo() {
        store.edit { preferences ->
            preferences.remove(UPDATE_CHECKED)
            preferences.remove(UPDATE_FOUND)
        }
    }

    val updateInstallation: Flow<UpdateInstallation?> = store.data.map { preferences ->
        val id = preferences[UPDATE_SESSION]
        val version = preferences[UPDATE_TARGET]
        when {
            id == null && version == null -> null
            id != null && id >= 0 && version != null && SemVer.parse(version) != null -> UpdateInstallation(id, version, preferences[UPDATE_COMMITTED] ?: false)
            else -> throw IOException("Повреждена запись установки обновления")
        }
    }

    /** Запись до commit установщика: при ошибке хранилища установка не начинается. */
    suspend fun setUpdateInstallation(value: UpdateInstallation?) {
        store.edit { preferences ->
            if (value == null) {
                preferences.remove(UPDATE_SESSION)
                preferences.remove(UPDATE_TARGET)
                preferences.remove(UPDATE_COMMITTED)
            } else {
                require(value.id >= 0 && SemVer.parse(value.version) != null)
                preferences[UPDATE_SESSION] = value.id
                preferences[UPDATE_TARGET] = value.version
                preferences[UPDATE_COMMITTED] = value.committed
            }
        }
    }

    /**
     * Максимальная принятая ревизия и SHA-256 точных подписанных байтов — отдельно для каждой версии/канала. В резервную
     * копию не идет и при выходе из аккаунта не стирается. Сбой записи запрещает обновление, а не работу VPN.
     */
    suspend fun rememberUpdatePolicy(policy: UpdatePolicy): Boolean = updatePolicyLock.withLock {
        val name = "update_policy_${policy.channel}_${policy.version}"
        val pending = pendingUpdatePolicies[name]
        if (pending == null || policy.revision > pending.revision) {
            pendingUpdatePolicies[name] = policy
        }
        var accepted = false
        store.edit { preferences ->
            for ((entry, checkpoint) in pendingUpdatePolicies) {
                val key = stringPreferencesKey(entry)
                val saved = preferences[key]
                val parts = saved?.split(':')
                val revision = parts?.getOrNull(0)?.toLongOrNull()
                val hash = parts?.getOrNull(1)
                if (saved != null && (parts?.size != 2 || revision == null || revision !in 0..UpdatePolicy.MAX_REVISION ||
                        hash == null || !Regex("[0-9a-f]{64}").matches(hash))) {
                    throw IOException("Повреждена история политик обновления")
                }

                if (revision == null || checkpoint.revision > revision) {
                    preferences[key] = "${checkpoint.revision}:${checkpoint.sha256}"
                }
            }
            accepted = preferences[stringPreferencesKey(name)] == "${policy.revision}:${policy.sha256}"
        }
        // Только после успешного commit: ошибка/отмена записи оставляет максимум в памяти для следующей попытки.
        pendingUpdatePolicies.clear()
        accepted
    }

    /** Номер этой установки 0–99 для частичной раскатки обновлений (как у службы десктопа): создается один раз. */
    suspend fun updateBucket(): Int {
        var bucket = 0
        store.edit { preferences ->
            bucket = preferences[UPDATE_BUCKET]?.takeIf { it in 0..99 } ?: Random.nextInt(100).also { preferences[UPDATE_BUCKET] = it }
        }
        return bucket
    }

    /**
     * Настройки для резервной копии (решение 559): значения ключей строками, имена — как у iOS. Без выбранного сервера (он
     * сменится вместе со списком), без того, что Android сообщил о системе, и без флага первого запуска.
     */
    suspend fun export(): Map<String, String> {
        val preferences = store.data.first()
        return BACKED_UP.mapNotNull { key -> preferences[key]?.let { key.name to it.toString() } }.toMap()
    }

    /** Вернуть настройки из копии: известные ключи с верным типом; чужие и испорченные значения пропускаются. */
    suspend fun import(values: Map<String, String>) {
        // Копия iPhone пишет значения списков со строчной буквы — привести к именам Android (DECISIONS.md, п. 3).
        val known = BackupSettings.normalize(values)
        store.edit { preferences ->
            for (key in BACKED_UP) {
                val text = known[key.name] ?: continue
                @Suppress("UNCHECKED_CAST")
                when {
                    key in BOOLEANS -> text.toBooleanStrictOrNull()?.let { preferences[key as Preferences.Key<Boolean>] = it }
                    key in INTS -> text.toIntOrNull()?.let { preferences[key as Preferences.Key<Int>] = it }
                    else -> preferences[key as Preferences.Key<String>] = text
                }
            }
        }
    }

    private companion object {
        val SELECTED = stringPreferencesKey("selected_server")
        val SUBS_REFRESH = stringPreferencesKey("subs_refresh")
        val SUBS_ONLY_VPN = booleanPreferencesKey("subs_only_vpn")
        val SUBS_NOTIFY = booleanPreferencesKey("subs_notify_selected")
        val DNS = stringPreferencesKey("tunnel_dns")
        val CUSTOM_DNS = stringPreferencesKey("tunnel_custom_dns")
        val IPV6 = booleanPreferencesKey("tunnel_ipv6")
        val ALLOW_LAN = booleanPreferencesKey("tunnel_allow_lan")
        val CORE_LOG = stringPreferencesKey("core_log")
        val MTU = intPreferencesKey("tunnel_mtu")
        val STRICT_ROUTES = booleanPreferencesKey("tunnel_strict_routes")
        val SNIFFING = booleanPreferencesKey("tunnel_sniffing")
        val FINGERPRINT = stringPreferencesKey("conn_fingerprint")
        val MUX = booleanPreferencesKey("conn_mux")
        val TLS_FRAGMENT = booleanPreferencesKey("conn_tls_fragment")
        val CONNECT_TIMEOUT = intPreferencesKey("conn_timeout")
        val ROUTING_MODE = stringPreferencesKey("routing_mode")
        val ROUTING_APPS = stringPreferencesKey("routing_apps")
        val ROUTING_RULES = stringPreferencesKey("routing_rules")
        val NOTIFY_LEVEL = stringPreferencesKey("notify_level")
        val RECONNECT = stringPreferencesKey("reconnect_policy")
        val CONNECT_ON_LAUNCH = booleanPreferencesKey("connect_on_launch")
        val SYSTEM_ALWAYS_ON = booleanPreferencesKey("system_always_on")
        val SYSTEM_LOCKDOWN = booleanPreferencesKey("system_lockdown")
        val ONBOARDING_DONE = booleanPreferencesKey("onboarding_done")
        val UPDATE_CHECKED = longPreferencesKey("update_checked_at")
        val UPDATE_FOUND = stringPreferencesKey("update_found")
        val UPDATE_BUCKET = intPreferencesKey("update_bucket")
        val UPDATE_SESSION = intPreferencesKey("update_session")
        val UPDATE_TARGET = stringPreferencesKey("update_target")
        val UPDATE_COMMITTED = booleanPreferencesKey("update_committed")

        private val BOOLEANS: Set<Preferences.Key<*>> = setOf(SUBS_ONLY_VPN, SUBS_NOTIFY, IPV6, ALLOW_LAN, STRICT_ROUTES, SNIFFING, MUX, TLS_FRAGMENT, CONNECT_ON_LAUNCH)
        private val INTS: Set<Preferences.Key<*>> = setOf(MTU, CONNECT_TIMEOUT)

        /** Что идет в резервную копию — настройки пользователя, а не состояние телефона. */
        val BACKED_UP: List<Preferences.Key<*>> = listOf(
            SUBS_REFRESH, SUBS_ONLY_VPN, SUBS_NOTIFY, DNS, CUSTOM_DNS, IPV6, ALLOW_LAN, CORE_LOG, MTU, STRICT_ROUTES, SNIFFING,
            FINGERPRINT, MUX, TLS_FRAGMENT, CONNECT_TIMEOUT, ROUTING_MODE, ROUTING_APPS, ROUTING_RULES, NOTIFY_LEVEL, RECONNECT,
            CONNECT_ON_LAUNCH,
        )
    }
}

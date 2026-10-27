package com.privategateway.client.data

import androidx.datastore.core.DataStore
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.booleanPreferencesKey
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.stringPreferencesKey
import com.privategateway.client.core.notify.NotificationLevel
import com.privategateway.client.core.settings.DnsChoice
import com.privategateway.client.core.settings.SubscriptionSettings
import com.privategateway.client.core.settings.TunnelSettings
import com.privategateway.client.core.vpn.ReconnectPolicy
import com.privategateway.client.core.vpn.SystemVpn
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.map
import javax.inject.Inject
import javax.inject.Singleton

/**
 * Настройки приложения (DataStore): выбранный сервер (меняется только нажатием пользователя), настройки туннеля — DNS,
 * IPv6 через сервер, локальная сеть мимо туннеля, — подписок, уведомлений и переподключения. Неизвестное значение (из
 * будущей версии) — значение по умолчанию.
 */
@Singleton
class SettingsRepository @Inject constructor(private val store: DataStore<Preferences>) {
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

    private companion object {
        val SELECTED = stringPreferencesKey("selected_server")
        val SUBS_REFRESH = stringPreferencesKey("subs_refresh")
        val SUBS_ONLY_VPN = booleanPreferencesKey("subs_only_vpn")
        val SUBS_NOTIFY = booleanPreferencesKey("subs_notify_selected")
        val DNS = stringPreferencesKey("tunnel_dns")
        val CUSTOM_DNS = stringPreferencesKey("tunnel_custom_dns")
        val IPV6 = booleanPreferencesKey("tunnel_ipv6")
        val ALLOW_LAN = booleanPreferencesKey("tunnel_allow_lan")
        val NOTIFY_LEVEL = stringPreferencesKey("notify_level")
        val RECONNECT = stringPreferencesKey("reconnect_policy")
        val SYSTEM_ALWAYS_ON = booleanPreferencesKey("system_always_on")
        val SYSTEM_LOCKDOWN = booleanPreferencesKey("system_lockdown")
    }
}

package com.privategateway.client.data

import androidx.datastore.preferences.core.PreferenceDataStoreFactory
import androidx.datastore.core.DataStore
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.intPreferencesKey
import androidx.datastore.preferences.core.stringPreferencesKey
import com.privategateway.client.core.settings.BypassApp
import com.privategateway.client.core.settings.ConnectionSettings
import com.privategateway.client.core.settings.CoreLogLevel
import com.privategateway.client.core.settings.DnsChoice
import com.privategateway.client.core.settings.RoutingMode
import com.privategateway.client.core.settings.RoutingSettings
import com.privategateway.client.core.settings.RuleAction
import com.privategateway.client.core.settings.SiteRule
import com.privategateway.client.core.settings.SubscriptionSettings
import com.privategateway.client.core.settings.TlsFingerprint
import com.privategateway.client.core.settings.TunnelSettings
import com.privategateway.client.core.subscriptions.RefreshInterval
import com.privategateway.client.core.updates.UpdatePolicy
import com.privategateway.client.core.vpn.SystemVpn
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.runBlocking
import java.io.IOException
import org.junit.After
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

class SettingsRepositoryTest {
    @get:Rule
    val folder = TemporaryFolder()

    private val scope = CoroutineScope(Dispatchers.IO + SupervisorJob())

    @After
    fun stop() = scope.cancel()

    @Test
    fun `selected server survives a restart and can be cleared`() = runBlocking<Unit> {
        val file = folder.root.resolve("settings.preferences_pb")
        val first = SettingsRepository(PreferenceDataStoreFactory.create(scope = scope) { file })
        assertNull(first.selectedServerId.first())
        first.select("nl")
        assertEquals("nl", first.selectedServerId.first())
        first.select(null)
        assertNull(first.selectedServerId.first())
        first.select("fi")
        scope.cancel()
        scope.coroutineContext[Job]?.join()

        // Новый процесс — новый DataStore на том же файле.
        val again = CoroutineScope(Dispatchers.IO + SupervisorJob())
        try {
            assertEquals("fi", SettingsRepository(PreferenceDataStoreFactory.create(scope = again) { file }).selectedServerId.first())
        } finally {
            again.cancel()
        }
    }

    @Test
    fun `android vpn flags are remembered and android 9 keeps the previous ones`() = runBlocking<Unit> {
        val settings = SettingsRepository(PreferenceDataStoreFactory.create(scope = scope) { folder.root.resolve("settings.preferences_pb") })
        assertEquals(SystemVpn(), settings.systemVpn.first(), "еще не подключались — неизвестно")

        settings.setSystemVpn(SystemVpn(alwaysOn = true, lockdown = true))
        assertEquals(SystemVpn(alwaysOn = true, lockdown = true), settings.systemVpn.first())

        settings.setSystemVpn(SystemVpn())
        assertEquals(SystemVpn(alwaysOn = true, lockdown = true), settings.systemVpn.first(), "Android 9 не сообщает — прежнее остается")

        settings.setSystemVpn(SystemVpn(alwaysOn = false, lockdown = false))
        assertEquals(SystemVpn(alwaysOn = false, lockdown = false), settings.systemVpn.first())
    }

    @Test
    fun `tunnel settings default to the protected choice and come back as saved`() = runBlocking<Unit> {
        val file = folder.root.resolve("settings.preferences_pb")
        val store = PreferenceDataStoreFactory.create(scope = scope) { file }
        val settings = SettingsRepository(store)
        assertEquals(TunnelSettings(), settings.tunnel.first(), "по умолчанию: DNS по DoH, IPv6 закрыт, локальная сеть закрыта")

        val custom = TunnelSettings(DnsChoice.Custom, "https://192.0.2.53/dns-query", ipv6 = true, allowLan = true)
        settings.setTunnel(custom)
        assertEquals(custom, settings.tunnel.first())
        assertEquals("https://192.0.2.53/dns-query", settings.tunnel.first().dnsServer)

        // Свой DNS стерт — ключа нет, а не пустая строка.
        settings.setTunnel(TunnelSettings(DnsChoice.Quad9Doh, "  "))
        assertEquals(TunnelSettings(DnsChoice.Quad9Doh), settings.tunnel.first())

        // Значение из будущей версии — по умолчанию, а не авария.
        store.edit { it[stringPreferencesKey("tunnel_dns")] = "SomethingNew" }
        assertEquals(DnsChoice.Profile, settings.tunnel.first().dns)
    }

    @Test
    fun `connection and routing settings come back as saved and survive a restart`() = runBlocking<Unit> {
        val file = folder.root.resolve("settings.preferences_pb")
        val settings = SettingsRepository(PreferenceDataStoreFactory.create(scope = scope) { file })
        assertEquals(ConnectionSettings(), settings.tunnel.first().connection, "по умолчанию — как у десктопа")
        assertEquals(RoutingSettings(), settings.tunnel.first().routing, "по умолчанию весь трафик через VPN")

        val chosen = TunnelSettings(
            DnsChoice.Quad9Doh,
            connection = ConnectionSettings(CoreLogLevel.Info, 1420, false, false, TlsFingerprint.Chrome, true, true, 30),
            routing = RoutingSettings(
                RoutingMode.Split,
                listOf(BypassApp("org.example.game", "Игра"), BypassApp("org.example.call", "Звонки", enabled = false)),
                listOf(SiteRule("example.com", RuleAction.Direct), SiteRule("203.0.113.0/24", RuleAction.Block)),
                blockOthers = true,
            ),
        )
        settings.setTunnel(chosen)
        assertEquals(chosen, settings.tunnel.first())
        scope.cancel()
        scope.coroutineContext[Job]?.join()

        // Новый процесс — новый DataStore на том же файле; «Авто» у MTU снова — ключ убран.
        val again = CoroutineScope(Dispatchers.IO + SupervisorJob())
        try {
            val reopened = SettingsRepository(PreferenceDataStoreFactory.create(scope = again) { file })
            assertEquals(chosen, reopened.tunnel.first())
            reopened.setTunnel(chosen.copy(connection = chosen.connection.copy(mtu = null)))
            assertEquals(null, reopened.tunnel.first().connection.mtu)
        } finally {
            again.cancel()
        }
    }

    @Test
    fun `broken or future connection and routing values fall back to the defaults`() = runBlocking<Unit> {
        val store = PreferenceDataStoreFactory.create(scope = scope) { folder.root.resolve("settings.preferences_pb") }
        val settings = SettingsRepository(store)
        store.edit {
            it[stringPreferencesKey("core_log")] = "Trace"
            it[intPreferencesKey("tunnel_mtu")] = 9000
            it[stringPreferencesKey("conn_fingerprint")] = "Edge"
            it[intPreferencesKey("conn_timeout")] = 0
            it[stringPreferencesKey("routing_mode")] = "Everything"
            it[stringPreferencesKey("routing_apps")] = "not json"
            it[stringPreferencesKey("routing_rules")] = """[{"pattern":"geosite:google","action":"Block"}]"""
        }
        assertEquals(TunnelSettings(), settings.tunnel.first(), "ни аварии, ни MTU, который режет пакеты, ни правила, которое ядро не примет")
    }

    @Test
    fun `subscription settings default to every 12 hours only via vpn and come back as saved`() = runBlocking<Unit> {
        val file = folder.root.resolve("settings.preferences_pb")
        val store = PreferenceDataStoreFactory.create(scope = scope) { file }
        val settings = SettingsRepository(store)
        assertEquals(SubscriptionSettings(RefreshInterval.Every12Hours, onlyViaVpn = true, notifySelected = true), settings.subscriptions.first())

        val changed = SubscriptionSettings(RefreshInterval.Off, onlyViaVpn = false, notifySelected = false)
        settings.setSubscriptions(changed)
        assertEquals(changed, settings.subscriptions.first())

        store.edit { it[stringPreferencesKey("subs_refresh")] = "Every48Hours" }
        assertEquals(RefreshInterval.Every12Hours, settings.subscriptions.first().refresh, "значение из будущей версии — по умолчанию")
    }

    @Test
    fun `update check memo and install number are phone state, not a backed up setting`() = runBlocking<Unit> {
        val settings = SettingsRepository(PreferenceDataStoreFactory.create(scope = scope) { folder.root.resolve("settings.preferences_pb") })
        assertEquals(UpdateMemo(), settings.updateMemo.first(), "еще не проверяли")
        settings.setUpdateMemo(1_000, "0.3.0")
        assertEquals(UpdateMemo(1_000, "0.3.0"), settings.updateMemo.first())
        settings.setUpdateMemo(2_000, null)
        assertEquals(UpdateMemo(2_000, null), settings.updateMemo.first(), "новее нет — найденная версия стерта")
        settings.clearUpdateMemo()
        assertEquals(UpdateMemo(), settings.updateMemo.first(), "неудача не сохраняет старый успешный ответ")

        val bucket = settings.updateBucket()
        assertTrue(bucket in 0..99, "номер установки $bucket")
        assertEquals(bucket, settings.updateBucket(), "номер установки создается один раз")
        assertEquals(emptyMap<String, String>(), settings.export().filterKeys { it.startsWith("update") }, "в резервную копию не идет")
    }

    @Test
    fun `installation and highest policy revision survive a real datastore restart`() = runBlocking<Unit> {
        val file = folder.root.resolve("updates.preferences_pb")
        val first = SettingsRepository(PreferenceDataStoreFactory.create(scope = scope) { file })
        val policy = UpdatePolicy("stable", "0.3.0", 2, "ab".repeat(32))
        assertTrue(first.rememberUpdatePolicy(policy))
        assertTrue(first.rememberUpdatePolicy(policy))
        first.setUpdateInstallation(UpdateInstallation(73, "0.3.0"))
        assertTrue(first.export().keys.none { it.startsWith("update") })
        scope.cancel()
        scope.coroutineContext[Job]?.join()

        val again = CoroutineScope(Dispatchers.IO + SupervisorJob())
        try {
            val settings = SettingsRepository(PreferenceDataStoreFactory.create(scope = again) { file })
            assertEquals(UpdateInstallation(73, "0.3.0"), settings.updateInstallation.first())
            assertFalse(settings.rememberUpdatePolicy(policy.copy(revision = 1)))
            assertFalse(settings.rememberUpdatePolicy(policy.copy(sha256 = "cd".repeat(32))))
            assertTrue(settings.rememberUpdatePolicy(policy.copy(revision = 3, sha256 = "cd".repeat(32))))
            assertTrue(settings.rememberUpdatePolicy(policy.copy(version = "0.2.9", revision = 0)), "история отдельная для каждой версии")
            settings.setUpdateInstallation(null)
            assertNull(settings.updateInstallation.first())
            assertFalse(settings.rememberUpdatePolicy(policy), "очистка установки не стирает историю раскатки")
        } finally {
            again.cancel()
            again.coroutineContext[Job]?.join()
        }
    }

    @Test
    fun `corrupt policy history is an update failure instead of forgetting a previously seen stop`() = runBlocking<Unit> {
        val store = PreferenceDataStoreFactory.create(scope = scope) { folder.root.resolve("updates.preferences_pb") }
        val settings = SettingsRepository(store)
        store.edit { it[stringPreferencesKey("update_policy_stable_0.3.0")] = "сломанная запись" }
        assertFailsWith<IOException> { settings.rememberUpdatePolicy(UpdatePolicy("stable", "0.3.0", 1, "ab".repeat(32))) }
    }

    @Test
    fun `a seen stop survives one failed write in memory and is flushed before accepting another feed`() = runBlocking<Unit> {
        val actual = PreferenceDataStoreFactory.create(scope = scope) { folder.root.resolve("updates.preferences_pb") }
        var fail = false
        val faulty = object : DataStore<Preferences> {
            override val data: Flow<Preferences> get() = actual.data
            override suspend fun updateData(transform: suspend (t: Preferences) -> Preferences): Preferences {
                if (fail) {
                    fail = false
                    throw IOException("временная ошибка записи")
                }
                return actual.updateData(transform)
            }
        }
        val settings = SettingsRepository(faulty)
        val old = UpdatePolicy("stable", "0.3.0", 1, "ab".repeat(32))
        val stop = old.copy(revision = 2, sha256 = "cd".repeat(32))
        assertTrue(settings.rememberUpdatePolicy(old))
        fail = true
        assertFailsWith<IOException> { settings.rememberUpdatePolicy(stop) }
        assertFalse(settings.rememberUpdatePolicy(old), "следующая старая лента не забывает уже увиденный стоп")
        assertFalse(SettingsRepository(actual).rememberUpdatePolicy(old), "успешная повторная запись сохранила стоп и для нового экземпляра")
        assertTrue(settings.rememberUpdatePolicy(stop.copy(revision = 3)), "новая подписанная ревизия может возобновить выпуск")
    }

    @Test
    fun `connect on launch is off by default and comes back as saved`() = runBlocking<Unit> {
        val settings = SettingsRepository(PreferenceDataStoreFactory.create(scope = scope) { folder.root.resolve("settings.preferences_pb") })
        assertEquals(false, settings.connectOnLaunch.first(), "как у десктопа: сервер подключает пользователь")

        settings.setConnectOnLaunch(true)
        assertEquals(true, settings.connectOnLaunch.first())
        settings.setConnectOnLaunch(false)
        assertEquals(false, settings.connectOnLaunch.first())
    }
    @Test
    fun `new sign-in waits for a manual connect even when Android requests an automatic start`() = runBlocking<Unit> {
        val file = folder.root.resolve("settings.preferences_pb")
        val store = PreferenceDataStoreFactory.create(scope = scope) { file }
        val settings = SettingsRepository(store)
        assertTrue(settings.permitConnection(manual = false), "существующий авторизованный сеанс до нового флага сохраняет перезапуск")
        settings.resetAccountNetwork()
        assertFalse(settings.permitConnection(manual = false), "Always-on и перезапуск ОС не подтверждают намерение пользователя")
        scope.cancel()
        scope.coroutineContext[Job]?.join()
        val again = CoroutineScope(Dispatchers.IO + SupervisorJob())
        try {
            val reopened = SettingsRepository(PreferenceDataStoreFactory.create(scope = again) { file })
            assertFalse(reopened.permitConnection(manual = false), "после перезапуска ожидание ручного действия прочитано с диска")
            assertTrue(reopened.permitConnection(manual = true))
            assertTrue(reopened.permitConnection(manual = false), "после ручного включения обычный обрыв и перезапуск восстанавливаются")
            reopened.resetAccountNetwork()
            assertFalse(reopened.permitConnection(manual = false), "следующий вход снова требует явного включения")
        } finally {
            again.cancel()
            again.coroutineContext[Job]?.join()
        }
    }

    @Test
    fun `backup cannot arm automatic connection after a new sign-in`() = runBlocking<Unit> {
        val settings = SettingsRepository(PreferenceDataStoreFactory.create(scope = scope) { folder.root.resolve("settings.preferences_pb") })
        settings.resetAccountNetwork()
        settings.import(mapOf("account_manual_connect_required" to "false", "connect_on_launch" to "true"))
        assertFalse(settings.permitConnection(manual = false), "копия настроек не заменяет новое явное подключение")
        assertFalse("account_manual_connect_required" in settings.export(), "право текущего входа не переносится резервной копией")
    }

    @Test
    fun `manual connect permission is never granted if its durable write fails`() = runBlocking<Unit> {
        val real = PreferenceDataStoreFactory.create(scope = scope) { folder.root.resolve("settings.preferences_pb") }
        SettingsRepository(real).resetAccountNetwork()
        val broken = object : DataStore<Preferences> {
            override val data = real.data
            override suspend fun updateData(transform: suspend (Preferences) -> Preferences): Preferences = throw IOException("read only")
        }
        assertFailsWith<IOException> { SettingsRepository(broken).permitConnection(manual = true) }
        assertTrue(SettingsRepository(real).manualConnectRequired.first())
    }

}

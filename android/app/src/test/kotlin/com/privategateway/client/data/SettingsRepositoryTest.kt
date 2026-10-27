package com.privategateway.client.data

import androidx.datastore.preferences.core.PreferenceDataStoreFactory
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.stringPreferencesKey
import com.privategateway.client.core.settings.DnsChoice
import com.privategateway.client.core.settings.SubscriptionSettings
import com.privategateway.client.core.settings.TunnelSettings
import com.privategateway.client.core.subscriptions.RefreshInterval
import com.privategateway.client.core.vpn.SystemVpn
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import kotlin.test.assertEquals
import kotlin.test.assertNull

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
}

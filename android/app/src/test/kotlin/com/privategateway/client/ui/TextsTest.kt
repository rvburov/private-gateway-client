package com.privategateway.client.ui

import android.os.LocaleList
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.privategateway.client.R
import com.privategateway.client.core.servers.ProxyProtocol
import com.privategateway.client.core.servers.ServerItem
import com.privategateway.client.core.servers.TransportKind
import com.privategateway.client.core.servers.TransportSecurity
import com.privategateway.client.vpn.TunnelState
import com.privategateway.client.vpn.TunnelStatus
import org.junit.Test
import org.junit.runner.RunWith
import kotlin.test.assertEquals

@RunWith(AndroidJUnit4::class)
class TextsTest {
    @Test
    fun `plural forms follow the language android took the texts in`() {
        // Украинских и немецких текстов нет: Android берет следующий язык списка, у которого тексты есть.
        assertEquals("ru", Texts.language(LocaleList.forLanguageTags("uk-UA,ru-RU")))
        assertEquals("ru", Texts.language(LocaleList.forLanguageTags("de-DE,ru-RU,en-US")))
        assertEquals("en", Texts.language(LocaleList.forLanguageTags("en-GB,ru-RU")))
        assertEquals("ru", Texts.language(LocaleList.forLanguageTags("ru-RU")))
        assertEquals(R.string.import_add_few, Texts.importAdd(3, Texts.language(LocaleList.forLanguageTags("uk-UA,ru-RU"))))
    }

    @Test
    fun `power button connects, adds the first server or leads to the list`() {
        val nl = ServerItem("nl", "Нидерланды 01", "NL", ProxyProtocol.Vless, TransportSecurity.Reality, TransportKind.Tcp, 443)
        assertEquals(PowerAction.Add, MainUi().power)
        assertEquals(PowerAction.Choose, MainUi(servers = listOf(nl)).power, "серверы есть, ни один не выбран — сам не выбирается")
        assertEquals(PowerAction.Toggle, MainUi(servers = listOf(nl), selectedId = "nl").power)
        assertEquals(PowerAction.Choose, MainUi(TunnelStatus(TunnelState.Error, error = "PGC-SVC-003"), listOf(nl)).power)
        // Защита идет — щит выключает ее, даже если выбор уже снят.
        assertEquals(PowerAction.Toggle, MainUi(TunnelStatus(TunnelState.Connecting), listOf(nl)).power)
        assertEquals(PowerAction.Toggle, MainUi(TunnelStatus(TunnelState.Connected, serverId = "gone")).power)
    }
}

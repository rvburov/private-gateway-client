package com.privategateway.client.ui.servers

import androidx.lifecycle.SavedStateHandle
import com.privategateway.client.core.servers.ProxyProtocol
import com.privategateway.client.core.servers.ServerListQuery
import com.privategateway.client.core.servers.ServerListTab
import com.privategateway.client.core.servers.ServerSort
import org.junit.Test
import kotlin.test.assertEquals

class ServersViewModelTest {
    @Test
    fun `query survives the window being recreated`() {
        val saved = SavedStateHandle()
        val first = ServersViewModel(saved)
        first.search("нидер")
        first.tab(ServerListTab.Recent)
        first.toggleProtocol(ProxyProtocol.Trojan)
        first.toggleProtocol(ProxyProtocol.Tuic)
        first.toggleProtocol(ProxyProtocol.Tuic)
        first.sort(ServerSort.Name)

        val expected = ServerListQuery("нидер", ServerListTab.Recent, setOf(ProxyProtocol.Trojan), ServerSort.Name)
        assertEquals(expected, first.query.value)
        assertEquals(expected, ServersViewModel(saved).query.value, "система пересоздала окно — запрос тот же")
    }

    @Test
    fun `reset restores protocols and order but keeps search and tab`() {
        val model = ServersViewModel(SavedStateHandle())
        model.search("fi")
        model.tab(ServerListTab.Favorites)
        model.toggleProtocol(ProxyProtocol.Vless)
        model.sort(ServerSort.Name)

        model.reset()
        assertEquals(ServerListQuery("fi", ServerListTab.Favorites), model.query.value)
    }

    @Test
    fun `unknown saved values fall back to defaults`() {
        val saved = SavedStateHandle(mapOf("servers.tab" to "Gone", "servers.sort" to "Gone", "servers.hidden" to arrayListOf("Gone", "Vmess")))
        assertEquals(ServerListQuery(hiddenProtocols = setOf(ProxyProtocol.Vmess)), ServersViewModel(saved).query.value)
    }
}

package com.privategateway.client.ui.servers

import androidx.lifecycle.SavedStateHandle
import androidx.lifecycle.ViewModel
import com.privategateway.client.core.servers.ProxyProtocol
import com.privategateway.client.core.servers.ServerListQuery
import com.privategateway.client.core.servers.ServerListTab
import com.privategateway.client.core.servers.ServerSort
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import javax.inject.Inject

/**
 * Поиск, вкладка, скрытые протоколы и порядок списка серверов. Живут, пока живет окно, и переживают его пересоздание
 * системой (SavedStateHandle); в настройки не пишутся — как у десктопа.
 */
@HiltViewModel
class ServersViewModel @Inject constructor(private val saved: SavedStateHandle) : ViewModel() {
    private val current = MutableStateFlow(read())
    val query: StateFlow<ServerListQuery> = current.asStateFlow()

    fun search(text: String) = change { it.copy(search = text) }

    fun tab(tab: ServerListTab) = change { it.copy(tab = tab) }

    fun toggleProtocol(protocol: ProxyProtocol) = change {
        it.copy(hiddenProtocols = if (protocol in it.hiddenProtocols) it.hiddenProtocols - protocol else it.hiddenProtocols + protocol)
    }

    fun sort(sort: ServerSort) = change { it.copy(sort = sort) }

    /** «Сбросить» фильтра: все протоколы и порядок по задержке; поиск и вкладка остаются. */
    fun reset() = change { it.copy(hiddenProtocols = emptySet(), sort = ServerSort.Latency) }

    private fun change(update: (ServerListQuery) -> ServerListQuery) {
        val next = update(current.value)
        current.value = next
        saved[SEARCH] = next.search
        saved[TAB] = next.tab.name
        saved[HIDDEN] = ArrayList(next.hiddenProtocols.map { it.name })
        saved[SORT] = next.sort.name
    }

    private fun read() = ServerListQuery(
        search = saved.get<String>(SEARCH) ?: "",
        tab = ServerListTab.entries.firstOrNull { it.name == saved.get<String>(TAB) } ?: ServerListTab.All,
        hiddenProtocols = saved.get<ArrayList<String>>(HIDDEN).orEmpty().mapNotNull { name -> ProxyProtocol.entries.firstOrNull { it.name == name } }.toSet(),
        sort = ServerSort.entries.firstOrNull { it.name == saved.get<String>(SORT) } ?: ServerSort.Latency,
    )

    private companion object {
        const val SEARCH = "servers.search"
        const val TAB = "servers.tab"
        const val HIDDEN = "servers.hidden"
        const val SORT = "servers.sort"
    }
}

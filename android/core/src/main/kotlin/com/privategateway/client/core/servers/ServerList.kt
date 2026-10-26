package com.privategateway.client.core.servers

import java.text.Collator
import java.util.Locale

/** Вкладки списка серверов. */
enum class ServerListTab {
    All,
    Favorites,
    Recent,
}

/** Порядок строк в списке серверов. */
enum class ServerSort {
    /** Сначала самые быстрые; без ответа и не измеренные — в конце. */
    Latency,

    Name,
}

/** Что показать в списке серверов: поиск, вкладка, скрытые протоколы и порядок. */
data class ServerListQuery(
    val search: String = "",
    val tab: ServerListTab = ServerListTab.All,
    val hiddenProtocols: Set<ProxyProtocol> = emptySet(),
    val sort: ServerSort = ServerSort.Latency,
)

/** Подписка для заголовка группы: «{name} · {n}», «обновлено HH:mm» ([updatedAt], мс от эпохи; null — ни разу). */
data class SubscriptionRef(val id: String, val name: String, val updatedAt: Long?)

/** Группа строк под одним заголовком: подписка или «Мои серверы» (subscription = null). */
data class ServerGroup(val subscription: SubscriptionRef?, val servers: List<ServerItem>)

/** Почему список пуст: серверов нет вовсе, нет избранных, ни разу не выбирали или ничего не нашлось. */
enum class ServerListEmpty {
    NoServers,
    NoFavorites,
    NoRecent,
    NothingFound,
}

/**
 * Поиск, вкладки, фильтр протоколов и сортировка списка серверов (доска 2) — `ServerList` десктопа. Только отбирает и
 * упорядочивает — сам выбранный сервер не меняет. Отличия Android: поиск не видит адреса — он запечатан (решение 420);
 * «Недавние» — в порядке выбора, свежие сверху, сортировка их не трогает (решение 421; так теперь и у десктопа).
 */
object ServerList {
    /** Сколько последних выбранных серверов показывать во вкладке «Недавние». */
    const val RECENT_LIMIT = 10

    /** Порядок протоколов в фильтре: сначала основные, как в макете. */
    val protocolOrder: List<ProxyProtocol> = listOf(
        ProxyProtocol.Vless,
        ProxyProtocol.Vmess,
        ProxyProtocol.Hysteria2,
        ProxyProtocol.Tuic,
        ProxyProtocol.WireGuard,
        ProxyProtocol.Trojan,
        ProxyProtocol.Shadowsocks,
    )

    /** Протоколы, которые есть среди серверов, — только они попадают в фильтр. */
    fun protocolsIn(servers: List<ServerItem>): List<ProxyProtocol> {
        val present = servers.mapTo(HashSet()) { it.protocol }
        return protocolOrder.filter { it in present }
    }

    /** Число на кнопке фильтра: скрытые протоколы из тех, что есть в списке, и порядок не по задержке. */
    fun filterCount(servers: List<ServerItem>, query: ServerListQuery): Int =
        protocolsIn(servers).count { it in query.hiddenProtocols } + if (query.sort == ServerSort.Latency) 0 else 1

    /**
     * Серверы вкладки по запросу, разложенные по группам. На вкладке «Все» — по подпискам в их порядке, серверы без
     * подписки (или из неизвестной) последней группой; на остальных — одна группа.
     *
     * @param locale язык интерфейса: по нему сравниваются названия, без учета регистра.
     */
    fun build(servers: List<ServerItem>, subscriptions: List<SubscriptionRef>, query: ServerListQuery, locale: Locale): List<ServerGroup> {
        val rows = filter(servers, query)
        val order = order(query.sort, locale)
        if (query.tab != ServerListTab.All) {
            val shown = if (query.tab == ServerListTab.Recent) rows else rows.sortedWith(order)
            return if (shown.isEmpty()) emptyList() else listOf(ServerGroup(null, shown))
        }

        val groups = ArrayList<ServerGroup>()
        for (subscription in subscriptions) {
            val own = rows.filter { it.subscriptionId == subscription.id }
            if (own.isNotEmpty()) {
                groups.add(ServerGroup(subscription, own.sortedWith(order)))
            }
        }

        // Без подписки или из удаленной — в «Мои серверы».
        val known = subscriptions.mapTo(HashSet()) { it.id }
        val manual = rows.filter { it.subscriptionId !in known }
        if (manual.isNotEmpty()) {
            groups.add(ServerGroup(null, manual.sortedWith(order)))
        }

        return groups
    }

    /** Подходит ли сервер под строку поиска: название, код страны, протокол. */
    fun matches(server: ServerItem, search: String): Boolean {
        val text = search.trim()
        if (text.isEmpty()) {
            return true
        }

        return listOf(server.displayName, server.countryCode, ServerFacts.protocolName(server.protocol))
            .any { it.contains(text, ignoreCase = true) }
    }

    /**
     * Чем объяснить пустой список; null — список не пуст. «Нет избранных» и «не выбирали» — только когда поиск и фильтр
     * ничего не скрывают; порядок по названию строк не скрывает (решение 422).
     */
    fun empty(servers: List<ServerItem>, groups: List<ServerGroup>, query: ServerListQuery): ServerListEmpty? = when {
        groups.isNotEmpty() -> null
        servers.isEmpty() -> ServerListEmpty.NoServers
        query.search.isNotBlank() || protocolsIn(servers).any { it in query.hiddenProtocols } -> ServerListEmpty.NothingFound
        query.tab == ServerListTab.Favorites -> ServerListEmpty.NoFavorites
        query.tab == ServerListTab.Recent -> ServerListEmpty.NoRecent
        else -> ServerListEmpty.NothingFound
    }

    private fun filter(servers: List<ServerItem>, query: ServerListQuery): List<ServerItem> {
        val rows = servers.filter { it.protocol !in query.hiddenProtocols && matches(it, query.search) }
        return when (query.tab) {
            ServerListTab.All -> rows
            ServerListTab.Favorites -> rows.filter { it.favorite }
            ServerListTab.Recent -> rows.filter { it.lastUsedAt != null }.sortedByDescending { it.lastUsedAt }.take(RECENT_LIMIT)
        }
    }

    private fun order(sort: ServerSort, locale: Locale): Comparator<ServerItem> {
        // Collator — по алфавиту языка и без учета регистра: «бета» между «Альфа» и «Яблоко», «е» с точками — рядом с «е».
        val collator = Collator.getInstance(locale).apply { strength = Collator.SECONDARY }
        val byName = compareBy<ServerItem, String>(collator) { it.displayName }
        return when (sort) {
            ServerSort.Name -> byName
            ServerSort.Latency -> compareBy<ServerItem> { it.latency?.ms == null }.thenBy { it.latency?.ms }.then(byName)
        }
    }
}

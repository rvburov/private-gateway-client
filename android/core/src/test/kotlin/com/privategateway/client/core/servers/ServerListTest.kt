package com.privategateway.client.core.servers

import org.junit.jupiter.api.Test
import org.junit.jupiter.params.ParameterizedTest
import org.junit.jupiter.params.provider.CsvSource
import org.junit.jupiter.params.provider.ValueSource
import java.time.Instant
import java.util.Locale
import java.util.Random
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

// Случаи десктопа (ServerListTests) и отличия Android: «Недавние» — в порядке выбора, адрес не ищется.
class ServerListTest {
    private val ru = Locale.forLanguageTag("ru-RU")
    private val now = Instant.parse("2026-10-24T15:42:00Z").toEpochMilli()
    private val minute = 60_000L

    private val main = SubscriptionRef("main", "Основная подписка", now)
    private val spare = SubscriptionRef("spare", "Резервная", null)

    private val nl = ServerItem(
        "nl", "Нидерланды 01", "NL", ProxyProtocol.Vless, TransportSecurity.Reality, TransportKind.Tcp, 443,
        subscriptionId = "main", favorite = true, latency = Latency.of(42), lastUsedAt = now,
    )
    private val fi = ServerItem(
        "fi", "Финляндия 01", "FI", ProxyProtocol.Hysteria2, TransportSecurity.Tls, TransportKind.Quic, 8443,
        subscriptionId = "main", favorite = true, latency = Latency.of(38),
    )
    private val de = ServerItem(
        "de", "Германия 02", "DE", ProxyProtocol.Vless, TransportSecurity.Tls, TransportKind.Xhttp, 443,
        subscriptionId = "spare", latency = Latency.of(51), lastUsedAt = now - 24 * 60 * minute,
    )
    private val us = ServerItem("us", "США 01", "US", ProxyProtocol.Trojan, TransportSecurity.Tls, TransportKind.WebSocket, 443, latency = Latency.of(148))
    private val tr = ServerItem(
        "tr", "Турция 01", "TR", ProxyProtocol.Vless, TransportSecurity.Tls, TransportKind.Grpc, 443,
        subscriptionId = "main", latency = Latency.failed(LatencyFailure.NoResponse),
    )

    private val all = listOf(nl, fi, de, us, tr)

    private fun build(query: ServerListQuery) = ServerList.build(all, listOf(main, spare), query, ru)

    private fun ids(groups: List<ServerGroup>) = groups.flatMap { group -> group.servers.map { it.id } }

    @Test
    fun `all tab groups by subscription in their order and manual servers last`() {
        val groups = build(ServerListQuery())

        assertEquals(listOf("main", "spare", null), groups.map { it.subscription?.id })
        assertEquals(listOf("fi", "nl", "tr"), groups[0].servers.map { it.id }, "по задержке, без ответа — в конце")
        assertEquals(listOf("de"), groups[1].servers.map { it.id })
        assertEquals(listOf("us"), groups[2].servers.map { it.id })
        assertEquals(main, groups[0].subscription, "заголовку группы — название и время обновления подписки")
        assertEquals(listOf<String?>("spare"), build(ServerListQuery(search = "германия")).map { it.subscription?.id }, "группы без строк не видны")
    }

    @Test
    fun `server of a removed subscription is shown with manual ones`() {
        val orphan = fi.copy(id = "orphan", subscriptionId = "gone")

        val groups = ServerList.build(listOf(orphan, us), listOf(main), ServerListQuery(), ru)

        assertNull(groups.single().subscription)
        assertEquals(listOf("orphan", "us"), groups[0].servers.map { it.id })
    }

    @ParameterizedTest
    @CsvSource(
        "германия, de",
        // У десктопа здесь город («ХЕЛЬСИНКИ»): на Android города нет — регистр проверяется на названии.
        "ФИНЛЯНДИЯ, fi",
        "us, us",
        "hysteria, fi",
        "'  сша  ', us",
    )
    fun `search finds by name country and protocol`(search: String, expected: String) {
        assertEquals(listOf(expected), ids(build(ServerListQuery(search = search))))
    }

    @ParameterizedTest
    @ValueSource(strings = ["203.0.113.24", "8443", "443"])
    fun `search does not look at the address`(search: String) {
        // Адрес запечатан вместе с ключом (решение 420): ни адрес, ни порт поиск не видит, у десктопа «198.51.100.2» находил США.
        assertEquals(emptyList(), build(ServerListQuery(search = search)))
    }

    @Test
    fun `renamed server is found and sorted by its own name`() {
        val work = nl.copy(customName = "Школа, работа")
        val servers = listOf(work, fi)

        assertEquals("Школа, работа", work.displayName)
        assertEquals("Нидерланды 01", nl.displayName, "без своего — название из ссылки")
        assertEquals(listOf("nl"), ids(ServerList.build(servers, emptyList(), ServerListQuery(search = "работа"), ru)))
        assertEquals(emptyList(), ServerList.build(servers, emptyList(), ServerListQuery(search = "нидерланды"), ru))
        val byName = ServerList.build(servers, emptyList(), ServerListQuery(sort = ServerSort.Name), ru)
        assertEquals(listOf("fi", "nl"), ids(byName), "«Школа» после «Финляндии», хотя «Нидерланды» — раньше")
    }

    @Test
    fun `empty search matches everything`() {
        assertTrue(ServerList.matches(nl, "   "))
        assertFalse(ServerList.matches(nl, "Польша"))
    }

    @Test
    fun `hidden protocols are filtered out`() {
        assertEquals(listOf("fi", "us"), ids(build(ServerListQuery(hiddenProtocols = setOf(ProxyProtocol.Vless)))))
    }

    @Test
    fun `sort by name follows the language alphabet`() {
        val favorites = build(ServerListQuery(tab = ServerListTab.Favorites, sort = ServerSort.Name))
        assertEquals(listOf("Нидерланды 01", "Финляндия 01"), favorites.single().servers.map { it.displayName })

        // Без учета регистра и по алфавиту языка: при сравнении по кодам «бета» встала бы после «Яблоко», а «е» с точками
        // (escape-ом: в текстах проекта этой буквы нет) — в начало, перед «А».
        val names = listOf("Яблоко", "бета", "Жук", "\u0401лка", "Альфа", "Дом", "Beta", "alpha")
        val servers = names.mapIndexed { index, name -> us.copy(id = "s$index", linkName = name) }
        val sorted = ServerList.build(servers, emptyList(), ServerListQuery(sort = ServerSort.Name), ru).single().servers
        assertEquals(listOf("alpha", "Beta", "Альфа", "бета", "Дом", "\u0401лка", "Жук", "Яблоко"), sorted.map { it.displayName })

        // Названия, которые различаются только регистром, равны — остаются в порядке списка, как у десктопа.
        val twins = listOf(us.copy(id = "upper", linkName = "США 01"), us.copy(id = "lower", linkName = "сша 01"))
        for (input in listOf(twins, twins.reversed())) {
            val rows = ServerList.build(input, emptyList(), ServerListQuery(sort = ServerSort.Name), ru).single().servers
            assertEquals(input.map { it.id }, rows.map { it.id })
        }
    }

    @Test
    fun `sort by latency puts measured first and the rest by name`() {
        val servers = listOf(
            us.copy(id = "apple", linkName = "Яблоко", latency = Latency.of(30)),
            us.copy(id = "beta", linkName = "бета", latency = null),
            us.copy(id = "alpha", linkName = "Альфа", latency = Latency.failed(LatencyFailure.NoResponse)),
            us.copy(id = "home", linkName = "Дом", latency = Latency.of(30)),
            us.copy(id = "fast", linkName = "Жук", latency = Latency.of(5)),
            us.copy(id = "slow", linkName = "Аист", latency = Latency.of(300)),
        )

        val sorted = ServerList.build(servers, emptyList(), ServerListQuery(), ru).single().servers

        assertEquals(listOf("fast", "home", "apple", "slow", "alpha", "beta"), sorted.map { it.id }, "равная задержка — по названию")
    }

    @Test
    fun `favorites tab is one group of starred servers`() {
        val groups = build(ServerListQuery(tab = ServerListTab.Favorites))

        assertNull(groups.single().subscription)
        assertEquals(listOf("fi", "nl"), groups[0].servers.map { it.id })
    }

    @ParameterizedTest
    @CsvSource("Latency", "Name")
    fun `recent tab keeps the last ten chosen servers in the order they were chosen`(sort: ServerSort) {
        val many = (0 until 15).map { i ->
            us.copy(id = "s$i", linkName = "США ${'z' - i}", latency = Latency.of(100 - i), lastUsedAt = now - i * minute)
        } + tr

        val rows = ServerList.build(many.shuffled(Random(7)), emptyList(), ServerListQuery(tab = ServerListTab.Recent, sort = sort), ru)
            .single().servers

        assertEquals(10, ServerList.RECENT_LIMIT)
        assertEquals((0 until 10).map { "s$it" }, rows.map { it.id }, "свежие сверху; задержка и название порядок не меняют (решение 419)")
    }

    @Test
    fun `chosen server appears in recent whatever the sort`() {
        // У десктопа — Choosing_a_server_puts_it_into_recent: время выбора пишет хранилище, список его только читает.
        val chosen = us.copy(lastUsedAt = now + 5 * minute)
        val rows = ServerList.build(listOf(nl, chosen, de), emptyList(), ServerListQuery(tab = ServerListTab.Recent, sort = ServerSort.Name), ru)

        assertEquals(listOf("us", "nl", "de"), ids(rows))
    }

    @Test
    fun `empty tab has no groups`() {
        assertEquals(emptyList(), ServerList.build(listOf(us), emptyList(), ServerListQuery(tab = ServerListTab.Favorites), ru))
    }

    @Test
    fun `filter lists only present protocols in mockup order`() {
        assertEquals(listOf(ProxyProtocol.Vless, ProxyProtocol.Hysteria2, ProxyProtocol.Trojan), ServerList.protocolsIn(all))
        assertEquals(ProxyProtocol.entries.toSet(), ServerList.protocolOrder.toSet(), "в порядке нет ни одного протокола")
        assertEquals(ProxyProtocol.entries.size, ServerList.protocolOrder.size)
    }

    @Test
    fun `filter badge counts hidden protocols of the list and the name sort`() {
        assertEquals(0, ServerList.filterCount(all, ServerListQuery()))
        // TUIC в списке нет — его флажка не видно, в число он не входит.
        assertEquals(1, ServerList.filterCount(all, ServerListQuery(hiddenProtocols = setOf(ProxyProtocol.Vless, ProxyProtocol.Tuic))))
        assertEquals(2, ServerList.filterCount(all, ServerListQuery(hiddenProtocols = setOf(ProxyProtocol.Vless), sort = ServerSort.Name)))
        assertEquals(1, ServerList.filterCount(all, ServerListQuery(sort = ServerSort.Name)))
    }

    @Test
    fun `empty list explains why`() {
        fun empty(servers: List<ServerItem>, query: ServerListQuery) =
            ServerList.empty(servers, ServerList.build(servers, emptyList(), query, ru), query)

        assertNull(empty(all, ServerListQuery()))
        assertEquals(ServerListEmpty.NoServers, empty(emptyList(), ServerListQuery(tab = ServerListTab.Favorites)))
        assertEquals(ServerListEmpty.NoFavorites, empty(listOf(us), ServerListQuery(tab = ServerListTab.Favorites)))
        val favoritesByName = ServerListQuery(tab = ServerListTab.Favorites, sort = ServerSort.Name)
        assertEquals(ServerListEmpty.NoFavorites, empty(listOf(us), favoritesByName), "порядок строк ничего не скрывает")
        assertEquals(ServerListEmpty.NoRecent, empty(listOf(us), ServerListQuery(tab = ServerListTab.Recent)))
        assertEquals(ServerListEmpty.NothingFound, empty(all, ServerListQuery(search = "Польша")))
        assertEquals(ServerListEmpty.NothingFound, empty(all, ServerListQuery(tab = ServerListTab.Favorites, search = "США")))
        val vlessHidden = ServerListQuery(tab = ServerListTab.Recent, hiddenProtocols = setOf(ProxyProtocol.Vless))
        assertEquals(ServerListEmpty.NothingFound, empty(listOf(nl, us), vlessHidden), "выбранный раньше скрыт фильтром")
    }
}

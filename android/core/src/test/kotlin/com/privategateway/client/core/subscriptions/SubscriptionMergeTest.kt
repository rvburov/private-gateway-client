package com.privategateway.client.core.subscriptions

import com.privategateway.client.core.links.LinkParser
import com.privategateway.client.core.servers.ServerDefinition
import com.privategateway.client.core.servers.ServerKeys
import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNull

// Случаи десктопа (SubscriptionUpdateTests): что остается, что добавляется и удаляется, что с выбранным.
class SubscriptionMergeTest {
    private fun server(link: String): ServerDefinition = LinkParser.parse(link).server!!

    private fun stored(vararg links: String) = links.mapIndexed { index, link -> StoredServer("s$index", server(link)) }

    @Test
    fun `update keeps known servers adds new and removes gone`() {
        val own = stored("trojan://pw-a@198.51.100.1:443#A", "trojan://pw-b@198.51.100.2:443#B", "trojan://pw-c@198.51.100.3:443#C")
        val result = SubscriptionMerge.merge(
            own,
            listOf(
                server("trojan://pw-a@198.51.100.1:443#A"),
                // Поставщик поправил SNI и название: тот же выход — тот же сервер, номер (избранное, свое название) остается.
                server("trojan://pw-b@198.51.100.2:443?sni=example.org#B2"),
                server("trojan://pw-d@198.51.100.4:443#D"),
            ),
            elsewhere = emptySet(),
            selectedId = null,
        )

        assertEquals(listOf("s0", "s1"), result.kept.map { it.first })
        assertEquals("example.org", result.kept.single { it.first == "s1" }.second.security.sni, "описание — из новой версии подписки")
        assertEquals(listOf("D"), result.added.map { it.name })
        assertEquals(listOf("s2"), result.removed)
        assertNull(result.missing)
        assertEquals(SelectedChange.None, result.selected)
    }

    @Test
    fun `selected server that disappeared stays until another is chosen`() {
        val own = stored("trojan://pw-a@198.51.100.1:443#A", "trojan://pw-b@198.51.100.2:443#B")
        val result = SubscriptionMerge.merge(own, listOf(server("trojan://pw-b@198.51.100.2:443#B")), emptySet(), selectedId = "s0")
        assertEquals(SelectedChange.Missing, result.selected)
        assertEquals("s0", result.missing)
        assertEquals(emptyList(), result.removed, "выбранный не удаляется")

        val chosenOther = SubscriptionMerge.merge(own, listOf(server("trojan://pw-b@198.51.100.2:443#B")), emptySet(), selectedId = "s1")
        assertEquals(listOf("s0"), chosenOther.removed)
    }

    @Test
    fun `selected server with new keys is reported as changed and the new one is added next to it`() {
        val own = stored("trojan://pw-a@198.51.100.1:443#A")
        val result = SubscriptionMerge.merge(own, listOf(server("trojan://pw-new@198.51.100.1:443#A")), emptySet(), selectedId = "s0")
        assertEquals(SelectedChange.Changed, result.selected)
        assertEquals("s0", result.missing)
        assertEquals(listOf("A"), result.added.map { it.name }, "новая версия — рядом, выбирает пользователь")
    }

    @Test
    fun `servers already in another group are not duplicated`() {
        val elsewhere = setOf(ServerKeys.key(server("trojan://pw-x@198.51.100.9:443#X")))
        val result = SubscriptionMerge.merge(
            stored("trojan://pw-a@198.51.100.1:443#A"),
            listOf(server("trojan://pw-a@198.51.100.1:443#A"), server("trojan://pw-x@198.51.100.9:443#X другое имя")),
            elsewhere,
            selectedId = null,
        )
        assertEquals(emptyList(), result.added)
    }

    @Test
    fun `repeated servers in the new list count once`() {
        val result = SubscriptionMerge.merge(
            emptyList(),
            listOf(server("trojan://pw-d@198.51.100.4:443#D"), server("trojan://pw-d@198.51.100.4:443#D2")),
            emptySet(),
            selectedId = null,
        )
        assertEquals(listOf("D"), result.added.map { it.name })
    }

    @Test
    fun `empty list never replaces a subscription`() {
        assertFailsWith<IllegalArgumentException> { SubscriptionMerge.merge(stored("trojan://pw-a@198.51.100.1:443#A"), emptyList(), emptySet(), null) }
    }

    @Test
    fun `keys ignore the name and the host case but not the credentials`() {
        val a = server("trojan://pw-a@Example.COM:443#A")
        assertEquals(ServerKeys.key(a), ServerKeys.key(server("trojan://pw-a@example.com:443#Другое")))
        assertEquals(ServerKeys.exit(a), ServerKeys.exit(server("trojan://pw-a@example.com:443?sni=example.org&type=ws#A")))
        assert(ServerKeys.key(a) != ServerKeys.key(server("trojan://pw-b@example.com:443#A")))
        assert(ServerKeys.key(a) != ServerKeys.key(server("trojan://pw-a@example.com:443?sni=example.org#A")))
    }
}

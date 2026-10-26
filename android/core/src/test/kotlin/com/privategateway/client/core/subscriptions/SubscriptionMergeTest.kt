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
    fun `server that came back is no longer missing`() {
        val own = stored("trojan://pw-a@198.51.100.1:443#A", "trojan://pw-b@198.51.100.2:443#B")
        val result = SubscriptionMerge.merge(
            own,
            listOf(server("trojan://pw-a@198.51.100.1:443#A"), server("trojan://pw-b@198.51.100.2:443#B")),
            emptySet(),
            selectedId = "s0",
        )
        assertEquals(listOf("s0", "s1"), result.kept.map { it.first })
        assertNull(result.missing)
        assertEquals(SelectedChange.None, result.selected)
    }

    @Test
    fun `changed or missing is decided by the link name`() {
        val own = stored("trojan://pw-a@198.51.100.1:443#A")
        // Тот же адрес, но новые ключи и другое название — для пользователя это другой сервер.
        val renamed = SubscriptionMerge.merge(own, listOf(server("trojan://pw-new@198.51.100.1:443#A2")), emptySet(), selectedId = "s0")
        assertEquals(SelectedChange.Missing, renamed.selected)
        // Другой адрес и ключи, но то же название — та же точка у поставщика, ее новая версия.
        val moved = SubscriptionMerge.merge(own, listOf(server("trojan://pw-new@203.0.113.7:8443#A")), emptySet(), selectedId = "s0")
        assertEquals(SelectedChange.Changed, moved.selected)
    }

    @Test
    fun `unreadable servers count as gone`() {
        val result = SubscriptionMerge.merge(
            stored("trojan://pw-a@198.51.100.1:443#A"),
            listOf(server("trojan://pw-a@198.51.100.1:443#A"), server("trojan://pw-u@198.51.100.5:443#U")),
            emptySet(),
            selectedId = "s0",
            unreadable = listOf(UnreadableServer("u1", "U"), UnreadableServer("u2", "V")),
        )
        assertEquals(listOf("s0"), result.kept.map { it.first })
        assertEquals(listOf("U"), result.added.map { it.name }, "нечитаемый не узнать — его версия из подписки добавляется заново")
        assertEquals(listOf("u1", "u2"), result.removed)
        assertNull(result.missing)
        assertEquals(SelectedChange.None, result.selected)
    }

    @Test
    fun `selected unreadable server stays until another is chosen`() {
        val own = stored("trojan://pw-a@198.51.100.1:443#A")
        val unreadable = listOf(UnreadableServer("u1", "U"), UnreadableServer("u2", "V"))
        val missing = SubscriptionMerge.merge(own, listOf(server("trojan://pw-a@198.51.100.1:443#A")), emptySet(), "u1", unreadable)
        assertEquals(listOf("u2"), missing.removed, "выбранный не удаляется")
        assertEquals("u1", missing.missing)
        assertEquals(SelectedChange.Missing, missing.selected)

        val changed = SubscriptionMerge.merge(own, listOf(server("trojan://pw-u@198.51.100.5:443#U")), emptySet(), "u1", unreadable)
        assertEquals(listOf("s0", "u2"), changed.removed)
        assertEquals("u1", changed.missing)
        assertEquals(SelectedChange.Changed, changed.selected, "в подписке есть сервер с тем же названием — его новая версия")
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

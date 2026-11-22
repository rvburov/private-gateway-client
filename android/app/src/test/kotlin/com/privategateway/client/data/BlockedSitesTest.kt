package com.privategateway.client.data

import org.junit.Test
import java.time.LocalDate
import java.time.ZoneId
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

class BlockedSitesTest {
    private fun line(host: String, outbound: String = "block") = "[Info] [42] app/dispatcher: taking detour [$outbound] for [tcp:$host:443]"

    @Test
    fun `only actual blocked destinations enter history and repetitions are counted`() {
        val history = BlockedSites()
        history.accept(line("example.com", "proxy"))
        history.accept("TLS failed for example.com")
        assertTrue(history.entries.value.isEmpty())
        history.accept(line("Example.COM"), 1000)
        history.accept(line("example.com"), 2000)
        assertEquals(listOf(BlockedSite("example.com", 2, 2000)), history.entries.value)
        assertTrue(history.entries.value.single().canAllow)
        assertNull(BlockedSites.blockedHost(line("https://example.com/path?token=secret")))
        assertEquals("sub.example.com", BlockedSites.blockedHost("[Info] [42] app/dispatcher: taking detour [block] for [tcp:sub.example.com:443]"))
        assertNull(BlockedSites.blockedHost("[Info] app/dispatcher: taking detour [proxy] for [tcp:sub.example.com:443]"))
        assertEquals("sub.example.com", BlockedSites.blockedHost("2026/11/23 00:00:00.123456 [Info] [42] app/dispatcher: Hit route rule: [site-rule] so taking detour [block] for [tcp:sub.example.com:443]"))
        assertNull(BlockedSites.blockedHost("[Warning] app/http: quoted taking detour [block] for [tcp:sub.example.com:443]"))
        assertNull(BlockedSites.blockedHost("[Warning] app/http: [Info] app/dispatcher: taking detour [block] for [tcp:sub.example.com:443]"))
    }

    @Test
    fun `ip does not become an automatic site permission and history is bounded and cleared`() {
        val history = BlockedSites()
        history.accept(line("[2001:db8::1]"), 1000)
        assertFalse(history.entries.value.single().canAllow)
        repeat(BlockedSites.LIMIT + 10) { history.accept(line("site$it.example.com"), 1000 + it.toLong()) }
        assertEquals(BlockedSites.LIMIT, history.entries.value.size)
        history.clear()
        assertTrue(history.entries.value.isEmpty())
        val old = history.begin()
        history.clear()
        history.record(line("old.example.com"), old)
        assertTrue(history.entries.value.isEmpty(), "поздняя строка старого ядра не возвращает историю после выхода")
    }

    @Test
    fun `first event of the new local day removes old entries`() {
        val history = BlockedSites()
        val day = LocalDate.of(2026, 11, 23).atStartOfDay(ZoneId.systemDefault()).toInstant().toEpochMilli()
        history.accept(line("yesterday.example.com"), day - 1)
        history.accept(line("today.example.com"), day)
        assertEquals(listOf("today.example.com"), history.entries.value.map { it.host })
        history.prune(day + 86_400_000)
        assertTrue(history.entries.value.isEmpty(), "полночь очищает историю и без новой блокировки")
    }
}

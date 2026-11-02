package com.privategateway.client.core.backup

import org.junit.jupiter.api.Test
import java.io.File
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

/** Содержимое копии телефона (решение 559): туда и обратно, без ключей, копия десктопа и чужой файл. */
class MobileBackupTest {
    private val fixtures = File(System.getProperty("testdata") ?: error("Нет пути к shared/testdata")).resolve("backup")
    private val created = Instant.parse("2026-11-02T15:42:00Z")

    private fun error(block: () -> Unit): BackupError = assertFailsWith<BackupException> { block() }.error

    @Test
    fun `copy with keys goes there and back`() {
        val copy = MobileBackup(
            created,
            servers = listOf(
                BackupServer("vless://11111111-2222-3333-4444-555555555555@198.51.100.20:443?security=tls#NL", "Нидерланды «01»", favorite = true),
                BackupServer("trojan://password@203.0.113.7:443#DE", null),
            ),
            subscriptions = listOf(BackupSubscription("https://example.com/sub/token", "Мой VPN")),
            settings = mapOf("tunnel_dns" to "Cloudflare", "connect_on_launch" to "true"),
        )
        val back = MobileBackup.read(copy.write())
        assertEquals(copy, back)
        assertTrue(back.keys)
    }

    @Test
    fun `copy without keys keeps subscriptions and settings only`() {
        val copy = MobileBackup(created, subscriptions = listOf(BackupSubscription("https://example.org/s", "Работа")), settings = emptyMap())
        val back = MobileBackup.read(copy.write())
        assertFalse(back.keys)
        assertNull(back.servers)
        assertEquals(copy.subscriptions, back.subscriptions)
        assertEquals(emptyMap(), back.settings)
    }

    @Test
    fun `desktop copy and other files are named honestly`() {
        val desktop = BackupEnvelope.open(fixtures.resolve("envelope-v1.pgbackup").readText(), "correct horse battery staple")
        assertEquals(BackupError.FromComputer, error { MobileBackup.read(desktop) })
        assertEquals(BackupError.NotBackup, error { MobileBackup.read("[1,2]".toByteArray()) })
        assertEquals(BackupError.NotBackup, error { MobileBackup.read("не JSON".toByteArray()) })
        val newer = MobileBackup(created).write().toString(Charsets.UTF_8).replace("\"version\":1", "\"version\":2")
        assertEquals(BackupError.NewerVersion, error { MobileBackup.read(newer.toByteArray()) })
    }
}

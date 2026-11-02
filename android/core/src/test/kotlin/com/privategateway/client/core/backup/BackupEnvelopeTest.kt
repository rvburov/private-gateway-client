package com.privategateway.client.core.backup

import org.junit.jupiter.api.Test
import java.io.File
import java.time.Instant
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNull

/** Файл .pgbackup — тот же, что у десктопа (решение 559): общий эталон, пароль, ключ, подмена обертки. */
class BackupEnvelopeTest {
    private val fixtures = File(System.getProperty("testdata") ?: error("Нет пути к shared/testdata")).resolve("backup")
    private val password = "correct horse battery staple"

    private fun error(block: () -> Unit): BackupError = assertFailsWith<BackupException> { block() }.error

    @Test
    fun `shared fixture made like the desktop opens with its password`() {
        val text = fixtures.resolve("envelope-v1.pgbackup").readText()
        assertContentEquals(fixtures.resolve("envelope-v1.plain.json").readBytes(), BackupEnvelope.open(text, password))
        assertEquals(Instant.parse("2026-11-02T12:00:00Z"), BackupEnvelope.created(text))
        assertEquals(BackupError.WrongPassword, error { BackupEnvelope.open(text, "wrong horse battery staple") })
    }

    @Test
    fun `phone copy opens with the password and the stored key`() {
        val created = Instant.parse("2026-11-02T15:42:00Z")
        val content = """{"servers":["vless://11111111-2222-3333-4444-555555555555@198.51.100.20:443"]}""".toByteArray()
        val sealed = BackupEnvelope.seal(content, password, created)
        assertContentEquals(content, BackupEnvelope.open(sealed, password))
        assertEquals(created, BackupEnvelope.created(sealed))

        val key = ByteArray(32) { it.toByte() }
        val auto = BackupEnvelope.seal(content, key, created)
        assertContentEquals(content, BackupEnvelope.open(auto, key))
        assertEquals(BackupError.NotBackup, error { BackupEnvelope.open(auto, password) }, "автокопия паролем не открывается")
    }

    @Test
    fun `changed envelope, newer version and other files are refused`() {
        val sealed = BackupEnvelope.seal("{}".toByteArray(), password, Instant.parse("2026-11-02T15:42:00Z"))
        val moved = sealed.replace("2026-11-02T15:42:00Z", "2026-11-03T15:42:00Z")
        assertEquals(BackupError.WrongPassword, error { BackupEnvelope.open(moved, password) }, "время создания — под защитой тега")
        assertEquals(BackupError.NewerVersion, error { BackupEnvelope.open(sealed.replace("\"version\":1", "\"version\":2"), password) })
        assertEquals(BackupError.NotBackup, error { BackupEnvelope.open("vless://example.com", password) })
        assertEquals(BackupError.NotBackup, error { BackupEnvelope.open(sealed.replace("\"iterations\":600000", "\"iterations\":1000"), password) })
        assertNull(BackupEnvelope.created("{}"))
    }
}

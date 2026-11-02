package com.privategateway.client.core.backup

import com.privategateway.client.core.links.JsonText
import com.privategateway.client.core.links.JsonTree
import java.security.SecureRandom
import java.time.Instant
import java.time.OffsetDateTime
import java.util.Base64
import javax.crypto.AEADBadTagException
import javax.crypto.Cipher
import javax.crypto.SecretKeyFactory
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.PBEKeySpec
import javax.crypto.spec.SecretKeySpec

/** Почему копию не открыть — `BackupError` десктопа. */
enum class BackupError {
    /** Это не файл резервной копии или он испорчен так, что не читается. */
    NotBackup,

    /** Копия из более новой версии программы. */
    NewerVersion,

    /** Пароль не подходит или содержимое изменено: AES-GCM эти случаи не различает. */
    WrongPassword,

    /** Копия десктопа: серверы в ней — описанием десктопа, телефон восстанавливает копии телефона (решение 559). */
    FromComputer,
}

class BackupException(val error: BackupError) : Exception(error.name)

/**
 * Файл .pgbackup — `BackupFile` десктопа, байт в байт тот же формат (решение 559): JSON-обертка с параметрами и
 * зашифрованным содержимым; шифр — AES-256-GCM, ключ — из пароля через PBKDF2-SHA256 (600 000 повторов, соль 16 байт) или
 * готовый ключ из хранилища ОС (автокопия). Параметры обертки и время создания входят в проверку подлинности. Общий эталон
 * `shared/testdata/backup` открывают десктоп, Android и iOS.
 */
object BackupEnvelope {
    const val FORMAT = "private-gateway-backup"
    const val VERSION = 1
    const val ITERATIONS = 600_000

    private const val PASSWORD_KDF = "pbkdf2-sha256"
    private const val STORED_KEY = "os-key"
    private const val MIN_ITERATIONS = 100_000
    private const val MAX_ITERATIONS = 10_000_000
    private const val TAG_BITS = 128

    private val random = SecureRandom()

    /** Запечатать паролем; долго (PBKDF2) — звать не из главного потока. */
    fun seal(content: ByteArray, password: String, created: Instant): String {
        val salt = ByteArray(16).also(random::nextBytes)
        return seal(content, PASSWORD_KDF, ITERATIONS, salt, derive(password, salt, ITERATIONS), created)
    }

    /** Запечатать готовым ключом (автокопия: ключ — в хранилище ОС). */
    fun seal(content: ByteArray, key: ByteArray, created: Instant): String = seal(content, STORED_KEY, 0, ByteArray(0), key, created)

    /** Открыть паролем; долго (PBKDF2) — звать не из главного потока. */
    fun open(text: String, password: String): ByteArray = open(text, PASSWORD_KDF) { salt, iterations -> derive(password, salt, iterations) }

    fun open(text: String, key: ByteArray): ByteArray = open(text, STORED_KEY) { _, _ -> key }

    /** Время создания из обертки — без пароля; null — не копия. */
    fun created(text: String): Instant? = envelope(text)?.let { parseTime(it.created) }

    private fun seal(content: ByteArray, kdf: String, iterations: Int, salt: ByteArray, key: ByteArray, created: Instant): String {
        val nonce = ByteArray(12).also(random::nextBytes)
        val envelope = Envelope(FORMAT, VERSION, kdf, iterations, b64(salt), b64(nonce), created.toString(), "")
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, SecretKeySpec(key, "AES"), GCMParameterSpec(TAG_BITS, nonce))
        cipher.updateAAD(envelope.associatedData())
        val sealed = cipher.doFinal(content)
        return JsonText.obj(
            "format" to envelope.format,
            "version" to envelope.version,
            "kdf" to envelope.kdf,
            "iterations" to envelope.iterations,
            "salt" to envelope.salt,
            "nonce" to envelope.nonce,
            "created" to envelope.created,
            "data" to b64(sealed),
        )
    }

    private fun open(text: String, kdf: String, key: (ByteArray, Int) -> ByteArray): ByteArray {
        val envelope = envelope(text) ?: throw BackupException(BackupError.NotBackup)
        if (envelope.version > VERSION) {
            throw BackupException(BackupError.NewerVersion)
        }

        val (salt, nonce, data) = try {
            Triple(unb64(envelope.salt), unb64(envelope.nonce), unb64(envelope.data))
        } catch (_: IllegalArgumentException) {
            throw BackupException(BackupError.NotBackup)
        }

        val iterationsOk = kdf == STORED_KEY || envelope.iterations in MIN_ITERATIONS..MAX_ITERATIONS
        if (envelope.kdf != kdf || !iterationsOk || nonce.size != 12 || data.size < TAG_BITS / 8) {
            throw BackupException(BackupError.NotBackup)
        }

        return try {
            val cipher = Cipher.getInstance("AES/GCM/NoPadding")
            cipher.init(Cipher.DECRYPT_MODE, SecretKeySpec(key(salt, envelope.iterations), "AES"), GCMParameterSpec(TAG_BITS, nonce))
            cipher.updateAAD(envelope.associatedData())
            cipher.doFinal(data)
        } catch (_: AEADBadTagException) {
            throw BackupException(BackupError.WrongPassword)
        }
    }

    private fun derive(password: String, salt: ByteArray, iterations: Int): ByteArray {
        val spec = PBEKeySpec(password.toCharArray(), salt, iterations, 256)
        try {
            return SecretKeyFactory.getInstance("PBKDF2WithHmacSHA256").generateSecret(spec).encoded
        } finally {
            spec.clearPassword()
        }
    }

    private fun envelope(text: String): Envelope? {
        val root = try {
            JsonTree.parse(text).value as? Map<*, *>
        } catch (_: JsonTree.SyntaxError) {
            null
        } ?: return null

        fun text(key: String) = root[key] as? String
        fun number(key: String) = (root[key] as? JsonTree.JsonNumber)?.text?.toIntOrNull()
        if (text("format") != FORMAT) {
            return null
        }

        return Envelope(
            FORMAT,
            number("version") ?: return null,
            text("kdf") ?: return null,
            number("iterations") ?: return null,
            text("salt") ?: return null,
            text("nonce") ?: return null,
            text("created") ?: return null,
            text("data") ?: return null,
        )
    }

    /** Время создания: десктоп пишет «2026-11-02T12:00:00.0000000+00:00», телефон — «2026-11-02T12:00:00Z». */
    private fun parseTime(text: String): Instant? = runCatching { OffsetDateTime.parse(text).toInstant() }.getOrNull()

    private fun b64(bytes: ByteArray): String = Base64.getEncoder().encodeToString(bytes)

    private fun unb64(text: String): ByteArray = Base64.getDecoder().decode(text)

    private class Envelope(
        val format: String,
        val version: Int,
        val kdf: String,
        val iterations: Int,
        val salt: String,
        val nonce: String,
        val created: String,
        val data: String,
    ) {
        /** Все, кроме самих данных, — под защитой тега GCM, как у десктопа. */
        fun associatedData(): ByteArray = listOf(format, version, kdf, iterations, salt, nonce, created).joinToString("|").toByteArray()
    }
}

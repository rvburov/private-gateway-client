package com.privategateway.client.core.settings

import java.security.MessageDigest
import java.security.SecureRandom
import java.util.Base64
import javax.crypto.SecretKeyFactory
import javax.crypto.spec.PBEKeySpec

/**
 * PIN-код снятия блока сайтов (решение 742), как `BlockPin` десктопа: 4–8 цифр, хранится солью и хешем PBKDF2-SHA256 — сам
 * код не записывается. Без него нельзя убрать или изменить правило «Блокировать», открыть остальные сайты и разрешить сайт
 * при «Остальные сайты: Блокировать». Забыли — сброс входом в аккаунт.
 */
data class BlockPin(val salt: String, val hash: String) {
    /** Совпадает ли код; сравнение — за одинаковое время. Испорченная запись не пускает. */
    fun matches(pin: String?): Boolean {
        if (!valid(pin)) {
            return false
        }

        return try {
            MessageDigest.isEqual(derive(pin!!, Base64.getDecoder().decode(salt)), Base64.getDecoder().decode(hash))
        } catch (_: IllegalArgumentException) {
            false
        }
    }

    /** Запись для хранилища: «соль:хеш». */
    override fun toString(): String = "$salt:$hash"

    companion object {
        const val MIN_LENGTH = 4
        const val MAX_LENGTH = 8

        /** Сколько раз считать хеш — как у десктопа. */
        private const val ITERATIONS = 120_000

        fun valid(pin: String?): Boolean = pin != null && pin.length in MIN_LENGTH..MAX_LENGTH && pin.all { it in '0'..'9' }

        fun create(pin: String): BlockPin {
            require(valid(pin)) { "PIN-код — от 4 до 8 цифр" }
            val salt = ByteArray(16).also { SecureRandom().nextBytes(it) }
            return BlockPin(Base64.getEncoder().encodeToString(salt), Base64.getEncoder().encodeToString(derive(pin, salt)))
        }

        /** Из записи хранилища; пусто или испорчено — null (кода нет). */
        fun parse(text: String?): BlockPin? {
            val parts = text?.split(':') ?: return null
            return if (parts.size == 2 && parts.all { it.isNotEmpty() }) BlockPin(parts[0], parts[1]) else null
        }

        private fun derive(pin: String, salt: ByteArray): ByteArray =
            SecretKeyFactory.getInstance("PBKDF2WithHmacSHA256").generateSecret(PBEKeySpec(pin.toCharArray(), salt, ITERATIONS, 256)).encoded
    }
}

/** Ответ на введенный код. */
enum class PinCheck { Right, Wrong, Paused }

/**
 * Попытки ввода PIN-кода (решение 742): пять неверных подряд — минута без попыток, как у десктопа. Живет вместе с моделью
 * настроек: уход с экрана счетчик не сбрасывает.
 */
class BlockPinAttempts(private val now: () -> Long = System::currentTimeMillis) {
    private var failures = 0
    private var pausedUntil = 0L

    @Synchronized
    fun check(pin: String, stored: BlockPin?): PinCheck {
        val time = now()
        if (time < pausedUntil) {
            return PinCheck.Paused
        }

        if (stored == null || stored.matches(pin)) {
            failures = 0
            return PinCheck.Right
        }

        if (++failures >= ATTEMPTS) {
            failures = 0
            pausedUntil = time + PAUSE_MS
            return PinCheck.Paused
        }

        return PinCheck.Wrong
    }

    companion object {
        const val ATTEMPTS = 5
        const val PAUSE_MS = 60_000L
    }
}

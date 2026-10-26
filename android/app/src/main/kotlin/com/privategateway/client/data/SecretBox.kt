package com.privategateway.client.data

import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec
import javax.inject.Inject

/** Шифрование секретов серверов: ссылка с ключом сервера хранится только запечатанной. */
interface SecretBox {
    fun seal(plain: ByteArray): ByteArray

    /** Открыть запечатанное; чужое, поврежденное или от другого ключа — исключение. */
    fun open(sealed: ByteArray): ByteArray
}

/**
 * AES-256-GCM ключом из Android Keystore: ключ не покидает хранилище устройства (на устройствах с TEE или StrongBox — и
 * память приложения), в базе — только шифротекст. Формат: версия (1 байт), IV (12 байт), шифротекст с тегом.
 */
class KeystoreSecretBox @Inject constructor() : SecretBox {
    override fun seal(plain: ByteArray): ByteArray {
        val cipher = Cipher.getInstance(TRANSFORMATION)
        cipher.init(Cipher.ENCRYPT_MODE, key())
        val iv = cipher.iv
        check(iv.size == IV_SIZE) { "IV не той длины: ${iv.size}" }
        return byteArrayOf(VERSION) + iv + cipher.doFinal(plain)
    }

    override fun open(sealed: ByteArray): ByteArray {
        require(sealed.size > 1 + IV_SIZE && sealed[0] == VERSION) { "Неизвестный формат запечатанных данных" }
        val cipher = Cipher.getInstance(TRANSFORMATION)
        cipher.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(TAG_BITS, sealed, 1, IV_SIZE))
        return cipher.doFinal(sealed, 1 + IV_SIZE, sealed.size - 1 - IV_SIZE)
    }

    @Synchronized
    private fun key(): SecretKey {
        val store = KeyStore.getInstance(PROVIDER).apply { load(null) }
        (store.getKey(ALIAS, null) as? SecretKey)?.let { return it }
        val generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, PROVIDER)
        generator.init(
            KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256)
                .build(),
        )
        return generator.generateKey()
    }

    private companion object {
        const val PROVIDER = "AndroidKeyStore"
        const val ALIAS = "pg-servers-v1"
        const val TRANSFORMATION = "AES/GCM/NoPadding"
        const val VERSION: Byte = 1
        const val IV_SIZE = 12
        const val TAG_BITS = 128
    }
}

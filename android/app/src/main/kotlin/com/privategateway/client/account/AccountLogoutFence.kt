package com.privategateway.client.account

import android.content.Context
import dagger.hilt.android.qualifiers.ApplicationContext
import java.io.File
import java.io.FileOutputStream
import java.io.IOException
import javax.inject.Inject

/**
 * Запрет восстанавливать прежний вход после выхода. Отдельный маленький файл без секретов: ошибка DataStore не должна
 * вернуть старый токен при следующем запуске. Файл остается до успешно сохраненного нового входа, в бэкап не попадает.
 * Если одновременно недоступны оба хранилища, процесс запрещает подключение и сообщает, что выход еще не закреплен.
 */
class AccountLogoutFence internal constructor(private val file: File) {
    @Inject
    constructor(@ApplicationContext context: Context) : this(File(context.noBackupFilesDir, "account.signed-out"))

    fun pending(): Boolean = file.exists()

    fun block() {
        file.parentFile?.let { parent ->
            if (!parent.isDirectory && !parent.mkdirs()) throw IOException("Sign-out directory unavailable")
        }
        FileOutputStream(file).use { output ->
            output.write(1)
            output.fd.sync()
        }
    }

    fun allow() {
        if (file.exists() && !file.delete()) throw IOException("Sign-out marker unavailable")
    }
}

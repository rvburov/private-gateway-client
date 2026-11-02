package com.privategateway.client.ui.servers

import android.content.Context
import android.net.Uri
import android.provider.OpenableColumns
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.ByteArrayOutputStream
import java.io.InputStream
import javax.inject.Inject
import javax.inject.Singleton

/**
 * Что вышло с файлом «Открыть файл»: текст и имя файла, или файл не прочитан (больше [ImportFile.MAX_BYTES], нет доступа,
 * ошибка чтения) — с именем, если система его дала.
 */
sealed interface FileRead {
    data class Found(val name: String?, val text: String) : FileRead

    data class Failed(val name: String?) : FileRead
}

/** Текстовый файл, выбранный в системном окне «Открыть»; в тестах — подделка. */
fun interface ImportFiles {
    suspend fun read(file: Uri): FileRead
}

/** Файл для импорта: предел размера, типы для окна «Открыть», название профиля по имени файла. */
object ImportFile {
    /** Не больше 1 МБ, как у десктопа: в конфиге и подписке столько текста не бывает. */
    const val MAX_BYTES = 1024 * 1024

    /**
     * Типы файлов для окна «Открыть»: текст (ссылки, base64), JSON (sing-box), YAML (Clash) и файлы без известного
     * расширения — подписки часто сохраняют без него.
     */
    val TYPES = arrayOf("text/*", "application/json", "application/yaml", "application/x-yaml", "application/octet-stream")

    /** Название профиля из имени файла — без расширения, как у десктопа («work-servers.yaml» → «work-servers»). */
    fun profileName(file: String): String = file.substringBeforeLast('.').ifBlank { file }
}

/**
 * Файл — через ContentResolver: приложению доступен только выбранный файл, без разрешения на память. Текст — UTF-8;
 * испорченные байты становятся знаком замены, и разбор честно скажет, что серверов не найдено. Не в главном потоке.
 */
@Singleton
class DeviceImportFiles @Inject constructor(@param:ApplicationContext private val context: Context) : ImportFiles {
    override suspend fun read(file: Uri): FileRead = withContext(Dispatchers.IO) {
        val name = displayName(file)
        try {
            val bytes = context.contentResolver.openInputStream(file)?.use { limited(it) }
            if (bytes == null) FileRead.Failed(name) else FileRead.Found(name, String(bytes, Charsets.UTF_8))
        } catch (e: CancellationException) {
            throw e
        } catch (_: Exception) {
            FileRead.Failed(name)
        }
    }

    /** Имя файла, как его показывает система; нет — последняя часть адреса. */
    private fun displayName(file: Uri): String? {
        val shown = try {
            context.contentResolver.query(file, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use { cursor ->
                if (cursor.moveToFirst()) cursor.getString(0) else null
            }
        } catch (e: CancellationException) {
            throw e
        } catch (_: Exception) {
            null
        }

        return shown?.takeIf { it.isNotBlank() } ?: file.lastPathSegment
    }

    /** Весь файл, если он не больше [ImportFile.MAX_BYTES]; больше — null. */
    private fun limited(input: InputStream): ByteArray? {
        val out = ByteArrayOutputStream()
        val buffer = ByteArray(16 * 1024)
        var read = input.read(buffer)
        while (read >= 0) {
            if (out.size() + read > ImportFile.MAX_BYTES) {
                return null
            }

            out.write(buffer, 0, read)
            read = input.read(buffer)
        }

        return out.toByteArray()
    }
}

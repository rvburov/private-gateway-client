package com.privategateway.client.updates

import android.content.Context
import com.privategateway.client.BuildConfig
import com.privategateway.client.core.text.SecretMasker
import com.privategateway.client.core.updates.UpdateCheck
import com.privategateway.client.core.updates.UpdateResult
import com.privategateway.client.vpn.TunnelJournal
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.ByteArrayOutputStream
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URI
import javax.inject.Inject
import javax.inject.Singleton

/** Проверка обновления по кнопке «Проверить» (DECISIONS.md, п. 8); в тестах — подделка. */
fun interface UpdateSource {
    suspend fun check(): UpdateResult
}

/**
 * Проверка обновления Android: ключи подписи — из APK (assets/update-keys, их кладет сборка из shared/update/keys), выпуски и
 * манифест — с GitHub по HTTPS из процесса приложения (под защитой — через туннель). Перенаправления — только HTTPS (загрузки
 * выпусков GitHub отдает с другого узла), не больше [UpdateCheck.MAX_BYTES], 15 с на соединение и ответ. Отказ — строкой в
 * журнал «Проверки», без адресов с параметрами.
 */
@Singleton
class AppUpdates @Inject constructor(
    @param:ApplicationContext private val context: Context,
    private val journal: TunnelJournal,
) : UpdateSource {
    override suspend fun check(): UpdateResult {
        val result = UpdateCheck.check(BuildConfig.VERSION_NAME, keys(), ::download)
        journal.info("update check  " + result.javaClass.simpleName + ((result as? UpdateResult.Available)?.let { " " + it.version } ?: ""))
        return result
    }

    private fun keys(): List<String> {
        val assets = context.assets
        return assets.list(KEYS).orEmpty().filter { it.endsWith(".pem") }.sorted().mapNotNull { name ->
            runCatching { assets.open("$KEYS/$name").bufferedReader().use { it.readText() } }.getOrNull()
        }
    }

    private suspend fun download(url: String): ByteArray? = withContext(Dispatchers.IO) {
        try {
            val connection = URI(url).toURL().openConnection() as HttpURLConnection
            try {
                connection.connectTimeout = TIMEOUT_MS
                connection.readTimeout = TIMEOUT_MS
                // HttpURLConnection переходит только по ссылке того же протокола: с HTTPS на HTTP — нет.
                connection.instanceFollowRedirects = true
                connection.useCaches = false
                connection.setRequestProperty("User-Agent", "Private-Gateway-Client/" + BuildConfig.VERSION_NAME)
                connection.setRequestProperty("Accept", "application/vnd.github+json, application/octet-stream")
                if (connection.responseCode != HttpURLConnection.HTTP_OK || connection.url.protocol != "https") {
                    journal.warning("update download  " + connection.responseCode + " " + SecretMasker.mask(connection.url.host))
                    return@withContext null
                }

                connection.inputStream.use { input ->
                    val out = ByteArrayOutputStream()
                    val buffer = ByteArray(8192)
                    while (true) {
                        val read = input.read(buffer)
                        if (read < 0) {
                            break
                        }

                        out.write(buffer, 0, read)
                        if (out.size() > UpdateCheck.MAX_BYTES) {
                            return@withContext null
                        }
                    }
                    out.toByteArray()
                }
            } finally {
                connection.disconnect()
            }
        } catch (e: IOException) {
            journal.warning("update download  " + e.javaClass.simpleName)
            null
        } catch (e: IllegalArgumentException) {
            journal.warning("update download  " + e.javaClass.simpleName)
            null
        }
    }

    private companion object {
        const val KEYS = "update-keys"
        const val TIMEOUT_MS = 15_000
    }
}

package com.privategateway.client.core.updates

import com.privategateway.client.core.account.AccountApi
import com.privategateway.client.core.links.JsonTree
import java.io.ByteArrayOutputStream
import java.io.File
import java.io.FileOutputStream
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URI
import java.net.URISyntaxException
import java.security.MessageDigest

/** Итог загрузки APK обновления. */
sealed interface DownloadResult {
    /** Файл скачан: размер и SHA-256 — как в подписанном манифесте. */
    data object Done : DownloadResult

    /** Скачалось не то: размер или SHA-256 не сходятся с манифестом — файл удален. [reason] — для журнала. */
    data class BadFile(val reason: String) : DownloadResult

    /** Билет на файл истек (403 ticket_expired): взять ленту заново — в ней новый билет — и повторить. */
    data object Expired : DownloadResult

    /**
     * Не скачалось: сеть, ответ не 200 (403 invalid_ticket или forbidden — устройство отозвано, 404 — файла нет), диск — файл
     * удален. [reason] — для журнала: код ответа и код ошибки сайта, без адреса.
     */
    data class Failed(val reason: String) : DownloadResult
}

/**
 * Загрузка APK обновления по ссылке-билету сайта (без токена: билет сам — право на файл): только https (http — только 127.0.0.1
 * для тестов, как у [AccountApi]), без перенаправлений и не длиннее размера из подписанного манифеста. Размер и SHA-256
 * сверяются по ходу — заголовкам сайта (длина, X-Checksum-SHA256) не верим на слово: не сошлись — [DownloadResult.BadFile],
 * оборвалось — [DownloadResult.Failed], билет истек — [DownloadResult.Expired]; во всех трех случаях файла [fetch] не
 * оставляет. Из тела ошибки 403 читается только код сайта ({"error":{"code":"…"}}). Ход — progress (получено, всего) примерно
 * по процентам. Сеть блокирующая: вызывать не в главном потоке.
 */
class ApkDownload(
    private val openConnection: (URI) -> HttpURLConnection = { it.toURL().openConnection() as HttpURLConnection },
    private val timeoutMs: Int = AccountApi.TIMEOUT_MS,
    private val userAgent: String = AccountApi.USER_AGENT,
) {
    /** Скачать [url] в [target]; [sha256] (строчными) и [size] — из подписанного манифеста. */
    fun fetch(url: String, target: File, sha256: String, size: Long, progress: (received: Long, total: Long) -> Unit = { _, _ -> }): DownloadResult {
        val result = try {
            load(url, target, sha256, size, progress)
        } catch (e: IOException) {
            DownloadResult.Failed(e.javaClass.simpleName)
        } catch (e: SecurityException) {
            DownloadResult.Failed(e.javaClass.simpleName)
        } catch (e: IllegalArgumentException) {
            DownloadResult.Failed(e.javaClass.simpleName)
        }

        if (result != DownloadResult.Done) {
            target.delete()
        }

        return result
    }

    private fun load(url: String, target: File, sha256: String, size: Long, progress: (Long, Long) -> Unit): DownloadResult {
        val uri = try {
            URI(url)
        } catch (_: URISyntaxException) {
            return DownloadResult.Failed("address")
        }

        if (!AccountApi.secure(uri) || size <= 0) {
            return DownloadResult.Failed("address")
        }

        val connection = openConnection(uri)
        try {
            connection.connectTimeout = timeoutMs
            connection.readTimeout = timeoutMs
            connection.instanceFollowRedirects = false
            connection.useCaches = false
            connection.setRequestProperty("User-Agent", userAgent)
            connection.setRequestProperty("Accept", "application/vnd.android.package-archive, application/octet-stream")
            val code = connection.responseCode
            if (code != HttpURLConnection.HTTP_OK) {
                val error = if (code == HttpURLConnection.HTTP_FORBIDDEN) errorCode(connection) else null
                return if (error == TICKET_EXPIRED) DownloadResult.Expired else DownloadResult.Failed("HTTP $code" + (error?.let { " $it" } ?: ""))
            }

            // Сайт объявил другой размер — это другой файл: не качать.
            val length = connection.contentLengthLong
            if (length >= 0 && length != size) {
                return DownloadResult.BadFile("length")
            }

            target.parentFile?.mkdirs()
            val digest = MessageDigest.getInstance("SHA-256")
            val step = maxOf(size / 100, MIN_STEP)
            var received = 0L
            var reported = 0L
            progress(0, size)
            connection.inputStream.use { input ->
                FileOutputStream(target).use { output ->
                    val buffer = ByteArray(BUFFER)
                    while (true) {
                        val read = input.read(buffer)
                        if (read < 0) {
                            break
                        }

                        if (received + read > size) {
                            return DownloadResult.BadFile("size")
                        }

                        digest.update(buffer, 0, read)
                        output.write(buffer, 0, read)
                        received += read
                        if (received - reported >= step || received == size) {
                            reported = received
                            progress(received, size)
                        }
                    }
                }
            }

            // Короче манифеста — оборвалось на середине (сеть), а не другой файл.
            if (received < size) {
                return DownloadResult.Failed("short")
            }

            return if (hex(digest.digest()) == sha256) DownloadResult.Done else DownloadResult.BadFile("sha256")
        } finally {
            connection.disconnect()
        }
    }

    /** Код ошибки сайта из короткого тела ответа; нет, длиннее [ERROR_MAX] или не тот вид — null. */
    private fun errorCode(connection: HttpURLConnection): String? {
        val text = try {
            connection.errorStream?.use { stream ->
                val out = ByteArrayOutputStream()
                val buffer = ByteArray(1024)
                while (out.size() < ERROR_MAX) {
                    val read = stream.read(buffer, 0, minOf(buffer.size, ERROR_MAX - out.size()))
                    if (read < 0) {
                        break
                    }

                    out.write(buffer, 0, read)
                }
                out.toString(Charsets.UTF_8.name())
            }
        } catch (_: IOException) {
            null
        } ?: return null

        val root = try {
            JsonTree.parse(text).value as? Map<*, *>
        } catch (_: JsonTree.SyntaxError) {
            null
        }
        return ((root?.get("error") as? Map<*, *>)?.get("code") as? String)?.takeIf { ERROR_CODE.matches(it) }
    }

    private companion object {
        const val BUFFER = 64 * 1024

        /** Ошибка сайта — несколько десятков байт; длиннее — не читать. */
        const val ERROR_MAX = 4 * 1024

        /** Код ошибки сайта у 403: билет истек — повторить с новым билетом. */
        const val TICKET_EXPIRED = "ticket_expired"

        /** Код ошибки сайта в журнал — только такой: строчные латинские и «_». */
        val ERROR_CODE = Regex("[a-z_]{1,40}")

        /** Чаще, чем раз в 256 КБ, ход не сообщается: окну хватает процентов. */
        const val MIN_STEP = 256 * 1024L

        fun hex(bytes: ByteArray): String = bytes.joinToString("") { (it.toInt() and 0xff).toString(16).padStart(2, '0') }
    }
}

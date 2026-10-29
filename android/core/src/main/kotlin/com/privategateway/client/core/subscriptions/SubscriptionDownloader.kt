package com.privategateway.client.core.subscriptions

import java.io.ByteArrayOutputStream
import java.io.IOException
import java.io.InterruptedIOException
import java.net.ConnectException
import java.net.HttpURLConnection
import java.net.NoRouteToHostException
import java.net.SocketTimeoutException
import java.net.URI
import java.net.UnknownHostException
import java.util.zip.GZIPInputStream
import javax.net.ssl.SSLException

/** Загруженная подписка: текст и название из заголовка Profile-Title. */
data class SubscriptionContent(val text: String, val title: String?)

/** Подписка не загрузилась: причина и код ответа сервера (для [DownloadFailure.Status]). Адреса в тексте нет. */
class SubscriptionDownloadException(val failure: DownloadFailure, val status: Int? = null) : IOException(failure.name)

/** Откуда загружается подписка: по сети — [SubscriptionDownloader], в тестах — подделка. */
fun interface SubscriptionSource {
    /** Текст и название подписки; не загрузилась — [SubscriptionDownloadException] с причиной. */
    fun download(url: URI): SubscriptionContent
}

/**
 * Загрузка подписки по http(s) — как `HttpSubscriptionDownloader` десктопа: не дольше 20 секунд, не больше 5 МБ после
 * распаковки, перенаправления — только внутри той же схемы (https не уходит на http). Адрес подписки — секрет: в ошибках
 * только причина и код ответа.
 */
class SubscriptionDownloader(
    private val timeoutMs: Int = DownloadLimits.TIMEOUT_MS,
    private val maxBytes: Int = DownloadLimits.MAX_BYTES,
    private val openConnection: (URI) -> HttpURLConnection = { it.toURL().openConnection() as HttpURLConnection },
    private val request: SubscriptionRequest = SubscriptionRequest(),
) : SubscriptionSource {
    override fun download(url: URI): SubscriptionContent {
        require(SubscriptionAddress.parse(url.toString()) != null) { "Подписка загружается только по http(s)" }
        val started = System.nanoTime()
        var target = url
        var redirects = 0
        while (true) {
            request.check()
            val connection = try {
                openConnection(target).also { request.attach(it) }
            } catch (e: IOException) {
                throw failure(e)
            }

            try {
                connection.connectTimeout = timeoutMs
                connection.readTimeout = timeoutMs
                // Каждый переход открывается тем же способом: закрепленная VPN Network не заменяется сетью по умолчанию.
                connection.instanceFollowRedirects = false
                connection.setRequestProperty("User-Agent", USER_AGENT)
                connection.setRequestProperty("Accept-Encoding", "gzip")
                val status = connection.responseCode
                if (status in listOf(301, 302, 303, 307, 308)) {
                    val next = connection.getHeaderField("Location")?.let { target.resolve(it) }
                    if (next == null || SubscriptionAddress.parse(next.toString()) == null ||
                        !next.scheme.equals(target.scheme, ignoreCase = true) || ++redirects > 10
                    ) {
                        throw SubscriptionDownloadException(DownloadFailure.Status, status)
                    }
                    target = next
                    continue
                }
                if (status !in 200..299) {
                    throw SubscriptionDownloadException(DownloadFailure.Status, status)
                }

                if (connection.contentLengthLong > maxBytes && connection.contentEncoding.isNullOrEmpty()) {
                    throw SubscriptionDownloadException(DownloadFailure.TooLarge)
                }

                val raw = connection.inputStream
                val body = if (connection.contentEncoding.equals("gzip", ignoreCase = true)) GZIPInputStream(raw) else raw
                val buffer = ByteArrayOutputStream()
                body.use { stream ->
                    val chunk = ByteArray(64 * 1024)
                    while (true) {
                        request.check()
                        val read = stream.read(chunk)
                        if (read < 0) {
                            break
                        }

                        if (buffer.size() + read > maxBytes) {
                            throw SubscriptionDownloadException(DownloadFailure.TooLarge)
                        }

                        // Медленный сервер, который отдает по байту, не держит загрузку дольше общего предела.
                        if ((System.nanoTime() - started) / 1_000_000 > timeoutMs) {
                            throw SubscriptionDownloadException(DownloadFailure.Timeout)
                        }

                        buffer.write(chunk, 0, read)
                    }
                }

                return SubscriptionContent(buffer.toString(Charsets.UTF_8.name()), SubscriptionTitle.from(connection.getHeaderField("Profile-Title")))
            } catch (e: SubscriptionDownloadException) {
                throw e
            } catch (e: IOException) {
                throw failure(e)
            } finally {
                request.detach(connection)
                connection.disconnect()
            }
        }
    }

    private fun failure(e: IOException) = SubscriptionDownloadException(
        when (e) {
            is UnknownHostException -> DownloadFailure.NameResolution
            is SocketTimeoutException, is InterruptedIOException -> DownloadFailure.Timeout
            is SSLException -> DownloadFailure.Tls
            is ConnectException, is NoRouteToHostException -> DownloadFailure.Connection
            else -> DownloadFailure.Network
        },
    )

    companion object {
        const val USER_AGENT = "PrivateGateway/0.1"
    }
}

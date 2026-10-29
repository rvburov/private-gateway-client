package com.privategateway.client.core.subscriptions

import com.privategateway.client.core.xray.SubscriptionProxy
import okhttp3.Credentials
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Authenticator
import java.io.ByteArrayOutputStream
import java.io.IOException
import java.io.InterruptedIOException
import java.net.ConnectException
import java.net.InetSocketAddress
import java.net.InetAddress
import java.net.Proxy
import java.net.SocketTimeoutException
import java.net.URI
import java.net.UnknownHostException
import java.util.concurrent.TimeUnit
import javax.net.SocketFactory
import javax.net.ssl.SSLException

/**
 * Только аутентифицированный HTTP-вход данного ядра. Имя адресата передается ядру в HTTP/CONNECT, не разрешается
 * телефоном; первое правило ядра отправляет этот вход в сервер раньше LAN и DNS. Аутентификатор принадлежит только
 * этому клиенту: пароль прокси не попадает в запрос HTTPS к сайту или глобальный Authenticator процесса.
 */
class ProtectedSubscriptionDownloader(
    proxy: SubscriptionProxy,
    socketFactory: SocketFactory,
    private val request: SubscriptionRequest,
    private val valid: () -> Unit = {},
) : SubscriptionSource {
    private val fixedProxy = Proxy(Proxy.Type.HTTP, InetSocketAddress(InetAddress.getByAddress(byteArrayOf(127, 0, 0, 1)), proxy.port))
    private val client = OkHttpClient.Builder()
        .proxy(fixedProxy)
        .socketFactory(socketFactory)
        .dns { throw UnknownHostException("Прямой DNS подписки запрещен") }
        .proxyAuthenticator { route, response ->
            valid()
            request.check()
            // 407 от HTTPS-сайта внутри уже открытого туннеля не является запросом пароля локального прокси.
            if (response.request.url.isHttps && response.request.method != "CONNECT" ||
                route?.proxy != fixedProxy || response.request.header("Proxy-Authorization") != null ||
                response.challenges().none { it.scheme.equals("Basic", true) || it.scheme.equals("OkHttp-Preemptive", true) }
            ) null
            else response.request.newBuilder().header("Proxy-Authorization", Credentials.basic(proxy.user, proxy.password)).build()
        }
        .authenticator(Authenticator.NONE)
        .followRedirects(false)
        .followSslRedirects(false)
        .retryOnConnectionFailure(false)
        .connectTimeout(DownloadLimits.TIMEOUT_MS.toLong(), TimeUnit.MILLISECONDS)
        .readTimeout(DownloadLimits.TIMEOUT_MS.toLong(), TimeUnit.MILLISECONDS)
        .callTimeout(DownloadLimits.TIMEOUT_MS.toLong(), TimeUnit.MILLISECONDS)
        .build()

    override fun download(url: URI): SubscriptionContent {
        require(SubscriptionAddress.parse(url.toString()) != null)
        var target = url
        var redirects = 0
        val deadline = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(DownloadLimits.TIMEOUT_MS.toLong())
        try {
            while (true) {
                valid()
                request.check()
                val call = client.newCall(Request.Builder().url(target.toString()).header("User-Agent", SubscriptionDownloader.USER_AGENT).build())
                call.timeout().deadlineNanoTime(deadline)
                request.onCancel { call.cancel() }
                call.execute().use { response ->
                    if (response.code in listOf(301, 302, 303, 307, 308)) {
                        val next = response.header("Location")?.let { target.resolve(it) }
                        if (next == null || SubscriptionAddress.parse(next.toString()) == null ||
                            !next.scheme.equals(target.scheme, true) || ++redirects > 10
                        ) throw SubscriptionDownloadException(DownloadFailure.Status, response.code)
                        target = next
                    } else {
                        if (!response.isSuccessful) throw SubscriptionDownloadException(DownloadFailure.Status, response.code)
                        val body = ByteArrayOutputStream()
                        response.body.byteStream().use { input ->
                            val buffer = ByteArray(8192)
                            while (true) {
                                request.check()
                                val count = input.read(buffer)
                                if (count < 0) break
                                if (body.size() + count > DownloadLimits.MAX_BYTES) throw SubscriptionDownloadException(DownloadFailure.TooLarge)
                                body.write(buffer, 0, count)
                            }
                        }
                        return SubscriptionContent(body.toString(Charsets.UTF_8.name()), SubscriptionTitle.from(response.header("Profile-Title")))
                    }
                }
            }
        } catch (e: SubscriptionDownloadException) {
            throw e
        } catch (e: IOException) {
            throw SubscriptionDownloadException(when (e) {
                is UnknownHostException -> DownloadFailure.NameResolution
                is SocketTimeoutException, is InterruptedIOException -> DownloadFailure.Timeout
                is SSLException -> DownloadFailure.Tls
                is ConnectException -> DownloadFailure.Connection
                else -> DownloadFailure.Network
            })
        } finally {
            client.connectionPool.evictAll()
            client.dispatcher.executorService.shutdown()
        }
    }
}

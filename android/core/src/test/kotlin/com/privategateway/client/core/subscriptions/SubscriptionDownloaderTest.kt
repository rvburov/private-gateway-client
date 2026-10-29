package com.privategateway.client.core.subscriptions

import com.sun.net.httpserver.HttpServer
import org.junit.jupiter.api.AfterEach
import org.junit.jupiter.api.Test
import java.io.ByteArrayOutputStream
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.URI
import java.net.HttpURLConnection
import java.io.ByteArrayInputStream
import java.io.InputStream
import java.io.IOException
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import kotlinx.coroutines.async
import kotlinx.coroutines.runBlocking
import java.util.zip.GZIPOutputStream
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNull

/** Загрузка с локального сервера на 127.0.0.1 — сети машины тест не касается. */
class SubscriptionDownloaderTest {
    private val server = HttpServer.create(InetSocketAddress(InetAddress.getLoopbackAddress(), 0), 0).apply { start() }
    private val base = "http://127.0.0.1:${server.address.port}"

    @AfterEach
    fun stop() = server.stop(0)

    private fun serve(path: String, status: Int = 200, headers: Map<String, String> = emptyMap(), body: ByteArray = ByteArray(0)) {
        server.createContext(path) { exchange ->
            headers.forEach { (name, value) -> exchange.responseHeaders.add(name, value) }
            exchange.sendResponseHeaders(status, if (body.isEmpty()) -1 else body.size.toLong())
            exchange.responseBody.use { it.write(body) }
        }
    }

    @Test
    fun `text and title come back`() {
        serve("/sub/token", headers = mapOf("Profile-Title" to "base64:0J3QvtGD0YLQsdGD0Lo="), body = "vless://a\nvless://b".toByteArray())
        val content = SubscriptionDownloader().download(URI("$base/sub/token"))
        assertEquals("vless://a\nvless://b", content.text)
        assertEquals("Ноутбук", content.title)
    }

    @Test
    fun `gzip is unpacked and the limit counts unpacked bytes`() {
        val packed = ByteArrayOutputStream().also { out -> GZIPOutputStream(out).use { it.write(ByteArray(3000) { 'a'.code.toByte() }) } }.toByteArray()
        serve("/gz", headers = mapOf("Content-Encoding" to "gzip"), body = packed)
        assertEquals(3000, SubscriptionDownloader().download(URI("$base/gz")).text.length)
        val failure = assertFailsWith<SubscriptionDownloadException> { SubscriptionDownloader(maxBytes = 2000).download(URI("$base/gz")) }
        assertEquals(DownloadFailure.TooLarge, failure.failure)
    }

    @Test
    fun `too large body stops the download`() {
        serve("/big", body = ByteArray(5000))
        val failure = assertFailsWith<SubscriptionDownloadException> { SubscriptionDownloader(maxBytes = 4096).download(URI("$base/big")) }
        assertEquals(DownloadFailure.TooLarge, failure.failure)
    }

    @Test
    fun `server answer other than 2xx keeps the status and not the address`() {
        serve("/sub/secret-token", status = 404)
        val failure = assertFailsWith<SubscriptionDownloadException> { SubscriptionDownloader().download(URI("$base/sub/secret-token")) }
        assertEquals(DownloadFailure.Status, failure.failure)
        assertEquals(404, failure.status)
        assertEquals("Status", failure.message)
    }

    @Test
    fun `update through the downloader gets the reason not an exception`() {
        serve("/sub/gone", status = 403)
        serve("/sub/ok", body = "trojan://pw-a@198.51.100.1:443#A".toByteArray())
        assertEquals(FetchResult.Failed(DownloadFailure.Status, 403), SubscriptionFetch.fetch(URI("$base/sub/gone"), SubscriptionDownloader()))
        assertEquals(listOf("A"), (SubscriptionFetch.fetch(URI("$base/sub/ok"), SubscriptionDownloader()) as FetchResult.Fetched).servers.map { it.name })
    }

    @Test
    fun `closed port is a connection failure`() {
        val port = ServerSocket(0, 1, InetAddress.getLoopbackAddress()).use { it.localPort }
        val failure = assertFailsWith<SubscriptionDownloadException> { SubscriptionDownloader().download(URI("http://127.0.0.1:$port/sub")) }
        assertEquals(DownloadFailure.Connection, failure.failure)
    }

    @Test
    fun `slow server runs into the timeout`() {
        server.createContext("/slow") { exchange ->
            Thread.sleep(1500)
            exchange.sendResponseHeaders(200, -1)
            exchange.close()
        }
        val failure = assertFailsWith<SubscriptionDownloadException> { SubscriptionDownloader(timeoutMs = 300).download(URI("$base/slow")) }
        assertEquals(DownloadFailure.Timeout, failure.failure)
    }

    @Test
    fun `missing title is null and only http urls are accepted`() {
        serve("/plain", body = "ss://x".toByteArray())
        assertNull(SubscriptionDownloader().download(URI("$base/plain")).title)
        assertFailsWith<IllegalArgumentException> { SubscriptionDownloader().download(URI("file:///etc/hosts")) }
    }

    @Test
    fun `every redirect uses the captured connection factory and losing it never falls back`() {
        serve("/redirect", 302, mapOf("Location" to "/plain"))
        serve("/plain", body = "server".toByteArray())
        val opened = mutableListOf<URI>()
        val downloader = SubscriptionDownloader(openConnection = {
            opened += it
            it.toURL().openConnection() as HttpURLConnection
        })
        assertEquals("server", downloader.download(URI("$base/redirect")).text)
        assertEquals(listOf(URI("$base/redirect"), URI("$base/plain")), opened)

        var calls = 0
        val lost = SubscriptionDownloader(openConnection = {
            if (++calls == 2) throw IOException("Сеть VPN потеряна")
            it.toURL().openConnection() as HttpURLConnection
        })
        assertEquals(DownloadFailure.Network, assertFailsWith<SubscriptionDownloadException> { lost.download(URI("$base/redirect")) }.failure)
        assertEquals(2, calls)
    }

    @Test
    fun `cancellation closes a request blocked before the response and prevents its next hop`() = runBlocking {
        val waiting = CountDownLatch(1)
        val closed = CountDownLatch(1)
        val request = SubscriptionRequest()
        var opens = 0
        val connection = object : HttpURLConnection(URI("$base/blocked").toURL()) {
            override fun connect() = Unit
            override fun usingProxy() = false
            override fun disconnect() { closed.countDown() }
            override fun getResponseCode(): Int {
                waiting.countDown()
                check(closed.await(3, TimeUnit.SECONDS))
                return 302
            }
            override fun getHeaderField(name: String): String? = if (name == "Location") "/next" else null
            override fun getInputStream(): InputStream = ByteArrayInputStream(ByteArray(0))
        }
        val downloader = SubscriptionDownloader(openConnection = { opens++; connection }, request = request)
        val work = async { request.execute { downloader.download(URI("$base/blocked")) } }
        kotlinx.coroutines.yield()
        check(waiting.await(3, TimeUnit.SECONDS))
        work.cancel()
        assertEquals(0L, closed.count, "отмена закрывает соединение сразу, а не после сетевого таймаута")
        work.join()
        assertEquals(1, opens, "после отмены перенаправление не открывается")
    }
}

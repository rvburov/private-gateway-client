package com.privategateway.client.core.updates

import com.privategateway.client.core.account.AccountApi
import com.sun.net.httpserver.HttpExchange
import com.sun.net.httpserver.HttpServer
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.AfterEach
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.io.TempDir
import java.io.File
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.URI
import java.security.KeyPairGenerator
import java.security.MessageDigest
import java.security.Signature
import java.security.spec.ECGenParameterSpec
import java.util.Base64
import java.util.concurrent.CopyOnWriteArrayList
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/** Загрузка APK обновления по билету сайта на локальном сервере 127.0.0.1: размер и SHA-256 — из подписанного манифеста. */
class ApkDownloadTest {
    private val server = HttpServer.create(InetSocketAddress(InetAddress.getLoopbackAddress(), 0), 0).apply { start() }
    private val base = "http://127.0.0.1:${server.address.port}"
    private val apk = ByteArray(700 * 1024) { (it * 31 + 7).toByte() }
    private val sha = sha256(apk)
    private val hits = CopyOnWriteArrayList<String>()

    @TempDir
    lateinit var folder: File

    private val target by lazy { folder.resolve("updates/update.apk") }

    @AfterEach
    fun stop() = server.stop(0)

    private fun sha256(bytes: ByteArray) = MessageDigest.getInstance("SHA-256").digest(bytes).joinToString("") { "%02x".format(it) }

    /**
     * Файл по пути: [length] — Content-Length (null — частями, без длины), [status] — код ответа; у ошибки [body] — ее тело
     * JSON (пустое — без тела).
     */
    private fun serve(path: String, body: ByteArray, length: Long? = body.size.toLong(), status: Int = 200, headers: Map<String, String> = emptyMap()) {
        server.createContext(path) { exchange: HttpExchange ->
            hits += exchange.requestURI.path
            headers.forEach { (name, value) -> exchange.responseHeaders.add(name, value) }
            exchange.responseHeaders.add("Content-Type", if (status == 200) "application/vnd.android.package-archive" else "application/json")
            when {
                status == 200 -> {
                    exchange.sendResponseHeaders(status, length ?: 0)
                    exchange.responseBody.use { it.write(body) }
                }

                body.isNotEmpty() -> {
                    exchange.sendResponseHeaders(status, body.size.toLong())
                    exchange.responseBody.use { it.write(body) }
                }

                else -> {
                    exchange.sendResponseHeaders(status, -1)
                    exchange.close()
                }
            }
        }
    }

    private fun siteError(code: String) = """{"error":{"code":"$code"}}""".toByteArray()

    private fun fetch(url: String = "$base/files/t1", bytes: ByteArray = apk, size: Long = bytes.size.toLong(), sha256: String = sha, steps: MutableList<Pair<Long, Long>>? = null) =
        ApkDownload(timeoutMs = 5_000).fetch(url, target, sha256, size) { received, total -> steps?.add(received to total) }

    @Test
    fun `file matching the manifest is kept and progress goes to the end`() {
        serve("/files/t1", apk)
        val steps = mutableListOf<Pair<Long, Long>>()
        assertEquals(DownloadResult.Done, fetch(steps = steps))
        assertContentEquals(apk, target.readBytes())
        assertEquals(0L to apk.size.toLong(), steps.first())
        assertEquals(apk.size.toLong() to apk.size.toLong(), steps.last())
        assertEquals(steps.map { it.first }.sorted(), steps.map { it.first }, "ход только растет")
        assertTrue(steps.size in 3..110, "ход примерно по процентам, а не на каждый кусок: ${steps.size}")
    }

    @Test
    fun `chunked answer without a length is checked the same way`() {
        serve("/files/t1", apk, length = null)
        assertEquals(DownloadResult.Done, fetch())
        assertContentEquals(apk, target.readBytes())
    }

    @Test
    fun `other bytes of the same size are a bad file and are deleted`() {
        val other = apk.copyOf().also { it[it.size / 2] = (it[it.size / 2] + 1).toByte() }
        serve("/files/t1", other)
        assertEquals(DownloadResult.BadFile("sha256"), fetch())
        assertFalse(target.exists(), "непроверенного файла не остается")
    }

    @Test
    fun `a different length is a bad file before reading it`() {
        serve("/files/t1", apk + byteArrayOf(1))
        assertEquals(DownloadResult.BadFile("length"), fetch())
        assertFalse(target.exists())
    }

    @Test
    fun `more bytes than the manifest says stop the download`() {
        serve("/files/t1", apk + ByteArray(4096), length = null)
        assertEquals(DownloadResult.BadFile("size"), fetch())
        assertFalse(target.exists())
    }

    @Test
    fun `a short answer is a failed download, not a bad file`() {
        serve("/files/t1", apk.copyOf(apk.size - 100), length = null)
        assertEquals(DownloadResult.Failed("short"), fetch())
        assertFalse(target.exists())
    }

    @Test
    fun `expired ticket asks for a new one and an old file does not stay`() {
        serve("/files/t1", siteError("ticket_expired"), status = 403)
        target.parentFile.mkdirs()
        target.writeBytes(byteArrayOf(1, 2, 3))
        assertEquals(DownloadResult.Expired, fetch())
        assertFalse(target.exists())
    }

    @Test
    fun `other refusals of the site fail with their code`() {
        serve("/files/forged", siteError("invalid_ticket"), status = 403)
        serve("/files/revoked", siteError("forbidden"), status = 403)
        serve("/files/odd", """{"error":{"code":"Ticket Expired!"}}""".toByteArray(), status = 403)
        serve("/files/bare", ByteArray(0), status = 403)
        serve("/files/gone", siteError("not_found"), status = 404)
        assertEquals(DownloadResult.Failed("HTTP 403 invalid_ticket"), fetch("$base/files/forged"))
        assertEquals(DownloadResult.Failed("HTTP 403 forbidden"), fetch("$base/files/revoked"))
        assertEquals(DownloadResult.Failed("HTTP 403"), fetch("$base/files/odd"), "чужой вид кода в журнал не идет")
        assertEquals(DownloadResult.Failed("HTTP 403"), fetch("$base/files/bare"))
        assertEquals(DownloadResult.Failed("HTTP 404"), fetch("$base/files/gone"))
        assertFalse(target.exists())
    }

    @Test
    fun `redirects are not followed`() {
        serve("/elsewhere", apk)
        serve("/files/t1", ByteArray(0), status = 302, headers = mapOf("Location" to "$base/elsewhere"))
        assertEquals(DownloadResult.Failed("HTTP 302"), fetch())
        assertEquals(listOf("/files/t1"), hits.toList())
    }

    @Test
    fun `only https leaves the phone`() {
        val opened = mutableListOf<URI>()
        val download = ApkDownload(openConnection = { opened += it; error("не должно открываться") })
        assertEquals(DownloadResult.Failed("address"), download.fetch("http://192.0.2.1/files/t1", target, sha, 10))
        assertEquals(DownloadResult.Failed("address"), download.fetch("не ссылка ::", target, sha, 10))
        assertEquals(DownloadResult.Failed("address"), download.fetch("ftp://127.0.0.1/files/t1", target, sha, 10))
        assertEquals(emptyList(), opened)
    }

    @Test
    fun `no server is a failed download`() {
        val port = ServerSocket(0, 1, InetAddress.getLoopbackAddress()).use { it.localPort }
        assertTrue(fetch("http://127.0.0.1:$port/files/t1") is DownloadResult.Failed)
        assertFalse(target.exists())
    }

    @Test
    fun `site feed to installed file end to end`() {
        // Весь путь Android на своем сервере: лента по токену → подпись → билет → APK, сверенный с манифестом.
        val pair = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }.generateKeyPair()
        val pem = "-----BEGIN PUBLIC KEY-----\n" + Base64.getEncoder().encodeToString(pair.public.encoded) + "\n-----END PUBLIC KEY-----"
        val manifest = ("""{"version":"0.3.0","channel":"stable","rollout":20,"published":"2026-11-13T00:00:00Z",""" +
            """"notes":"https://private-gateway.duckdns.org/downloads","highlights":[],""" +
            """"assets":{"android-apk":{"file":"PrivateGateway-0.3.0-android.apk","sha256":"$sha","size":${apk.size}}}}""").toByteArray()
        val signature = Base64.getEncoder().encodeToString(Signature.getInstance("SHA256withECDSA").run { initSign(pair.private); update(manifest); sign() })
        val files = "$base/api/app/updates/files/"
        val feed = """{"releases":[{"version":"0.3.0","channel":"stable","manifest":"${Base64.getEncoder().encodeToString(manifest)}",""" +
            """"signature":"$signature","files":{"android-apk":{"url":"${files}eyJ0IjoxfQ.${"D".repeat(43)}","expires_at":"2026-11-13T10:30:00Z"}}}]}"""
        val tokens = CopyOnWriteArrayList<String?>()
        server.createContext("/api/app/updates") { exchange ->
            tokens += exchange.requestHeaders.getFirst("Authorization")
            val bytes = feed.toByteArray()
            exchange.sendResponseHeaders(200, bytes.size.toLong())
            exchange.responseBody.use { it.write(bytes) }
        }
        server.createContext("/api/app/updates/files/") { exchange ->
            tokens += exchange.requestHeaders.getFirst("Authorization")
            exchange.sendResponseHeaders(200, apk.size.toLong())
            exchange.responseBody.use { it.write(apk) }
        }

        val api = AccountApi(URI(base))
        var offer: UpdateResult = UpdateResult.Failed
        runTest { offer = UpdateCheck.check("0.2.1", listOf(pem), { channel -> api.updates("tok-secret-1", channel = channel) }, files) }
        val available = offer as UpdateResult.Available
        assertEquals(DownloadResult.Done, ApkDownload().fetch(available.url, target, available.sha256, available.size))
        assertContentEquals(apk, target.readBytes())
        assertEquals(listOf("Bearer tok-secret-1", null), tokens.toList(), "файл по билету — без токена")
    }
}

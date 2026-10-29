package com.privategateway.client.core.subscriptions

import com.privategateway.client.core.links.LinkParser
import com.privategateway.client.core.xray.CoreSettings
import com.privategateway.client.core.xray.SubscriptionProxy
import com.privategateway.client.core.xray.XrayConfig
import com.sun.net.httpserver.HttpServer
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.jupiter.api.Assumptions.assumeTrue
import org.junit.jupiter.api.Test
import java.io.File
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.net.URI
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicReference
import javax.net.SocketFactory
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNull
import kotlin.test.assertTrue

class ProtectedSubscriptionDownloaderTest {
    @Test
    fun `real core authenticates the subscription input and routes private and domain urls through the proxy only`() {
        val xray = File(System.getProperty("xrayMobile") ?: error("Нет пути к Xray libXray"))
        assumeTrue(xray.isFile, "нет Xray libXray — scripts/get-xray-mobile.sh")
        val originAuth = AtomicReference<String?>("не было запроса")
        val origin = HttpServer.create(InetSocketAddress(InetAddress.getLoopbackAddress(), 0), 0)
        origin.createContext("/") { exchange ->
            originAuth.set(exchange.requestHeaders.getFirst("Proxy-Authorization"))
            val body = "server-only".toByteArray()
            exchange.sendResponseHeaders(200, body.size.toLong())
            exchange.responseBody.use { it.write(body) }
        }
        origin.start()
        val proxy = SubscriptionProxy(ServerSocket(0, 1, InetAddress.getLoopbackAddress()).use { it.localPort }, "test-user", "test-password")
        val server = requireNotNull(LinkParser.parse("vless://11111111-2222-3333-4444-555555555555@127.0.0.1:19999#Test").server)
        val config = Json.parseToJsonElement(XrayConfig.build(server, CoreSettings(allowLan = true, lanDns = "192.168.1.53"), 42, proxy).json!!).jsonObject.toMutableMap()
        config["inbounds"] = JsonArray(config["inbounds"]!!.jsonArray.filter { it.jsonObject["tag"]!!.jsonPrimitive.content == XrayConfig.SUBSCRIPTION_TAG })
        // Серверный выход перенаправляет на свой origin. Прямой выход закрыт: ошибка порядка LAN-правил сломает тест.
        config["outbounds"] = Json.parseToJsonElement("""[{"tag":"proxy","protocol":"freedom","settings":{"redirect":"127.0.0.1:${origin.address.port}"}},{"tag":"direct","protocol":"blackhole"},{"tag":"block","protocol":"blackhole"},{"tag":"dns-out","protocol":"dns"}]""")
        val file = File.createTempFile("pg-subscription-proxy-", ".json")
        val log = File.createTempFile("pg-subscription-proxy-", ".log")
        var process: Process? = null
        try {
            file.writeText(JsonObject(config).toString())
            process = ProcessBuilder(xray.path, "run", "-c", file.path).redirectErrorStream(true).redirectOutput(log).start()
            val deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(10)
            while (true) {
                assertTrue(process.isAlive, log.readText())
                if (runCatching { Socket("127.0.0.1", proxy.port).use { } }.isSuccess) break
                assertTrue(System.nanoTime() < deadline, log.readText())
                Thread.sleep(10)
            }
            for (url in listOf("http://192.168.254.254:18081/sub", "http://subscription.example.invalid:18081/sub")) {
                val request = SubscriptionRequest()
                assertEquals("server-only", ProtectedSubscriptionDownloader(proxy, SocketFactory.getDefault(), request).download(URI(url)).text)
                assertNull(originAuth.get(), "пароль прокси не должен доходить до origin")
            }
            val wrong = SubscriptionProxy(proxy.port, proxy.user, "wrong-password")
            val failure = assertFailsWith<SubscriptionDownloadException> {
                ProtectedSubscriptionDownloader(wrong, SocketFactory.getDefault(), SubscriptionRequest()).download(URI("http://192.168.254.254:18081/sub"))
            }
            assertEquals(DownloadFailure.Status, failure.failure)
            assertEquals(407, failure.status)
            process.destroy()
            assertTrue(process.waitFor(10, TimeUnit.SECONDS))
            assertFailsWith<SubscriptionDownloadException> {
                ProtectedSubscriptionDownloader(proxy, SocketFactory.getDefault(), SubscriptionRequest()).download(URI("http://subscription.example.invalid:18081/sub"))
            }
        } finally {
            process?.destroyForcibly()
            origin.stop(0)
            file.delete()
            log.delete()
        }
    }
}

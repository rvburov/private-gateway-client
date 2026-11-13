package com.privategateway.client.core.vpn

import com.sun.net.httpserver.HttpServer
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeoutOrNull
import org.junit.jupiter.api.Test
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.URI
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

/** Проба проверки до «Защищено» на настоящих сокетах: вид отказа для журнала, своя сеть для соединения, отмена. */
class TunnelProbeTest {
    @Test
    fun `failure names the exception class and not the address`() {
        val closed = ServerSocket(0).use { it.localPort }
        val result = TunnelProbe.check("http://127.0.0.1:$closed/", 2_000)
        assertEquals("ConnectException", result.failure)
        assertFalse(result.failure.orEmpty().contains("127.0.0.1"))
    }

    @Test
    fun `probe opens the connection through the given network`() = withServer { port ->
        // Адрес пробы закрыт, а «сеть» ведет на живой сервер: ответ пришел — значит, соединение открыла она.
        val closed = ServerSocket(0).use { it.localPort }
        val result = runBlocking {
            TunnelProbe.probe("http://127.0.0.1:$closed/", 2_000) {
                URI("http://127.0.0.1:$port/204").toURL().openConnection()
            }
        }
        assertTrue(result.answered, result.failure)
    }

    @Test
    fun `cancelled probe closes its connection instead of waiting for the socket timeout`() {
        // Сервер принимает соединение и молчит: так выглядит туннель, пока ядро ждет сервер.
        ServerSocket(0, 1, InetAddress.getLoopbackAddress()).use { silent ->
            val started = System.nanoTime()
            val result = runBlocking {
                withTimeoutOrNull(300) { TunnelProbe.probe("http://127.0.0.1:${silent.localPort}/", 10_000) }
            }
            val ms = (System.nanoTime() - started) / 1_000_000
            assertNull(result)
            assertTrue(ms < 3_000, "отмена ждала таймаута сокета: $ms мс")
        }
    }

    private fun withServer(block: (port: Int) -> Unit) {
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/204") { it.sendResponseHeaders(204, -1); it.close() }
        server.start()
        try {
            block(server.address.port)
        } finally {
            server.stop(0)
        }
    }
}

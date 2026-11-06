package com.privategateway.client

import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.privategateway.client.core.links.LinkParser
import com.privategateway.client.vpn.ServerTcp
import org.junit.Assume.assumeTrue
import org.junit.Test
import org.junit.runner.RunWith
import java.io.IOException
import java.net.InetSocketAddress
import java.net.Socket
import kotlin.test.assertNotNull
import kotlin.test.assertTrue

/**
 * Проба сервера мимо туннеля после отказа (отчет Android 2026-11-05: сотовая сеть, PGC-NET-001 без причины): сервер стенда
 * (scripts/android-test-server.sh) принимает TCP — «answers», закрытый порт той же машины — «silent». Адреса в строке нет.
 */
@RunWith(AndroidJUnit4::class)
class ServerTcpOnDeviceTest {
    private val context = InstrumentationRegistry.getInstrumentation().targetContext

    @Test
    fun verdict_tells_a_server_that_answers_from_one_this_network_does_not_reach() {
        val ready = reachable()
        val message = "нет сервера проверки туннеля на машине (scripts/android-test-server.sh)"
        if (InstrumentationRegistry.getArguments().getString("requireTunnelServer").toBoolean()) assertTrue(ready, message)
        else assumeTrue(message, ready)

        val open = assertNotNull(LinkParser.parse(link(PORT)).server)
        val answers = ServerTcp.verdict(context, open)
        assertTrue(answers.startsWith("server answers on tcp  port=$PORT"), answers)

        val closed = assertNotNull(LinkParser.parse(link(CLOSED_PORT)).server)
        val silent = ServerTcp.verdict(context, closed)
        assertTrue(silent.startsWith("server silent on tcp  port=$CLOSED_PORT"), silent)
        assertTrue(HOST !in answers + silent, "адреса сервера в журнале нет")
    }

    private fun reachable(): Boolean = try {
        Socket().use { it.connect(InetSocketAddress(HOST, PORT), 3_000) }
        true
    } catch (_: IOException) {
        false
    }

    private companion object {
        const val HOST = "10.0.2.2"
        const val PORT = 10086
        const val CLOSED_PORT = 10087

        /** VLESS без TLS к серверу на машине CI; UUID — заведомо ненастоящий, из примеров. */
        fun link(port: Int) = "vless://11111111-2222-3333-4444-555555555555@$HOST:$port?encryption=none&security=none&type=tcp#Emulator"
    }
}

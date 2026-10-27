package com.privategateway.client

import android.net.VpnService
import android.os.ParcelFileDescriptor
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.privategateway.client.core.links.LinkParser
import com.privategateway.client.core.vpn.TunnelState
import com.privategateway.client.di.AppEntryPoint
import com.privategateway.client.vpn.GatewayVpnService
import dagger.hilt.android.EntryPointAccessors
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.After
import org.junit.Assume.assumeTrue
import org.junit.Test
import org.junit.runner.RunWith
import java.io.IOException
import java.net.InetSocketAddress
import java.net.Socket
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNotNull
import kotlin.test.assertNull

/**
 * Настоящий туннель на эмуляторе (пункт 9.4): VpnService, ядро libXray и сервер Xray на машине CI
 * (scripts/android-test-server.sh; эмулятор видит машину как 10.0.2.2). Проверочный адрес 198.51.100.7 — из диапазона
 * документации: напрямую его нет, сервер отправляет все соединения на свой HTTP с ответом «pg-tunnel-ok». Ответ пришел —
 * трафик шел через туннель; после отключения адрес снова недоступен.
 */
@RunWith(AndroidJUnit4::class)
class TunnelOnDeviceTest {
    private val instrumentation = InstrumentationRegistry.getInstrumentation()
    private val context = instrumentation.targetContext
    private val app = EntryPointAccessors.fromApplication(context.applicationContext, AppEntryPoint::class.java)

    @After
    fun disconnect() = runBlocking<Unit> {
        GatewayVpnService.disconnect(context)
        withTimeout(15_000) { app.monitor().status.first { it.state == TunnelState.Off || it.state == TunnelState.Error } }
        Unit
    }

    @Test
    fun traffic_goes_through_the_server_and_only_while_connected() = runBlocking<Unit> {
        assumeTrue("нет сервера проверки туннеля на машине (scripts/android-test-server.sh)", reachable(HOST, PORT))
        // Согласие на VPN за пользователя: системное окно «Запрос на подключение» в проверке не нажать.
        shell("appops set ${context.packageName} ACTIVATE_VPN allow")
        assertNull(VpnService.prepare(context), "разрешение на VPN не выдано")
        assertFailsWith<IOException>("проверочный адрес не должен быть доступен напрямую") { fetch() }

        val server = assertNotNull(LinkParser.parse(LINK).server, "ссылка проверки не разобралась")
        app.servers().add(listOf(server))
        val id = app.servers().servers.first().first { it.port == PORT }.id
        app.settings().select(id)

        GatewayVpnService.connect(context)
        val status = withTimeout(30_000) {
            app.monitor().status.first { it.state == TunnelState.Connected || it.state == TunnelState.Error }
        }
        assertEquals(TunnelState.Connected, status.state, "туннель не поднялся: ${status.error}")
        assertEquals("pg-tunnel-ok", fetch().trim())

        // Проба связи службы (сразу после подключения, через туннель к cp.cloudflare.com) прошла — переподключения нет.
        delay(12_000)
        assertEquals(TunnelState.Connected, app.monitor().status.value.state, "служба сочла связь потерянной")

        GatewayVpnService.disconnect(context)
        withTimeout(15_000) { app.monitor().status.first { it.state == TunnelState.Off } }
        assertFailsWith<IOException>("после отключения адрес снова недоступен") { fetch() }
    }

    /**
     * Страница проверочного адреса — HTTP простым сокетом: запрет Android на открытый HTTP (HttpURLConnection) его не
     * касается, а с ним и проверка «напрямую недоступен» проходила бы сама — отказом политики, а не сети.
     */
    private fun fetch(): String = Socket().use { socket ->
        socket.connect(InetSocketAddress(PROBE, 80), 5_000)
        socket.soTimeout = 5_000
        socket.getOutputStream().write("GET / HTTP/1.1\r\nHost: $PROBE\r\nConnection: close\r\n\r\n".toByteArray())
        socket.getInputStream().bufferedReader().readText().substringAfter("\r\n\r\n")
    }

    private fun reachable(host: String, port: Int): Boolean = try {
        Socket().use { it.connect(InetSocketAddress(host, port), 3_000) }
        true
    } catch (_: IOException) {
        false
    }

    private fun shell(command: String) {
        ParcelFileDescriptor.AutoCloseInputStream(instrumentation.uiAutomation.executeShellCommand(command)).use { it.readBytes() }
    }

    private companion object {
        const val HOST = "10.0.2.2"
        const val PORT = 10086
        const val PROBE = "198.51.100.7"

        /** VLESS без TLS к серверу на машине CI; UUID — заведомо ненастоящий, из примеров. */
        const val LINK = "vless://11111111-2222-3333-4444-555555555555@$HOST:$PORT?encryption=none&security=none&type=tcp#Emulator"
    }
}

package com.privategateway.client

import android.app.Notification
import android.app.NotificationManager
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
import kotlin.test.assertTrue

/**
 * Настоящий туннель на эмуляторе (пункт 9.4): VpnService, ядро libXray и сервер Xray на машине CI
 * (scripts/android-test-server.sh; эмулятор видит машину как 10.0.2.2). Проверочный адрес 1.1.1.1:18081 напрямую не
 * отвечает (на этом порту там ничего нет), а сервер отправляет соединения на порт 18081 на свой HTTP с ответом
 * «pg-tunnel-ok». Ответ пришел — трафик шел через туннель; после отключения адрес снова недоступен. Адрес публичный:
 * диапазоны документации клиент сам закрывает как особые сети (решение 446).
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
    }

    @Test
    fun traffic_goes_through_the_server_and_only_while_connected() = runBlocking<Unit> {
        prepare()
        assertFailsWith<IOException>("проверочный адрес не должен быть доступен напрямую") { fetch() }

        connect()
        assertEquals("pg-tunnel-ok", fetch().trim())

        // На экране блокировки уведомление туннеля — без текста: в нем название сервера.
        val tunnel = context.getSystemService(NotificationManager::class.java).activeNotifications
            .single { it.notification.channelId == PrivateGatewayApp.CHANNEL_TUNNEL }.notification
        assertEquals(Notification.VISIBILITY_PRIVATE, tunnel.visibility)
        assertNotNull(tunnel.extras.getCharSequence(Notification.EXTRA_TEXT), "в самом уведомлении сервер назван")
        assertNull(assertNotNull(tunnel.publicVersion, "нет версии для экрана блокировки").extras.getCharSequence(Notification.EXTRA_TEXT))

        // Проба связи службы (сразу после подключения, через туннель к cp.cloudflare.com) прошла — переподключения нет.
        delay(12_000)
        assertEquals(TunnelState.Connected, app.monitor().status.value.state, "служба сочла связь потерянной")

        GatewayVpnService.disconnect(context)
        withTimeout(15_000) { app.monitor().status.first { it.state == TunnelState.Off } }
        assertFailsWith<IOException>("после отключения адрес снова недоступен") { fetch() }
    }

    @Test
    fun tunnel_survives_doze_sleep_and_app_standby() = runBlocking<Unit> {
        prepare()
        connect()
        // Doze без зарядки, экран выключен, приложение «неактивно»: служба переднего плана держит туннель.
        shell("dumpsys battery unplug")
        shell("input keyevent KEYCODE_SLEEP")
        shell("dumpsys deviceidle force-idle")
        shell("am set-inactive ${context.packageName} true")
        try {
            delay(5_000)
            assertEquals("pg-tunnel-ok", fetch().trim(), "в Doze трафик идет через туннель")
            assertEquals(TunnelState.Connected, app.monitor().status.value.state)
        } finally {
            shell("am set-inactive ${context.packageName} false")
            shell("dumpsys deviceidle unforce")
            shell("input keyevent KEYCODE_WAKEUP")
            shell("dumpsys battery reset")
        }

        // Выход из Doze — проба сразу; связь та же, переподключения нет.
        delay(3_000)
        assertEquals(TunnelState.Connected, app.monitor().status.value.state, "после сна служба сочла связь потерянной")
        assertEquals("pg-tunnel-ok", fetch().trim())
    }

    @Test
    fun tunnel_follows_a_network_change() = runBlocking<Unit> {
        prepare()
        connect()
        // Wi-Fi выключен — сеть по умолчанию теперь мобильная: служба идет следом (DNS новой сети, проба сразу и, если
        // соединение с сервером оборвалось, переподключение к тому же серверу).
        shell("svc wifi disable")
        try {
            assertTrue(answersWithin(90_000), "после смены сети трафик через туннель не пошел: ${app.monitor().status.value}")
        } finally {
            shell("svc wifi enable")
        }

        assertTrue(answersWithin(90_000), "после возврата Wi-Fi трафик через туннель не пошел: ${app.monitor().status.value}")
        assertEquals(TunnelState.Connected, withTimeout(90_000) { app.monitor().status.first { it.state == TunnelState.Connected } }.state)
    }

    /** Согласие на VPN за пользователя: системное окно «Запрос на подключение» в проверке не нажать. */
    private fun prepare() {
        assumeTrue("нет сервера проверки туннеля на машине (scripts/android-test-server.sh)", reachable(HOST, PORT))
        shell("appops set ${context.packageName} ACTIVATE_VPN allow")
        assertNull(VpnService.prepare(context), "разрешение на VPN не выдано")
    }

    /** Сервер проверки выбран, защита включена и дошла до «Защищено». */
    private suspend fun connect() {
        val server = assertNotNull(LinkParser.parse(LINK).server, "ссылка проверки не разобралась")
        app.servers().add(listOf(server))
        val id = app.servers().servers.first().first { it.port == PORT }.id
        app.settings().select(id)

        GatewayVpnService.connect(context)
        val status = withTimeout(30_000) {
            app.monitor().status.first { it.state == TunnelState.Connected || it.state == TunnelState.Error }
        }
        assertEquals(TunnelState.Connected, status.state, "туннель не поднялся: ${status.error}")
    }

    /** Страница проверочного адреса пришла через туннель за [timeoutMs] — пробы раз в 2 с. */
    private suspend fun answersWithin(timeoutMs: Long): Boolean {
        val deadline = System.currentTimeMillis() + timeoutMs
        while (System.currentTimeMillis() < deadline) {
            val answer = try {
                fetch().trim()
            } catch (_: IOException) {
                null
            }

            if (answer == "pg-tunnel-ok") {
                return true
            }

            delay(2_000)
        }

        return false
    }

    /**
     * Страница проверочного адреса — HTTP простым сокетом: запрет Android на открытый HTTP (HttpURLConnection) его не
     * касается, а с ним и проверка «напрямую недоступен» проходила бы сама — отказом политики, а не сети.
     */
    private fun fetch(): String = Socket().use { socket ->
        socket.connect(InetSocketAddress(PROBE, PROBE_PORT), 5_000)
        socket.soTimeout = 5_000
        socket.getOutputStream().write("GET / HTTP/1.1\r\nHost: $PROBE:$PROBE_PORT\r\nConnection: close\r\n\r\n".toByteArray())
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
        const val PROBE = "1.1.1.1"
        const val PROBE_PORT = 18081

        /** VLESS без TLS к серверу на машине CI; UUID — заведомо ненастоящий, из примеров. */
        const val LINK = "vless://11111111-2222-3333-4444-555555555555@$HOST:$PORT?encryption=none&security=none&type=tcp#Emulator"
    }
}

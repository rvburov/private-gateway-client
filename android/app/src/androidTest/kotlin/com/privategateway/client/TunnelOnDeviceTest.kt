package com.privategateway.client

import android.Manifest
import android.app.Notification
import android.app.NotificationManager
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.net.VpnService
import android.os.Build
import android.os.ParcelFileDescriptor
import android.os.PowerManager
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.privategateway.client.core.links.LinkParser
import com.privategateway.client.core.vpn.TunnelState
import com.privategateway.client.core.settings.TunnelSettings
import com.privategateway.client.core.settings.DnsChoice
import com.privategateway.client.core.text.SecretMasker
import com.privategateway.client.core.xray.XrayConfig
import java.net.URI
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
    private val connectivity = context.getSystemService(ConnectivityManager::class.java)
    private var failureObserved = false

    @After
    fun disconnect() = runBlocking<Unit> {
        try {
            disconnectAndWait()
        } catch (failure: Throwable) {
            diagnostics("disconnect")
            throw failure
        }
        if (failureObserved) diagnostics("after-vpn-removed")
        app.settings().setTunnel(TunnelSettings())
    }

    @Test
    fun protected_subscriptions_use_authenticated_http_and_https_even_with_lan_and_private_dns() = networkTest {
        prepare()
        app.settings().setTunnel(TunnelSettings(dns = DnsChoice.Custom, customDns = "192.168.254.253", allowLan = true))
        connect()
        // Сервер стенда перенаправляет порт18081 на свой HTTP. Прямой LAN-путь до этого адреса не отвечает.
        val http = app.subscriptionTransport().download(URI("http://192.168.254.254:18081/"), onlyViaVpn = true)
        assertEquals("pg-tunnel-ok", http.text.trim())
        // Настоящий TLS через authenticated CONNECT: локальный DNS не должен искать имя Cloudflare.
        val https = app.subscriptionTransport().download(URI("https://cp.cloudflare.com/generate_204"), onlyViaVpn = true)
        assertEquals("", https.text)
        disconnectAndWait(offOnly = true)
        assertFailsWith<IOException> { app.subscriptionTransport().download(URI("https://cp.cloudflare.com/generate_204"), onlyViaVpn = true) }
    }

    @Test
    fun traffic_goes_through_the_server_and_only_while_connected() = networkTest {
        prepare()
        assertFailsWith<IOException>("проверочный адрес не должен быть доступен напрямую") { fetch() }

        connect()
        assertEquals("pg-tunnel-ok", fetch().trim())

        // На экране блокировки уведомление туннеля — без текста: в нем название сервера. Уведомление обновляется вслед за
        // состоянием — ждать, пока в нем появится сервер.
        val tunnel = withTimeout(10_000) {
            var found: Notification? = null
            while (found == null) {
                found = context.getSystemService(NotificationManager::class.java).activeNotifications
                    .map { it.notification }
                    .firstOrNull { it.channelId == PrivateGatewayApp.CHANNEL_TUNNEL && it.extras.getCharSequence(Notification.EXTRA_TEXT) != null }
                if (found == null) {
                    delay(200)
                }
            }

            checkNotNull(found)
        }
        assertEquals(Notification.VISIBILITY_PRIVATE, tunnel.visibility)
        assertNull(assertNotNull(tunnel.publicVersion, "нет версии для экрана блокировки").extras.getCharSequence(Notification.EXTRA_TEXT))

        // Проба связи службы (сразу после подключения, через туннель к cp.cloudflare.com) прошла — переподключения нет.
        delay(12_000)
        assertEquals(TunnelState.Connected, app.monitor().status.value.state, "служба сочла связь потерянной")

        disconnectAndWait(offOnly = true)
        assertFailsWith<IOException>("после отключения адрес снова недоступен") { fetch() }
    }

    @Test
    fun tunnel_survives_doze_sleep_and_app_standby() = networkTest {
        prepare()
        connect()
        assertEquals("pg-tunnel-ok", fetch().trim(), "путь через TUN работает до входа в Doze")
        // Doze без зарядки, экран выключен, приложение «неактивно»: служба переднего плана держит туннель.
        var primaryFailure: Throwable? = null
        try {
            shell("dumpsys battery unplug")
            shell("input keyevent KEYCODE_SLEEP")
            shell("dumpsys deviceidle force-idle")
            shell("am set-inactive ${context.packageName} true")
            delay(5_000)
            assertTrue(context.getSystemService(PowerManager::class.java).isDeviceIdleMode, "Android действительно вошел в Doze")
            assertEquals("pg-tunnel-ok", fetch().trim(), "в Doze трафик идет через туннель")
            assertEquals(TunnelState.Connected, app.monitor().status.value.state)
        } catch (failure: Throwable) {
            primaryFailure = failure
            diagnostics("in-doze-before-power-reset")
            throw failure
        } finally {
            val failures = listOf(
                "am set-inactive ${context.packageName} false",
                "dumpsys deviceidle unforce",
                "input keyevent KEYCODE_WAKEUP",
                "dumpsys battery reset",
            ).mapNotNull { command -> runCatching { shell(command) }.exceptionOrNull() }
            val report = primaryFailure ?: failures.firstOrNull()
            failures.forEach { if (it !== report) report?.addSuppressed(it) }
            if (primaryFailure == null && report != null) throw report
        }

        // Выход из Doze — проба сразу; связь та же, переподключения нет.
        delay(3_000)
        assertEquals(TunnelState.Connected, app.monitor().status.value.state, "после сна служба сочла связь потерянной")
        assertEquals("pg-tunnel-ok", fetch().trim())
    }

    @Test
    fun tunnel_follows_a_network_change() = networkTest {
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

    /**
     * Согласие на VPN и разрешение на уведомления (Android 13+) за пользователя: системные окна в проверке не нажать, а без
     * разрешения уведомление туннеля не показывается вовсе.
     */
    private fun prepare() {
        val ready = reachable(HOST, PORT)
        val message = "нет сервера проверки туннеля на машине (scripts/android-test-server.sh)"
        if (InstrumentationRegistry.getArguments().getString("requireTunnelServer").toBoolean()) assertTrue(ready, message)
        else assumeTrue(message, ready)
        shell("appops set ${context.packageName} ACTIVATE_VPN allow")
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            instrumentation.uiAutomation.grantRuntimePermission(context.packageName, Manifest.permission.POST_NOTIFICATIONS)
        }

        assertNull(VpnService.prepare(context), "разрешение на VPN не выдано")
    }

    private fun networkTest(block: suspend () -> Unit) = runBlocking<Unit> {
        try {
            block()
        } catch (failure: Throwable) {
            if (!failureObserved) diagnostics("failure-before-disconnect")
            throw failure
        }
    }

    /** Off в модели приходит раньше удаления маршрутов Android: следующий тест ждет тот же предел 15 с. */
    private suspend fun disconnectAndWait(offOnly: Boolean = false) {
        val previous = ownVpnNetworks()
        GatewayVpnService.disconnect(context)
        withTimeout(15_000) {
            app.monitor().status.first { it.state == TunnelState.Off || (!offOnly && it.state == TunnelState.Error) }
            while (previous.any { connectivity.getNetworkCapabilities(it) != null } || ownVpnNetworks().isNotEmpty()) delay(20)
        }
    }

    // Нужен разовый снимок именно в момент остановки или ошибки, без нового асинхронного слушателя фикстуры.
    @Suppress("DEPRECATION")
    private fun snapshotNetworks(): Array<Network> = connectivity.allNetworks

    private fun ownVpnNetworks(): Set<Network> = snapshotNetworks().filter { network ->
        val caps = connectivity.getNetworkCapabilities(network) ?: return@filter false
        caps.hasTransport(NetworkCapabilities.TRANSPORT_VPN) && if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            caps.ownerUid == context.applicationInfo.uid
        } else {
            connectivity.getLinkProperties(network)?.linkAddresses?.any { it.address.hostAddress == XrayConfig.TUN_ADDRESS } == true
        }
    }.toSet()

    /** Только наблюдение и соединения к тестовому HOST; ошибки диагностики не подменяют исходную ошибку проверки. */
    private fun diagnostics(stage: String) {
        failureObserved = true
        runCatching {
            fun log(text: String) = System.err.println(SecretMasker.mask("NATIVE $stage $text"))
            log("status=${app.monitor().status.value} blocked=${app.monitor().blocked.value} active=${connectivity.activeNetwork}")
            val networks = snapshotNetworks()
            for (network in networks) {
                val caps = connectivity.getNetworkCapabilities(network)
                log("network=$network vpn=${caps?.hasTransport(NetworkCapabilities.TRANSPORT_VPN)} interface=${connectivity.getLinkProperties(network)?.interfaceName}")
            }
            // Сокет по умолчанию, в том числе shell UID, при живом VPN тоже может идти через TUN.
            log("app-default active=${connectivity.activeNetwork} host=$HOST:$PORT ${probeHost(null)}")
            for (network in networks) {
                val caps = connectivity.getNetworkCapabilities(network) ?: continue
                if (!caps.hasTransport(NetworkCapabilities.TRANSPORT_VPN) && caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)) {
                    log("app-explicit-non-vpn network=$network host=$HOST:$PORT ${probeHost(network)}")
                }
            }
            log("shell-default " + shellHostProbe())
            val fields = listOf("mState=", "mLightState=", "mForceIdle=", "mScreenOn=", "mCharging=", "mDeviceIdleMode=", "Active default network", "NetworkAgentInfo")
            for (service in listOf("deviceidle", "netpolicy", "connectivity")) {
                val selected = shellOutput("dumpsys -t 2 $service").lineSequence()
                    .filter { line -> line.contains(context.applicationInfo.uid.toString()) || fields.any(line::contains) }
                    .take(120).joinToString("\n")
                log("$service\n$selected")
            }
        }
    }

    private fun probeHost(network: Network?): String = runCatching {
        (if (network == null) Socket() else network.socketFactory.createSocket()).use { it.connect(InetSocketAddress(HOST, PORT), 1_000) }
        "connected"
    }.getOrElse { it.javaClass.simpleName }

    private fun shellHostProbe(): String {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.S) return "unavailable: shell stdin requires API 31"
        // executeShellCommand передает строку в Runtime.exec, где кавычки sh -c не разбираются: сценарий идет в stdin.
        val pipes = instrumentation.uiAutomation.executeShellCommandRw("sh")
        return ParcelFileDescriptor.AutoCloseInputStream(pipes[0]).use { output ->
            ParcelFileDescriptor.AutoCloseOutputStream(pipes[1]).use { input ->
                input.write("toybox nc -w 1 $HOST $PORT < /dev/null; echo exit=\$?\n".toByteArray())
            }
            output.bufferedReader().readText()
        }
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
        shellOutput(command)
    }

    private fun shellOutput(command: String): String =
        ParcelFileDescriptor.AutoCloseInputStream(instrumentation.uiAutomation.executeShellCommand(command)).use { it.bufferedReader().readText() }

    private companion object {
        const val HOST = "10.0.2.2"
        const val PORT = 10086
        const val PROBE = "1.1.1.1"
        const val PROBE_PORT = 18081

        /** VLESS без TLS к серверу на машине CI; UUID — заведомо ненастоящий, из примеров. */
        const val LINK = "vless://11111111-2222-3333-4444-555555555555@$HOST:$PORT?encryption=none&security=none&type=tcp#Emulator"
    }
}

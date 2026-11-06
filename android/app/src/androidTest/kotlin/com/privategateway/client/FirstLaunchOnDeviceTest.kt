package com.privategateway.client

import android.Manifest
import android.content.Intent
import android.net.VpnService
import android.os.Build
import android.os.ParcelFileDescriptor
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import androidx.test.uiautomator.By
import androidx.test.uiautomator.UiDevice
import androidx.test.uiautomator.Until
import com.privategateway.client.core.links.LinkParser
import com.privategateway.client.core.vpn.TunnelState
import com.privategateway.client.di.AppEntryPoint
import com.privategateway.client.vpn.GatewayVpnService
import dagger.hilt.android.EntryPointAccessors
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
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * Первый запуск на телефоне (главная проверка пользователя 2026-11-06, docs/testing/first-run-service.md): разрешения на
 * VPN еще нет. Человек нажимает щит — Android показывает свой «Запрос на подключение», «ОК» — туннель поднимается и трафик
 * идет через сервер. В [TunnelOnDeviceTest] разрешение выдано заранее (`appops`); здесь — настоящее системное окно,
 * кнопка нажимается через UiAutomator. Сервер проверки — тот же, что у [TunnelOnDeviceTest] (scripts/android-test-server.sh).
 */
@RunWith(AndroidJUnit4::class)
class FirstLaunchOnDeviceTest {
    private val instrumentation = InstrumentationRegistry.getInstrumentation()
    private val context = instrumentation.targetContext
    private val app = EntryPointAccessors.fromApplication(context.applicationContext, AppEntryPoint::class.java)
    private val device = UiDevice.getInstance(instrumentation)

    @After
    fun disconnect() = runBlocking<Unit> {
        GatewayVpnService.disconnect(context)
        withTimeout(15_000) { app.monitor().status.first { it.state == TunnelState.Off || it.state == TunnelState.Error } }
        device.pressHome()
    }

    @Test
    fun first_connection_asks_android_for_the_vpn_and_after_ok_traffic_goes_through_the_server() = runBlocking<Unit> {
        val ready = reachable(HOST, PORT)
        val message = "нет сервера проверки туннеля на машине (scripts/android-test-server.sh)"
        if (InstrumentationRegistry.getArguments().getString("requireTunnelServer").toBoolean()) assertTrue(ready, message)
        else assumeTrue(message, ready)

        // Как после установки: разрешения на VPN нет, Android его еще не спрашивал. Уведомления — разрешены, чтобы после
        // подключения не мешал вопрос о них (его проверяют отдельно).
        shell("appops set ${context.packageName} ACTIVATE_VPN default")
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            instrumentation.uiAutomation.grantRuntimePermission(context.packageName, Manifest.permission.POST_NOTIFICATIONS)
        }
        assertNotNull(VpnService.prepare(context), "разрешение на VPN уже есть — первый запуск не воспроизведен")

        val server = assertNotNull(LinkParser.parse(LINK).server, "ссылка проверки не разобралась")
        app.servers().add(listOf(server))
        app.settings().select(app.servers().servers.first().first { it.port == PORT }.id)

        context.startActivity(Intent(context, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TASK))
        val shield = assertNotNull(
            device.wait(Until.findObject(By.desc(context.getString(R.string.power_action_connect))), WAIT_MS),
            "на главной нет щита «Подключить»",
        )
        shield.click()

        // Системное окно Android «Запрос на подключение» (пакет com.android.vpndialogs) — «ОК».
        val ok = assertNotNull(
            device.wait(Until.findObject(By.pkg(VPN_DIALOGS).res("android", "button1")), WAIT_MS),
            "Android не показал запрос на подключение VPN",
        )
        ok.click()

        val status = withTimeout(30_000) {
            app.monitor().status.first { it.state == TunnelState.Connected || it.state == TunnelState.Error }
        }
        assertEquals(TunnelState.Connected, status.state, "после «ОК» туннель не поднялся: ${status.error}")
        assertNull(VpnService.prepare(context), "разрешение запомнено: второй раз Android не спросит")
        assertEquals("pg-tunnel-ok", fetch().trim(), "трафик идет через сервер")
    }

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
        ParcelFileDescriptor.AutoCloseInputStream(instrumentation.uiAutomation.executeShellCommand(command)).use { it.bufferedReader().readText() }
    }

    private companion object {
        const val WAIT_MS = 15_000L
        const val VPN_DIALOGS = "com.android.vpndialogs"
        const val HOST = "10.0.2.2"
        const val PORT = 10086
        const val PROBE = "1.1.1.1"
        const val PROBE_PORT = 18081

        /** VLESS без TLS к серверу на машине CI; UUID — заведомо ненастоящий, из примеров. */
        const val LINK = "vless://11111111-2222-3333-4444-555555555555@$HOST:$PORT?encryption=none&security=none&type=tcp#Emulator"
    }
}

package com.privategateway.client

import android.Manifest
import android.content.ActivityNotFoundException
import android.content.ClipboardManager
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Color
import android.net.VpnService
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.SystemBarStyle
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.activity.viewModels
import androidx.core.content.ContextCompat
import com.privategateway.client.ui.AppActions
import com.privategateway.client.ui.AppScreen
import com.privategateway.client.ui.MainViewModel
import com.privategateway.client.ui.Tab
import com.privategateway.client.ui.servers.ServersViewModel
import com.privategateway.client.ui.settings.SettingsViewModel
import com.privategateway.client.ui.theme.PgTheme
import com.privategateway.client.vpn.GatewayVpnService
import com.privategateway.client.vpn.TunnelState
import dagger.hilt.android.AndroidEntryPoint

/**
 * Окно приложения. Здесь — то, что требует Activity: разрешение Android на VPN (системное окно «Запрос на подключение»,
 * доска 26) и на уведомления, системные настройки VPN, буфер обмена. Остальное — Compose (AppScreen).
 */
@AndroidEntryPoint
class MainActivity : ComponentActivity() {
    private val viewModel: MainViewModel by viewModels()
    private val settings: SettingsViewModel by viewModels()
    private val servers: ServersViewModel by viewModels()

    private val vpnPermission = registerForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
        if (result.resultCode == RESULT_OK) {
            GatewayVpnService.connect(this)
        } else {
            Toast.makeText(this, R.string.android_permission_denied, Toast.LENGTH_LONG).show()
        }
    }

    // Уведомление туннеля без разрешения не видно, но туннель работает и без него: ответ не мешает подключению.
    private val notificationPermission = registerForActivityResult(ActivityResultContracts.RequestPermission()) { askVpn() }

    override fun onCreate(savedInstanceState: Bundle?) {
        // Только светлая тема (макет): значки строки состояния и навигации — темные при любой теме телефона.
        enableEdgeToEdge(
            statusBarStyle = SystemBarStyle.light(Color.TRANSPARENT, Color.TRANSPARENT),
            navigationBarStyle = SystemBarStyle.light(Color.TRANSPARENT, Color.TRANSPARENT),
        )
        super.onCreate(savedInstanceState)
        val actions = AppActions(
            power = ::power,
            disconnect = { GatewayVpnService.disconnect(this) },
            openVpnSettings = ::openVpnSettings,
            clipboard = ::clipboardText,
        )
        setContent {
            PgTheme {
                AppScreen(viewModel, settings, servers, actions)
            }
        }

        openTab(intent)
    }

    override fun onStart() {
        super.onStart()
        viewModel.windowShown()
    }

    // Окно одно (singleTask): «Сменить сервер» из уведомления приходит сюда, когда окно уже открыто.
    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        openTab(intent)
    }

    private fun openTab(intent: Intent?) {
        if (intent?.getStringExtra(EXTRA_TAB) == TAB_SERVERS) {
            viewModel.show(Tab.Servers)
            intent.removeExtra(EXTRA_TAB)
        }
    }

    /** Кнопка-щит: включена или подключается — выключить; выключена или ошибка — подключить к выбранному. */
    private fun power() {
        when (viewModel.ui.value.status.state) {
            TunnelState.Connected, TunnelState.Connecting -> GatewayVpnService.disconnect(this)
            TunnelState.Stopping -> Unit
            TunnelState.Off, TunnelState.Error -> start()
        }
    }

    private fun start() {
        val ask = Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        if (ask) {
            notificationPermission.launch(Manifest.permission.POST_NOTIFICATIONS)
        } else {
            askVpn()
        }
    }

    /** Разрешение на VPN спрашивается один раз; потом prepare возвращает null — подключаемся сразу. */
    private fun askVpn() {
        val consent = VpnService.prepare(this)
        if (consent == null) {
            GatewayVpnService.connect(this)
        } else {
            vpnPermission.launch(consent)
        }
    }

    private fun openVpnSettings() {
        try {
            startActivity(Intent(Settings.ACTION_VPN_SETTINGS))
        } catch (_: ActivityNotFoundException) {
            startActivity(Intent(Settings.ACTION_WIRELESS_SETTINGS))
        }
    }

    companion object {
        /** Раздел, который открыть сразу (из уведомления). */
        const val EXTRA_TAB = "com.privategateway.client.TAB"
        const val TAB_SERVERS = "servers"
    }

    private fun clipboardText(): String? {
        val clip = getSystemService(ClipboardManager::class.java)?.primaryClip ?: return null
        return if (clip.itemCount > 0) clip.getItemAt(0).coerceToText(this)?.toString() else null
    }
}

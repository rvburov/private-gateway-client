package com.privategateway.client.ui

import android.Manifest
import android.content.pm.PackageManager
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.PickVisualMediaRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.core.content.ContextCompat
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.privategateway.client.R
import com.privategateway.client.inspect.InspectorViewModel
import com.privategateway.client.ui.check.CheckScreen
import com.privategateway.client.ui.check.InspectorActions
import com.privategateway.client.ui.home.HomeActions
import com.privategateway.client.ui.home.HomeScreen
import com.privategateway.client.ui.qr.QrCameraScreen
import com.privategateway.client.ui.servers.CardActions
import com.privategateway.client.ui.servers.ImportScreen
import com.privategateway.client.ui.servers.ServerCardScreen
import com.privategateway.client.ui.servers.ServersActions
import com.privategateway.client.ui.servers.ServersScreen
import com.privategateway.client.ui.servers.ServersViewModel
import com.privategateway.client.ui.settings.SettingsScreen
import com.privategateway.client.ui.settings.SettingsViewModel
import com.privategateway.client.ui.subscriptions.SubscriptionsActions
import com.privategateway.client.ui.subscriptions.SubscriptionsScreen
import com.privategateway.client.ui.subscriptions.SubscriptionsViewModel
import com.privategateway.client.ui.theme.LocalPgToast
import com.privategateway.client.ui.theme.PgBackground
import com.privategateway.client.ui.theme.PgToast
import com.privategateway.client.ui.theme.PgToastState
import kotlinx.coroutines.flow.merge

/** Разделы островка-меню: Главная · Серверы · [кнопка-щит] · Проверка · Настройки. */
enum class Tab {
    Home,
    Servers,
    Check,
    Settings,
}

/** Действия окна, которые требуют Android (разрешение на VPN, системные настройки). */
class AppActions(
    val power: () -> Unit,
    val disconnect: () -> Unit,
    val openVpnSettings: () -> Unit,
    val clipboard: () -> String?,
    /** Скопировать ссылку сервера: в буфере она помечена как секрет (Android не показывает ее в подсказке). */
    val copySecret: (String) -> Unit = {},
    /** Отправить ссылку сервера — системный выбор программы. */
    val send: (String) -> Unit = {},
    /** Скопировать текст в буфер обычным текстом (IP с главной). */
    val copy: (String) -> Unit = {},
)

@Composable
fun AppScreen(
    viewModel: MainViewModel,
    settings: SettingsViewModel,
    servers: ServersViewModel,
    subscriptions: SubscriptionsViewModel,
    actions: AppActions,
    inspector: InspectorViewModel,
) {
    val ui by viewModel.ui.collectAsStateWithLifecycle()
    val importing by viewModel.importing.collectAsStateWithLifecycle()
    val core by viewModel.core.collectAsStateWithLifecycle()
    val query by servers.query.collectAsStateWithLifecycle()
    var tab by rememberSaveable { mutableStateOf(Tab.Home) }
    var adding by rememberSaveable { mutableStateOf(false) }
    // Сканер QR камерой — поверх «Добавить».
    var scanning by rememberSaveable { mutableStateOf(false) }
    // Открытая карточка сервера (доска 11) — страница поверх списка серверов.
    var card by rememberSaveable { mutableStateOf<String?>(null) }
    val requested by viewModel.requestedTab.collectAsStateWithLifecycle()
    val context = LocalContext.current
    // Сообщения окна — своей подсказкой сверху (.toast макета), а не системным Toast: 1,8 с после действия, 6 с о событии.
    val toast = remember { PgToastState() }
    LaunchedEffect(viewModel, subscriptions) {
        merge(viewModel.messages, subscriptions.messages).collect {
            toast.show(context.getString(it.text, *it.args), long = it.long)
        }
    }
    LaunchedEffect(requested) {
        requested?.let {
            tab = it
            adding = false
            card = null
            viewModel.tabShown()
        }
    }
    val closeImport: () -> Unit = {
        adding = false
        scanning = false
        viewModel.closeImport()
    }
    val hasCamera = remember(context) { context.packageManager.hasSystemFeature(PackageManager.FEATURE_CAMERA_ANY) }

    CompositionLocalProvider(LocalPgToast provides toast) {
        PgBackground {
            if (adding) {
                BackHandler(onBack = closeImport)
                // Системный выбор картинки: приложению — только выбранная, без доступа ко всем фото.
                val picker = rememberLauncherForActivityResult(ActivityResultContracts.PickVisualMedia()) { image ->
                    image?.let(viewModel::previewQr)
                }
                // Доступ к камере спрашивается по нажатию «Сканировать камерой», а не при запуске; отказ — подсказка в карточке QR.
                val cameraAccess = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
                    if (granted) {
                        scanning = true
                    } else {
                        viewModel.cameraProblem(R.string.android_import_qr_camera_denied)
                    }
                }
                if (scanning) {
                    QrCameraScreen(
                        onFound = { text ->
                            scanning = false
                            viewModel.preview(text)
                        },
                        onFailed = {
                            scanning = false
                            viewModel.cameraProblem(R.string.android_import_qr_camera_failed)
                        },
                        onClose = { scanning = false },
                    )
                } else {
                    val scan = {
                        if (ContextCompat.checkSelfPermission(context, Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED) {
                            scanning = true
                        } else {
                            cameraAccess.launch(Manifest.permission.CAMERA)
                        }
                    }
                    ImportScreen(
                        importing,
                        actions.clipboard,
                        viewModel::edit,
                        viewModel::preview,
                        viewModel::name,
                        viewModel::add,
                        closeImport,
                        onScan = scan.takeIf { hasCamera },
                    ) {
                        picker.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageOnly))
                    }
                }
            } else {
                BackHandler(enabled = tab != Tab.Home) {
                    if (tab == Tab.Servers && card != null) {
                        card = null
                    } else {
                        tab = Tab.Home
                    }
                }

                val power: () -> Unit = {
                    when (ui.power) {
                        PowerAction.Toggle -> actions.power()
                        PowerAction.Add -> adding = true
                        PowerAction.Choose -> tab = Tab.Servers
                    }
                }
                val open = card
                when (tab) {
                    Tab.Home -> {
                        // Скорость туннеля читается, только пока главная на экране.
                        val speed by viewModel.speed.collectAsStateWithLifecycle()
                        HomeScreen(
                            ui,
                            onServers = { tab = Tab.Servers },
                            onCheck = { tab = Tab.Check },
                            onAdd = { adding = true },
                            onSelect = viewModel::select,
                            onRefresh = viewModel::refreshLatency,
                            speed = speed,
                            actions = HomeActions(
                                verify = viewModel::verify,
                                // «Повторить» — то же, что щит в «Ошибке»: подключить снова к выбранному.
                                retry = power,
                                vpnSettings = actions.openVpnSettings,
                                copy = actions.copy,
                            ),
                        )
                    }

                    Tab.Servers -> if (open != null) {
                        ServerCardScreen(
                            ui,
                            open,
                            CardActions(
                                back = { card = null },
                                connect = { id -> viewModel.connectTo(id, actions.power) },
                                favorite = viewModel::setFavorite,
                                rename = viewModel::rename,
                                delete = { id ->
                                    card = null
                                    viewModel.delete(id)
                                },
                                address = viewModel::address,
                                share = viewModel::shareLink,
                                copy = actions.copySecret,
                                send = actions.send,
                            ),
                        )
                    } else {
                        ServersScreen(
                            ui,
                            query,
                            ServersActions(
                                select = viewModel::select,
                                open = { card = it },
                                add = { adding = true },
                                refresh = viewModel::refreshLatency,
                                search = servers::search,
                                tab = servers::tab,
                                toggleProtocol = servers::toggleProtocol,
                                sort = servers::sort,
                                reset = servers::reset,
                            ),
                        )
                    }

                    Tab.Check -> {
                        val inspect by inspector.ui.collectAsStateWithLifecycle()
                        CheckScreen(
                            ui,
                            core,
                            viewModel::loadCoreVersion,
                            inspect,
                            InspectorActions(inspector::measureIp, inspector::describe, inspector::kind, inspector::target, inspector::port, inspector::run),
                        )
                    }
                    Tab.Settings -> {
                        val tunnel by settings.tunnel.collectAsStateWithLifecycle()
                        val behavior by settings.behavior.collectAsStateWithLifecycle()
                        val page by settings.requestedPage.collectAsStateWithLifecycle()
                        val system by settings.systemVpn.collectAsStateWithLifecycle()
                        SettingsScreen(
                            core,
                            viewModel::loadCoreVersion,
                            actions.openVpnSettings,
                            tunnel,
                            settings::setTunnel,
                            behavior,
                            settings::setBehavior,
                            page,
                            settings::pageShown,
                            system,
                        ) { back ->
                            val state by subscriptions.ui.collectAsStateWithLifecycle()
                            SubscriptionsScreen(
                                state,
                                ui,
                                SubscriptionsActions(
                                    settings = subscriptions::setSettings,
                                    add = { adding = true },
                                    update = subscriptions::update,
                                    edit = subscriptions::edit,
                                    remove = subscriptions::remove,
                                ),
                                back,
                            )
                        }
                    }

                }

                // Пункт островка всегда ведет на свой экран: «Серверы» — к списку, открытая карточка закрывается (как на iOS).
                val select: (Tab) -> Unit = {
                    tab = it
                    card = null
                }
                Island(
                    tab,
                    ui.status.state,
                    hasServer = ui.inUse != null,
                    onTab = select,
                    onPower = power,
                    modifier = Modifier.align(Alignment.BottomCenter),
                    look = StateLook.of(ui.view),
                )
            }

            PgToast(toast, Modifier.align(Alignment.TopCenter))
        }
    }
}

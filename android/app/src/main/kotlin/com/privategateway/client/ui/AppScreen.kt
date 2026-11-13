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
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalUriHandler
import androidx.compose.ui.res.stringResource
import androidx.core.content.ContextCompat
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.privategateway.client.core.vpn.TunnelErrors
import com.privategateway.client.core.vpn.TunnelState
import com.privategateway.client.R
import com.privategateway.client.account.AccountInfo
import com.privategateway.client.account.AccountState
import com.privategateway.client.inspect.CheckViewModel
import com.privategateway.client.inspect.InspectorViewModel
import com.privategateway.client.ui.account.AccountViewModel
import com.privategateway.client.ui.account.LoginActions
import com.privategateway.client.ui.account.LoginScreen
import com.privategateway.client.ui.check.CheckActions
import com.privategateway.client.ui.check.CheckReport
import com.privategateway.client.ui.check.CheckScreen
import com.privategateway.client.ui.check.InspectorActions
import com.privategateway.client.ui.home.HomeActions
import com.privategateway.client.ui.home.HomeScreen
import com.privategateway.client.ui.home.OtherVpnActions
import com.privategateway.client.ui.home.OtherVpnScreen
import com.privategateway.client.ui.qr.QrCameraScreen
import com.privategateway.client.ui.servers.CardActions
import com.privategateway.client.ui.servers.ImportActions
import com.privategateway.client.ui.servers.ImportFile
import com.privategateway.client.ui.servers.ImportScreen
import com.privategateway.client.ui.servers.ServerCardScreen
import com.privategateway.client.ui.servers.ServersActions
import com.privategateway.client.ui.servers.ServersScreen
import com.privategateway.client.ui.servers.ServersViewModel
import com.privategateway.client.ui.servers.importDone
import com.privategateway.client.ui.settings.BackupPage
import com.privategateway.client.ui.settings.BackupViewModel
import com.privategateway.client.ui.settings.SettingsPage
import com.privategateway.client.ui.settings.SettingsScreen
import com.privategateway.client.ui.settings.SettingsViewModel
import com.privategateway.client.ui.subscriptions.SubscriptionsActions
import com.privategateway.client.ui.subscriptions.SubscriptionsScreen
import com.privategateway.client.ui.subscriptions.SubscriptionsViewModel
import com.privategateway.client.ui.theme.LocalPgToast
import com.privategateway.client.ui.theme.PgBackground
import com.privategateway.client.ui.theme.PgDialog
import com.privategateway.client.ui.theme.PgIcons
import com.privategateway.client.ui.theme.PgToast
import com.privategateway.client.ui.theme.PgToastState
import com.privategateway.client.ui.theme.PgTone
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.emptyFlow
import kotlinx.coroutines.flow.merge
import java.time.Instant

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
    /** Скопировать текст в буфер обычным текстом (IP с главной). */
    val copy: (String) -> Unit = {},
    /** Язык приложения («ru», «en») — Android 13+; null — только язык системы. */
    val language: ((String) -> Unit)? = null,
    /** Пауза для входа в Wi-Fi: туннель опускается на 5 минут (решение 557). */
    val pause: () -> Unit = {},
    /** События о защите при открытом окне — «Заголовок · текст» (доска 19). */
    val events: Flow<String> = emptyFlow(),
)

@Composable
fun AppScreen(
    viewModel: MainViewModel,
    settings: SettingsViewModel,
    servers: ServersViewModel,
    subscriptions: SubscriptionsViewModel,
    actions: AppActions,
    inspector: InspectorViewModel,
    checks: CheckViewModel,
    backups: BackupViewModel? = null,
    account: AccountViewModel? = null,
) {
    val ui by viewModel.ui.collectAsStateWithLifecycle()
    // Вход в аккаунт сайта (решение 621): без [account] (тесты экранов) — вход есть; пока вход не прочитан (null) — пустой
    // фон, чтобы экран входа не мелькал у тех, кто вошел.
    val signedInState: AccountState? = if (account != null) account.state.collectAsStateWithLifecycle().value else SIGNED_IN
    val signedIn = signedInState?.signedIn
    val importing by viewModel.importing.collectAsStateWithLifecycle()
    val core by viewModel.core.collectAsStateWithLifecycle()
    val query by servers.query.collectAsStateWithLifecycle()
    var tab by rememberSaveable { mutableStateOf(Tab.Home) }
    var adding by rememberSaveable { mutableStateOf(false) }
    // «Добавить профиль» открыт из «Профилей и подписок»: «Назад» возвращает на эту страницу, а не на главную «Настроек».
    var fromSubscriptions by rememberSaveable { mutableStateOf(false) }
    // Сканер QR камерой — поверх «Добавить».
    var scanning by rememberSaveable { mutableStateOf(false) }
    // Открытая карточка сервера (доска 11) — нижний лист поверх списка серверов.
    var card by rememberSaveable { mutableStateOf<String?>(null) }
    val requested by viewModel.requestedTab.collectAsStateWithLifecycle()
    val context = LocalContext.current
    // Сообщения окна — своей подсказкой сверху (.toast макета), а не системным Toast: 1,8 с после действия, 6 с о событии.
    val toast = remember { PgToastState() }
    LaunchedEffect(viewModel, subscriptions, checks) {
        merge(viewModel.messages, subscriptions.messages, checks.messages).collect {
            toast.show(it.render(context), long = it.long)
        }
    }
    LaunchedEffect(actions) {
        actions.events.collect { toast.show(it, long = true) }
    }
    LaunchedEffect(backups) {
        backups?.messages?.collect { toast.show(it.render(context), long = it.long) }
    }
    LaunchedEffect(requested) {
        requested?.let {
            tab = it
            adding = false
            fromSubscriptions = false
            card = null
            viewModel.tabShown()
        }
    }
    // «Назад» — туда, откуда открыли «Добавить профиль»; страница «Профили и подписки» без этого закрылась бы вместе с «Настройками».
    val closeImport: () -> Unit = {
        if (fromSubscriptions) {
            settings.request(SettingsPage.Subscriptions)
        }

        adding = false
        scanning = false
        fromSubscriptions = false
        viewModel.closeImport()
    }
    // Добавлено — итог подсказкой «Добавлено N» и экран, откуда пришли: главная, «Серверы» или «Профили и подписки»
    // (DECISIONS.md, п. 21.7); первый сервер уже выбран, если выбора не было (MainViewModel.add). Язык форм чисел — из
    // LocalConfiguration: конфигурацию через LocalContext Compose не отслеживает (и Lint это запрещает).
    val language = Texts.language(LocalConfiguration.current.locales)
    LaunchedEffect(importing.added) {
        val done = importDone(importing, language) ?: return@LaunchedEffect
        toast.show(done.render(context))
        closeImport()
    }
    val hasCamera = remember(context) { context.packageManager.hasSystemFeature(PackageManager.FEATURE_CAMERA_ANY) }
    // Первый запуск (доска 10): до первого сервера или «Позже»; пока флаг не прочитан (null) — не показывать, чтобы
    // приветствие не мелькало у тех, кто его прошел. Появился сервер — первый запуск пройден.
    val onboarded by settings.onboardingDone.collectAsStateWithLifecycle()
    val hasServers = ui.servers.isNotEmpty()
    LaunchedEffect(onboarded, ui.loaded, hasServers) {
        if (onboarded == false && ui.loaded && hasServers) {
            settings.finishOnboarding()
        }
    }
    val welcome = onboarded == false && ui.loaded && !hasServers
    // Пауза для входа в Wi-Fi (решение 557): «Вход в Wi-Fi» на главной и «Открыть» в «Защите и утечках» спрашивают, можно ли
    // открыть интернет без VPN на 5 минут. Защита выключена — пауза не нужна; «Блокировать подключения без VPN» — не поможет.
    var askingPause by rememberSaveable { mutableStateOf(false) }
    val captive: () -> Unit = {
        when {
            ui.status.state == TunnelState.Off || ui.status.state == TunnelState.Error -> toast.show(context.getString(R.string.pause_off))
            ui.status.lockdown == true -> toast.show(context.getString(R.string.android_pause_lockdown), long = true)
            else -> askingPause = true
        }
    }

    val links = LocalUriHandler.current

    CompositionLocalProvider(LocalPgToast provides toast) {
        PgBackground {
            if (signedIn == null) {
                // Вход еще не прочитан: только фон.
            } else if (!signedIn && account != null) {
                val form by account.ui.collectAsStateWithLifecycle()
                LoginScreen(
                    form,
                    signedInState.notice,
                    LoginActions(
                        email = account::email,
                        password = account::password,
                        code = account::code,
                        toggleRecovery = account::toggleRecovery,
                        submit = account::login,
                        site = { runCatching { links.openUri(it) } },
                        back = account::back,
                    ),
                    actions.language,
                )
            } else if (adding) {
                BackHandler(onBack = closeImport)
                // Системный выбор картинки: приложению — только выбранная, без доступа ко всем фото.
                val picker = rememberLauncherForActivityResult(ActivityResultContracts.PickVisualMedia()) { image ->
                    image?.let(viewModel::previewQr)
                }
                // Системное окно «Открыть»: приложению — только выбранный файл, без разрешения на память.
                val files = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { file ->
                    file?.let(viewModel::previewFile)
                }
                // Доступ к камере спрашивается по нажатию «Сканировать камерой», а не при запуске; отказ — подсказка под карточками.
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
                        ImportActions(
                            source = viewModel::selectSource,
                            clipboard = { viewModel.fromClipboard(actions.clipboard()) },
                            text = viewModel::edit,
                            check = viewModel::preview,
                            name = viewModel::name,
                            add = viewModel::add,
                            close = closeImport,
                            pickImage = { picker.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageOnly)) },
                            pickFile = { files.launch(ImportFile.TYPES) },
                            scan = scan.takeIf { hasCamera },
                        ),
                    )
                }
            } else if (welcome) {
                WelcomeScreen(onAdd = { adding = true }, onDone = settings::finishOnboarding, language = actions.language)
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
                    Tab.Home -> if (ui.status.state == TunnelState.Error && ui.status.error == TunnelErrors.OTHER_VPN) {
                        // VPN телефона занят другим приложением (PGC-NET-010) — страница проблемы вместо главной (DECISIONS.md, п. 4).
                        OtherVpnScreen(
                            OtherVpnActions(
                                retry = {
                                    if (viewModel.otherVpnRunning()) {
                                        toast.show(context.getString(R.string.problem_conflict_still_mobile), long = true)
                                    } else {
                                        power()
                                    }
                                },
                                vpnSettings = actions.openVpnSettings,
                                details = { tab = Tab.Check },
                            ),
                        )
                    } else {
                        // Скорость туннеля читается, только пока главная на экране.
                        val speed by viewModel.speed.collectAsStateWithLifecycle()
                        HomeScreen(
                            ui,
                            onServers = { tab = Tab.Servers },
                            // Плитка «Пинг» ведет к «Задержке» на «Проверке» и меряет ее заново (доски 01 и 7).
                            onCheck = {
                                tab = Tab.Check
                                checks.focusDelay()
                            },
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
                                captive = captive,
                                // «Вернуть защиту» на паузе — то же, что щит: подключить к выбранному серверу.
                                resume = power,
                            ),
                        )
                    }

                    Tab.Servers -> {
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
                        // Карточка — нижний лист поверх списка (доска 11); «Сервер удален» говорит модель после удаления.
                        if (open != null) {
                            ServerCardScreen(
                                ui,
                                open,
                                CardActions(
                                    close = { card = null },
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
                                    exit = viewModel::exit,
                                ),
                            )
                        }
                    }

                    Tab.Check -> {
                        val inspect by inspector.ui.collectAsStateWithLifecycle()
                        val check by checks.ui.collectAsStateWithLifecycle()
                        val tunnel by settings.tunnel.collectAsStateWithLifecycle()
                        // «Экспорт отчета»: системное окно «Сохранить» — приложению только выбранный файл, без разрешения на память.
                        val report = rememberLauncherForActivityResult(ActivityResultContracts.CreateDocument("text/plain")) { file ->
                            file?.let { checks.save(it, CheckReport.build(context, ui, check, inspect.networks, core, tunnel, language)) }
                        }
                        // Проверить: внешний IP заново (его ведет MainViewModel, как для главной) и то, что проверка меряет сама.
                        val run = {
                            viewModel.verify()
                            checks.run()
                        }
                        CheckScreen(
                            ui,
                            core,
                            viewModel::loadCoreVersion,
                            check,
                            CheckActions(
                                open = run,
                                run = run,
                                export = { report.launch(CheckReport.fileName(System.currentTimeMillis())) },
                                measureDelay = checks::measureDelay,
                                delayShown = checks::delayShown,
                            ),
                            inspect,
                            InspectorActions(inspector::measureIp, inspector::describe, inspector::kind, inspector::target, inspector::port, inspector::run),
                            tunnel,
                        )
                    }
                    Tab.Settings -> {
                        val tunnel by settings.tunnel.collectAsStateWithLifecycle()
                        val behavior by settings.behavior.collectAsStateWithLifecycle()
                        val page by settings.requestedPage.collectAsStateWithLifecycle()
                        val system by settings.systemVpn.collectAsStateWithLifecycle()
                        val updates by settings.updates.collectAsStateWithLifecycle()
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
                            onCheck = { tab = Tab.Check },
                            language = actions.language,
                            onCaptive = captive,
                            installedApps = { settings.installedApps() },
                            updates = updates,
                            onCheckUpdates = settings::checkUpdates,
                            onInstallUpdate = settings::installUpdate,
                            onAboutShown = settings::aboutShown,
                            backup = { back ->
                                if (backups != null) {
                                    BackupPage(backups, back)
                                }
                            },
                            // Без модели входа (тесты экранов) карточки «Аккаунт» нет.
                            account = if (account != null) signedInState.info else null,
                            onSignOut = { account?.logout() },
                        ) { back ->
                            val state by subscriptions.ui.collectAsStateWithLifecycle()
                            SubscriptionsScreen(
                                state,
                                ui,
                                SubscriptionsActions(
                                    settings = subscriptions::setSettings,
                                    add = {
                                        fromSubscriptions = true
                                        adding = true
                                    },
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

            if (askingPause) {
                PgDialog(
                    stringResource(R.string.pause_dialog_title),
                    stringResource(R.string.pause_dialog_text),
                    PgIcons.Clock,
                    PgTone.Caution,
                    stringResource(R.string.pause_dialog_keep),
                    { askingPause = false },
                    stringResource(R.string.pause_dialog_open),
                    {
                        askingPause = false
                        actions.pause()
                        toast.show(context.getString(R.string.pause_started), long = true)
                    },
                    { askingPause = false },
                    "dialog.pause",
                    secondaryDanger = true,
                )
            }

            // Пока открыта карточка сервера, подсказку показывает ее лист: окно листа закрывает собой это окно.
            if (signedIn != true || adding || welcome || tab != Tab.Servers || card == null) {
                PgToast(toast, Modifier.align(Alignment.TopCenter))
            }
        }
    }
}

/** Без модели входа (тесты экранов) окно считает, что вход есть. */
private val SIGNED_IN = AccountState(AccountInfo("", "", "", Instant.MAX, null))

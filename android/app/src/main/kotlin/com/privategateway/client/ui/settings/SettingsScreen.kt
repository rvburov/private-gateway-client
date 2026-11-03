package com.privategateway.client.ui.settings

import androidx.activity.compose.BackHandler
import androidx.annotation.StringRes
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
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
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import com.privategateway.client.BuildConfig
import com.privategateway.client.R
import com.privategateway.client.core.notify.NotificationLevel
import com.privategateway.client.core.settings.TunnelSettings
import com.privategateway.client.core.vpn.ReconnectPolicy
import com.privategateway.client.core.vpn.SystemVpn
import com.privategateway.client.core.vpn.SystemVpnSource
import com.privategateway.client.core.vpn.SystemVpnView
import com.privategateway.client.data.PhoneApp
import com.privategateway.client.licenses.OpenComponents
import com.privategateway.client.ui.Texts
import com.privategateway.client.ui.theme.PgCard
import com.privategateway.client.ui.theme.PgColor
import com.privategateway.client.ui.theme.PgIcons
import com.privategateway.client.ui.theme.PgLayout
import com.privategateway.client.ui.theme.PgLinkRow
import com.privategateway.client.ui.theme.PgSegments
import com.privategateway.client.ui.theme.PgSettingSwitch
import com.privategateway.client.ui.theme.PgSpace
import com.privategateway.client.ui.theme.PgType
import com.privategateway.client.ui.theme.pgTopDivider

/** Страницы «Настроек»: список разделов и вложенные страницы (на телефоне — по одной на экран, доска 25). */
enum class SettingsPage {
    Main,
    Protection,
    Core,
    Routing,
    Subscriptions,
    Backup,
    About,
}

/**
 * «Настройки» (доска 4): «Основные» одной карточкой и «Для опытных» строками-ссылками, внизу — версии приложения и ядра.
 * Режима нет — на телефоне только VPN (решение 527); блок при обрыве — системный (Постоянная VPN и блок без VPN включает
 * только пользователь в настройках Android), поэтому вместо переключателя — что сообщил Android и переход в его настройки.
 * [onCheck] — «Проверка и логи»: вкладка «Проверка» островка. [language] — выбрать язык приложения (Android 13+); null —
 * язык как в системе. [requested] — открыть страницу сразу (кнопка уведомления «Открыть» у подписки); открыта — [onRequested].
 * [installedApps] — программы телефона для «Программы мимо VPN» («Маршрутизация»).
 */
@Composable
fun SettingsScreen(
    core: String?,
    loadCore: () -> Unit,
    openVpnSettings: () -> Unit,
    tunnel: TunnelSettings,
    onTunnel: (TunnelSettings) -> Unit,
    behavior: Behavior = Behavior(),
    onBehavior: (Behavior) -> Unit = {},
    requested: SettingsPage? = null,
    onRequested: () -> Unit = {},
    system: SystemVpnView = SystemVpnView(SystemVpn(), SystemVpnSource.Unknown),
    onCheck: () -> Unit = {},
    language: ((String) -> Unit)? = null,
    installedApps: suspend () -> List<PhoneApp> = { emptyList() },
    onCaptive: () -> Unit = {},
    backup: @Composable (onBack: () -> Unit) -> Unit = {},
    subscriptions: @Composable (onBack: () -> Unit) -> Unit = {},
) {
    var page by rememberSaveable { mutableStateOf(SettingsPage.Main) }
    LaunchedEffect(requested) {
        if (requested != null) {
            page = requested
            onRequested()
        }
    }

    when (page) {
        SettingsPage.Protection -> {
            BackHandler { page = SettingsPage.Main }
            ProtectionScreen(
                tunnel,
                onTunnel,
                onBack = { page = SettingsPage.Main },
                system = system,
                openVpnSettings = openVpnSettings,
                onCaptive = onCaptive,
            )
        }

        SettingsPage.Core -> {
            BackHandler { page = SettingsPage.Main }
            CoreScreen(core, loadCore, tunnel, onTunnel, behavior, onBehavior) { page = SettingsPage.Main }
        }

        SettingsPage.Routing -> {
            BackHandler { page = SettingsPage.Main }
            RoutingScreen(
                tunnel,
                onTunnel,
                onBack = { page = SettingsPage.Main },
                onProtection = { page = SettingsPage.Protection },
                installedApps = installedApps,
            )
        }

        SettingsPage.Subscriptions -> {
            BackHandler { page = SettingsPage.Main }
            subscriptions { page = SettingsPage.Main }
        }

        SettingsPage.Backup -> {
            BackHandler { page = SettingsPage.Main }
            backup { page = SettingsPage.Main }
        }

        // Лицензии — лист поверх «О программе» (DECISIONS.md, п. 1): «Назад» и затемнение закрывают лист, а не страницу.
        SettingsPage.About -> {
            BackHandler { page = SettingsPage.Main }
            var licenses by rememberSaveable { mutableStateOf(false) }
            AboutScreen(core, loadCore, onLicenses = { licenses = true }, onReport = onCheck) { page = SettingsPage.Main }
            if (licenses) {
                val assets = LocalContext.current.assets
                val components = remember(assets) { OpenComponents.read(assets) }
                LicensesSheet(components, { OpenComponents.text(assets, it) }) { licenses = false }
            }
        }

        SettingsPage.Main -> MainSettings(core, loadCore, openVpnSettings, system, behavior, onBehavior, onCheck, language) { page = it }
    }
}

/**
 * Поведение приложения: уведомления и «Подключаться при запуске» («Основные») и переподключение — строка группы «Соединение»
 * страницы «Подключение и ядро» (доска 6). Туннель оно не меняет, поэтому хранится не в настройках туннеля.
 */
data class Behavior(
    val notifications: NotificationLevel = NotificationLevel.DEFAULT,
    val reconnect: ReconnectPolicy = ReconnectPolicy.Backoff60Seconds,
    val connectOnLaunch: Boolean = false,
)

/** Поля карточек «Настроек» (Border.card Padding="16,4"): строки сами без боковых полей, полосы — внутри полей карточки. */
private val CardPadding = PaddingValues(horizontal = PgSpace.Card, vertical = PgSpace.Xxs)

/** Строка «Основных» (.card > .setting доски 4): 13 сверху и снизу. */
private val BasicRow = PaddingValues(vertical = 13.dp)

/** Языки приложения — те, тексты которых есть (RU и EN). */
private val Languages = listOf("ru", "en")

@Composable
private fun MainSettings(
    core: String?,
    loadCore: () -> Unit,
    openVpnSettings: () -> Unit,
    system: SystemVpnView,
    behavior: Behavior,
    onBehavior: (Behavior) -> Unit,
    onCheck: () -> Unit,
    language: ((String) -> Unit)?,
    onPage: (SettingsPage) -> Unit,
) {
    LaunchedEffect(Unit) { loadCore() }
    Column(
        Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .statusBarsPadding()
            .padding(horizontal = PgSpace.WindowSideMobile)
            .padding(bottom = PgLayout.ScrollBottomPadding)
            .testTag("settings"),
    ) {
        Text(stringResource(R.string.nav_settings), Modifier.padding(top = 18.dp, bottom = PgSpace.L), style = PgType.H1)

        GroupTitle(stringResource(R.string.settings_section_basic), top = 0.dp)
        PgCard(Modifier.fillMaxWidth(), contentPadding = CardPadding) {
            Basic(openVpnSettings, system, behavior, onBehavior, language)
        }

        GroupTitle(stringResource(R.string.settings_section_advanced), top = 18.dp)
        PgCard(Modifier.fillMaxWidth(), contentPadding = CardPadding) {
            Advanced(onCheck, onPage)
        }

        Footer(core)
    }
}

/**
 * «Основные»: блок при обрыве (что сообщил Android и откуда это известно; нажатие — настройки VPN), «Подключаться при
 * запуске», уведомления сегментами с подписью по выбору и язык. Переподключение — на странице «Подключение и ядро».
 */
@Composable
private fun Basic(
    openVpnSettings: () -> Unit,
    system: SystemVpnView,
    behavior: Behavior,
    onBehavior: (Behavior) -> Unit,
    language: ((String) -> Unit)?,
) {
    SystemVpnRow(
        stringResource(R.string.settings_ks_title),
        stringResource(R.string.android_settings_vpn_text),
        system.flags.lockdown,
        "settings.vpn.lockdown",
        "settings.ks",
        openVpnSettings,
        BasicRow,
        extra = { SystemVpnSourceText(system.source, "settings.vpn.source") },
    )

    PgSettingSwitch(
        stringResource(R.string.settings_connectOnLaunch_title),
        stringResource(R.string.settings_connectOnLaunch_text),
        behavior.connectOnLaunch,
        "settings.connectOnLaunch",
        { onBehavior(behavior.copy(connectOnLaunch = it)) },
        divider = true,
        padding = BasicRow,
    )

    Notifications(behavior, onBehavior)
    LanguageRow(language)
}

/** Уведомления (.setting.stacked): заголовок, подпись выбранного уровня, под ними три сегмента «Все · Только важные · Выключены». */
@Composable
private fun Notifications(behavior: Behavior, onBehavior: (Behavior) -> Unit) {
    Column(
        Modifier.fillMaxWidth().pgTopDivider(true).padding(BasicRow),
        verticalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        Column(verticalArrangement = Arrangement.spacedBy(3.dp)) {
            Text(stringResource(R.string.settings_notifications_title), color = PgColor.Text.Primary, style = PgType.BodyStrong)
            notifyNote(behavior.notifications)?.let {
                Text(stringResource(it), Modifier.testTag("notify.note"), color = PgColor.Text.Muted, style = PgType.Caption)
            }
        }

        PgSegments(NotificationLevel.entries, behavior.notifications, { stringResource(notifyLabel(it)) }, "notify", {
            onBehavior(behavior.copy(notifications = it))
        })
    }
}

/**
 * Язык (сегменты «Русский · English» шириной 190): выбран тот, на котором приложение сейчас. Выбрать свой язык приложению
 * Android дает с 13-й версии; раньше — только язык системы: сегменты недоступны, подпись говорит, где он меняется.
 */
@Composable
private fun LanguageRow(language: ((String) -> Unit)?) {
    // Язык телефона без своих текстов (немецкий и т. п.) — приложение на английском (values/), и выбран English.
    val current = if (Texts.language(LocalConfiguration.current.locales) == "ru") "ru" else "en"
    Column(
        Modifier.fillMaxWidth().pgTopDivider(true).padding(BasicRow),
        verticalArrangement = Arrangement.spacedBy(PgSpace.Xs),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(stringResource(R.string.settings_language_title), Modifier.weight(1f), color = PgColor.Text.Primary, style = PgType.BodyStrong)
            PgSegments(
                Languages,
                current,
                { stringResource(if (it == "ru") R.string.settings_language_ru else R.string.settings_language_en) },
                "language",
                { language?.invoke(it) },
                Modifier.width(190.dp),
                enabled = language != null,
            )
        }

        if (language == null) {
            Text(stringResource(R.string.android_settings_language), Modifier.testTag("language.system"), color = PgColor.Text.Muted, style = PgType.Caption)
        }
    }
}

/** «Для опытных»: строки-ссылки в порядке доски 4 — только на страницы, которые у телефона есть. */
@Composable
private fun Advanced(onCheck: () -> Unit, onPage: (SettingsPage) -> Unit) {
    AdvancedRow(PgIcons.AdvProtection, R.string.settings_adv_protection_title, R.string.settings_adv_protection_text, "settings.protection", false) {
        onPage(SettingsPage.Protection)
    }

    AdvancedRow(PgIcons.AdvCore, R.string.settings_adv_core_title, R.string.settings_adv_core_text, "settings.core", true) {
        onPage(SettingsPage.Core)
    }

    AdvancedRow(PgIcons.AdvRouting, R.string.settings_adv_routing_title, R.string.settings_adv_routing_text, "settings.routing", true) {
        onPage(SettingsPage.Routing)
    }

    AdvancedRow(PgIcons.AdvProfiles, R.string.settings_adv_profiles_title, R.string.settings_adv_profiles_text, "settings.profiles", true) {
        onPage(SettingsPage.Subscriptions)
    }

    AdvancedRow(PgIcons.AdvCheck, R.string.settings_adv_check_title, R.string.settings_adv_check_text, "settings.check", true, onCheck)
    AdvancedRow(PgIcons.AdvBackup, R.string.settings_adv_backup_title, R.string.settings_adv_backup_text, "settings.backup", true) {
        onPage(SettingsPage.Backup)
    }

    AdvancedRow(PgIcons.AdvAbout, R.string.settings_adv_about_title, R.string.settings_adv_about_text, "settings.about", true) {
        onPage(SettingsPage.About)
    }
}

/** Строка «Для опытных»: плитка 36 со значком 20, подпись в одну строку, полосы между строками (решение 486). */
@Composable
private fun AdvancedRow(icon: String, @StringRes title: Int, @StringRes text: Int, tag: String, divider: Boolean, onClick: () -> Unit) {
    PgLinkRow(
        icon,
        stringResource(title),
        stringResource(text),
        tag,
        onClick,
        divider = divider,
        padding = PaddingValues(vertical = 10.dp),
        tile = 36.dp,
        iconSize = 20.dp,
    )
}

/**
 * Подпись внизу (settings.footer десктопа без службы — ее у телефона нет): «Private Gateway Client 0.1.0 · ядро: Xray-core
 * 26.10.21»; версия ядра еще не прочитана — «—».
 */
@Composable
private fun Footer(core: String?) {
    val locale = LocalConfiguration.current.locales[0]
    val text = stringResource(R.string.about_name) + " " + BuildConfig.VERSION_NAME + " · " +
        stringResource(R.string.card_core).lowercase(locale) + ": Xray-core " + (core ?: "—")
    Text(
        text,
        Modifier.fillMaxWidth().padding(top = 14.dp).testTag("settings.footer"),
        color = PgColor.Text.Muted,
        style = PgType.Caption,
        textAlign = TextAlign.Center,
    )
}

@StringRes
private fun notifyLabel(level: NotificationLevel): Int = when (level) {
    NotificationLevel.All -> R.string.settings_notifications_all
    NotificationLevel.ImportantOnly -> R.string.settings_notifications_important
    NotificationLevel.Off -> R.string.settings_notifications_off
}

/** Подпись уровня уведомлений. У «Выключены» подписи десктопа — про значок в трее; трея у телефона нет, своей подписи пока нет. */
@StringRes
private fun notifyNote(level: NotificationLevel): Int? = when (level) {
    NotificationLevel.All -> R.string.settings_notifications_all_note
    NotificationLevel.ImportantOnly -> R.string.settings_notifications_important_note
    NotificationLevel.Off -> null
}

package com.privategateway.client.ui.settings

import android.os.Build
import androidx.activity.compose.BackHandler
import androidx.annotation.StringRes
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.unit.dp
import com.privategateway.client.BuildConfig
import com.privategateway.client.R
import com.privategateway.client.core.notify.NotificationLevel
import com.privategateway.client.core.settings.TunnelSettings
import com.privategateway.client.core.vpn.ReconnectPolicy
import com.privategateway.client.ui.theme.Overline
import com.privategateway.client.ui.theme.PgCard
import com.privategateway.client.ui.theme.PgChoiceRow
import com.privategateway.client.ui.theme.PgColor
import com.privategateway.client.ui.theme.PgIcon
import com.privategateway.client.ui.theme.PgIcons
import com.privategateway.client.ui.theme.PgLayout
import com.privategateway.client.ui.theme.PgRadius
import com.privategateway.client.ui.theme.PgSpace
import com.privategateway.client.ui.theme.PgType

/** Страницы «Настроек»: список разделов и вложенные страницы (на телефоне — по одной на экран, доска 25). */
enum class SettingsPage {
    Main,
    Protection,
    Subscriptions,
}

/**
 * «Настройки»: разделы («Защита и утечки», «Профили и подписки»), «Уведомления» и «Переподключение» (как у десктопа:
 * главная страница и «Подключение и ядро»), VPN в настройках Android (Постоянная VPN и блок без VPN включает только
 * пользователь) и «О программе». Язык — системный или выбранный для приложения в настройках Android 13+. [requested] —
 * открыть страницу сразу (кнопка уведомления «Открыть» у подписки); открыта — [onRequested].
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
            ProtectionScreen(tunnel, onTunnel) { page = SettingsPage.Main }
        }

        SettingsPage.Subscriptions -> {
            BackHandler { page = SettingsPage.Main }
            subscriptions { page = SettingsPage.Main }
        }

        SettingsPage.Main -> MainSettings(core, loadCore, openVpnSettings, behavior, onBehavior) { page = it }
    }
}

/** Уведомления и переподключение — то, что на десктопе в главных настройках и в «Подключении и ядре». */
data class Behavior(
    val notifications: NotificationLevel = NotificationLevel.DEFAULT,
    val reconnect: ReconnectPolicy = ReconnectPolicy.Backoff60Seconds,
)

@Composable
private fun MainSettings(
    core: String?,
    loadCore: () -> Unit,
    openVpnSettings: () -> Unit,
    behavior: Behavior,
    onBehavior: (Behavior) -> Unit,
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
        verticalArrangement = Arrangement.spacedBy(PgSpace.L),
    ) {
        Text(stringResource(R.string.nav_settings), style = PgType.H1, modifier = Modifier.padding(top = PgSpace.L))
        PgCard(Modifier.fillMaxWidth()) {
            SectionRow(PgIcons.AdvProtection, R.string.settings_adv_protection_title, R.string.settings_adv_protection_text, "settings.protection") {
                onPage(SettingsPage.Protection)
            }

            SectionRow(PgIcons.AdvProfiles, R.string.settings_adv_profiles_title, R.string.settings_adv_profiles_text, "settings.profiles") {
                onPage(SettingsPage.Subscriptions)
            }
        }

        PgCard(Modifier.fillMaxWidth()) {
            Text(stringResource(R.string.settings_notifications_title), style = PgType.Title)
            Spacer(Modifier.size(PgSpace.S))
            val levels = listOf(
                NotificationLevel.All to R.string.settings_notifications_all,
                NotificationLevel.ImportantOnly to R.string.settings_notifications_important,
                NotificationLevel.Off to R.string.settings_notifications_off,
            )
            for ((level, label) in levels) {
                PgChoiceRow(stringResource(label), null, behavior.notifications == level, "notify.${level.name}") {
                    onBehavior(behavior.copy(notifications = level))
                }
            }
        }

        PgCard(Modifier.fillMaxWidth()) {
            Text(stringResource(R.string.core_reconnect_title), style = PgType.Title)
            Spacer(Modifier.size(PgSpace.Xs))
            Text(stringResource(R.string.core_reconnect_text), color = PgColor.Text.Secondary, style = PgType.Small)
            Spacer(Modifier.size(PgSpace.S))
            val policies = listOf(
                ReconnectPolicy.Backoff60Seconds to R.string.core_reconnect_backoff,
                ReconnectPolicy.UpTo5Minutes to R.string.core_reconnect_5min,
                ReconnectPolicy.Never to R.string.core_reconnect_never,
            )
            for ((policy, label) in policies) {
                PgChoiceRow(stringResource(label), null, behavior.reconnect == policy, "reconnect.${policy.name}") {
                    onBehavior(behavior.copy(reconnect = policy))
                }
            }
        }

        PgCard(Modifier.fillMaxWidth()) {
            Text(stringResource(R.string.android_settings_vpn_title), style = PgType.Title)
            Spacer(Modifier.size(PgSpace.Xs))
            Text(stringResource(R.string.android_settings_vpn_text), color = PgColor.Text.Secondary, style = PgType.Small)
            Spacer(Modifier.size(PgSpace.M))
            val shape = RoundedCornerShape(PgRadius.Button)
            Box(
                Modifier
                    .fillMaxWidth()
                    .heightIn(min = PgLayout.TouchTarget)
                    .clip(shape)
                    .background(PgColor.Accent.Tint)
                    .clickable(role = Role.Button, onClick = openVpnSettings)
                    .testTag("settings.vpn"),
                contentAlignment = Alignment.Center,
            ) {
                Text(stringResource(R.string.android_settings_vpn_open), color = PgColor.Accent.Text, style = PgType.BodyStrong)
            }
        }

        PgCard(Modifier.fillMaxWidth()) {
            Overline(stringResource(R.string.about_name))
            Spacer(Modifier.size(PgSpace.Xs))
            Text(
                stringResource(R.string.about_version, BuildConfig.VERSION_NAME, "Android " + Build.VERSION.RELEASE, Build.SUPPORTED_ABIS.firstOrNull() ?: ""),
                style = PgType.Body,
            )
            Text(stringResource(R.string.card_core) + ": Xray-core " + (core ?: "—"), color = PgColor.Text.Secondary, style = PgType.Small)
        }

        Column(Modifier.fillMaxWidth(), horizontalAlignment = Alignment.CenterHorizontally) {
            Text(stringResource(R.string.settings_language_title) + ": " + stringResource(R.string.android_settings_language), color = PgColor.Text.Muted, style = PgType.Caption)
        }
    }
}

/** Раздел настроек: иконка, название, что внутри и стрелка — как список разделов десктопа (доска 4). */
@Composable
private fun SectionRow(icon: String, @StringRes title: Int, @StringRes text: Int, tag: String, onClick: () -> Unit) {
    Row(
        Modifier
            .fillMaxWidth()
            .heightIn(min = PgLayout.TouchTarget)
            .clip(RoundedCornerShape(PgRadius.Button))
            .clickable(role = Role.Button, onClick = onClick)
            .padding(vertical = PgSpace.Xs)
            .testTag(tag),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        PgIcon(icon, null, color = PgColor.Accent.Base, size = 22.dp)
        Spacer(Modifier.width(PgSpace.M))
        Column(Modifier.weight(1f)) {
            Text(stringResource(title), style = PgType.BodyStrong)
            Text(stringResource(text), color = PgColor.Text.Secondary, style = PgType.Caption)
        }

        PgIcon(PgIcons.ChevronRight, null, color = PgColor.Text.Muted, size = 20.dp)
    }
}

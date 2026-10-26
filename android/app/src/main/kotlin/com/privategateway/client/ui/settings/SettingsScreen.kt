package com.privategateway.client.ui.settings

import android.os.Build
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import com.privategateway.client.BuildConfig
import com.privategateway.client.R
import com.privategateway.client.ui.theme.Overline
import com.privategateway.client.ui.theme.PgCard
import com.privategateway.client.ui.theme.PgColor
import com.privategateway.client.ui.theme.PgLayout
import com.privategateway.client.ui.theme.PgRadius
import com.privategateway.client.ui.theme.PgSpace
import com.privategateway.client.ui.theme.PgType

/**
 * «Настройки»: VPN в настройках Android (Постоянная VPN и блок без VPN включает только пользователь) и «О программе».
 * Язык — системный или выбранный для приложения в настройках Android 13+; DNS, протокол IP и уведомления — пункт 9.2.
 */
@Composable
fun SettingsScreen(core: String?, loadCore: () -> Unit, openVpnSettings: () -> Unit) {
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

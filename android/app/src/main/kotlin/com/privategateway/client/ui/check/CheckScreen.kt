package com.privategateway.client.ui.check

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import com.privategateway.client.R
import com.privategateway.client.ui.MainUi
import com.privategateway.client.ui.ProtectionChips
import com.privategateway.client.ui.Texts
import com.privategateway.client.ui.theme.Overline
import com.privategateway.client.ui.theme.PgCard
import com.privategateway.client.ui.theme.PgColor
import com.privategateway.client.ui.theme.PgIcon
import com.privategateway.client.ui.theme.PgLayout
import com.privategateway.client.ui.theme.PgSpace
import com.privategateway.client.ui.theme.PgType
import com.privategateway.client.vpn.TunnelState

/**
 * «Проверка»: четыре пункта защиты с объяснением — что значит каждый и в каком он состоянии сейчас, ошибка туннеля и ядро.
 * Замеры (внешний IP, утечки DNS, путь) — инспектор Android, пункт 9.5.
 */
@Composable
fun CheckScreen(ui: MainUi, core: String?, loadCore: () -> Unit) {
    LaunchedEffect(Unit) { loadCore() }
    Column(
        Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .statusBarsPadding()
            .padding(horizontal = PgSpace.WindowSideMobile)
            .padding(bottom = PgLayout.ScrollBottomPadding)
            .testTag("check"),
        verticalArrangement = Arrangement.spacedBy(PgSpace.L),
    ) {
        Text(stringResource(R.string.nav_check), style = PgType.H1, modifier = Modifier.padding(top = PgSpace.L))
        if (ui.status.state == TunnelState.Error) {
            PgCard(Modifier.fillMaxWidth()) {
                Text(ui.status.error ?: "", color = PgColor.Status.Danger.Text, style = PgType.Mono)
                Text(stringResource(Texts.error(ui.status.error)), style = PgType.Body)
            }
        }

        PgCard(Modifier.fillMaxWidth()) {
            val chips = ProtectionChips.of(ui.status.state, ui.status.lockdown)
            for ((index, chip) in chips.withIndex()) {
                if (index > 0) {
                    Spacer(Modifier.size(PgSpace.L))
                }

                Row(verticalAlignment = Alignment.CenterVertically) {
                    Box(Modifier.size(22.dp).clip(CircleShape).background(chip.look.base), contentAlignment = Alignment.Center) {
                        PgIcon(chip.look.mark, null, color = PgColor.Surface.Solid, size = 14.dp, strokeWidth = 3f)
                    }

                    Spacer(Modifier.width(PgSpace.M))
                    Text(stringResource(chip.title), style = PgType.BodyStrong, modifier = Modifier.weight(1f))
                    Text(stringResource(chip.value), color = chip.look.base, style = PgType.Small)
                }

                Text(stringResource(chip.text), color = PgColor.Text.Secondary, style = PgType.Small, modifier = Modifier.padding(top = PgSpace.Xxs))
            }
        }

        PgCard(Modifier.fillMaxWidth()) {
            Overline(stringResource(R.string.card_core))
            Spacer(Modifier.size(PgSpace.Xs))
            Text("Xray-core " + (core ?: "—"), style = PgType.Mono, modifier = Modifier.testTag("check.core"))
        }
    }
}

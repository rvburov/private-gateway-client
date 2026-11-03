package com.privategateway.client.ui.home

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.scale
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.privategateway.client.R
import com.privategateway.client.core.vpn.TunnelErrors
import com.privategateway.client.ui.theme.PgCard
import com.privategateway.client.ui.theme.PgColor
import com.privategateway.client.ui.theme.PgIcon
import com.privategateway.client.ui.theme.PgIcons
import com.privategateway.client.ui.theme.PgLayout
import com.privategateway.client.ui.theme.PgPrimaryButton
import com.privategateway.client.ui.theme.PgRadius
import com.privategateway.client.ui.theme.PgSecondaryButton
import com.privategateway.client.ui.theme.PgSpace
import com.privategateway.client.ui.theme.PgType

/** Что делают кнопки страницы «Работает другой VPN». */
class OtherVpnActions(
    /** «Я закрыл его — проверить снова»: VPN свободен — подключить, занят — подсказка, страница остается. */
    val retry: () -> Unit = {},
    /** «Открыть настройки VPN»: там видно, какое приложение держит VPN, и его можно выключить. */
    val vpnSettings: () -> Unit = {},
    /** «Подробнее в журнале · код PGC-NET-010» — вкладка «Проверка» с журналом туннеля. */
    val details: () -> Unit = {},
)

/**
 * «Работает другой VPN» на Android (доска 16, DECISIONS.md, п. 4) — вместо главной, пока защита стоит на PGC-NET-010: VPN
 * телефона занят другим приложением — не дал подключиться или вытеснил наш во время работы. Знак-треугольник в оранжевой
 * плитке 72, заголовок display, пояснение для Android, факты с оранжевыми кружками, «Я закрыл его — проверить снова»,
 * «Открыть настройки VPN» и ссылка на журнал. Островок под страницей работает: можно уйти в «Серверы» или «Настройки».
 */
@Composable
fun OtherVpnScreen(actions: OtherVpnActions) {
    Column(
        Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .statusBarsPadding()
            .padding(horizontal = PgSpace.WindowSideMobile)
            .padding(bottom = PgLayout.ScrollBottomPadding)
            .testTag("other_vpn"),
    ) {
        Row(Modifier.fillMaxWidth().height(60.dp), verticalAlignment = Alignment.CenterVertically) {
            Box {
                PgIcon(PgIcons.Shield, null, color = PgColor.Accent.Base, size = 28.dp)
                PgIcon(PgIcons.MarkOk, null, color = PgColor.Accent.Base, size = 28.dp)
            }

            Spacer(Modifier.width(PgSpace.M))
            Text(stringResource(R.string.app_title), style = PgType.Brand)
        }

        Column(Modifier.padding(top = 20.dp, start = 4.dp, end = 4.dp), verticalArrangement = Arrangement.spacedBy(PgSpace.M)) {
            Box(
                Modifier
                    .align(Alignment.CenterHorizontally)
                    .size(72.dp)
                    .clip(RoundedCornerShape(20.dp))
                    .background(PgColor.Status.Caution.Tint),
                contentAlignment = Alignment.Center,
            ) {
                PgIcon(PgIcons.Warning, null, color = PgColor.Status.Caution.Text, size = 34.dp, strokeWidth = 1.9f)
            }

            Text(
                stringResource(R.string.problem_conflict_title_mobile),
                Modifier.fillMaxWidth().padding(top = PgSpace.Xs).semantics { heading() }.testTag("other_vpn.title"),
                style = PgType.Display,
                textAlign = TextAlign.Center,
            )
            Text(
                stringResource(R.string.problem_conflict_text_android),
                Modifier.align(Alignment.CenterHorizontally).widthIn(max = 360.dp).testTag("other_vpn.text"),
                color = PgColor.Text.Secondary,
                style = PgType.Body.copy(lineHeight = 22.sp),
                textAlign = TextAlign.Center,
            )

            PgCard(Modifier.fillMaxWidth().padding(top = PgSpace.Xs), contentPadding = PaddingValues(horizontal = PgSpace.L, vertical = 4.dp)) {
                Fact(stringResource(R.string.problem_fact_vpn_mobile), stringResource(R.string.problem_fact_vpn_mobile_other))
                Fact(stringResource(R.string.problem_fact_protection_mobile), stringResource(R.string.problem_fact_protection_mobile_off))
            }

            Column(Modifier.padding(top = PgSpace.L), verticalArrangement = Arrangement.spacedBy(PgSpace.M)) {
                PgPrimaryButton(stringResource(R.string.problem_conflict_retry), "other_vpn.retry", actions.retry, Modifier.fillMaxWidth(), height = 50.dp)
                PgSecondaryButton(stringResource(R.string.android_settings_vpn_open), "other_vpn.settings", actions.vpnSettings, Modifier.fillMaxWidth(), height = 46.dp)
            }

            Details(stringResource(R.string.problem_details, TunnelErrors.OTHER_VPN), actions.details)
        }
    }
}

/** Факт (.pr-fact): оранжевый кружок 8, что проверено, итог моноширинным справа. */
@Composable
private fun Fact(label: String, value: String) {
    Row(Modifier.fillMaxWidth().padding(vertical = 11.dp), verticalAlignment = Alignment.CenterVertically) {
        Box(Modifier.size(8.dp).clip(CircleShape).background(PgColor.Status.Caution.Base))
        Text(label, Modifier.weight(1f).padding(start = PgSpace.M), style = PgType.Body)
        Text(value, Modifier.padding(start = PgSpace.M), color = PgColor.Text.Secondary, style = PgType.Mono.copy(fontSize = PgType.Caption.fontSize), textAlign = TextAlign.End)
    }
}

/** «Подробнее в журнале · код …» (.btn-link): по центру, акцентным цветом, при нажатии сжимается до 0,98. */
@Composable
private fun Details(text: String, onClick: () -> Unit) {
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    Box(Modifier.fillMaxWidth(), contentAlignment = Alignment.Center) {
        Text(
            text,
            Modifier
                .scale(if (pressed) 0.98f else 1f)
                .clip(RoundedCornerShape(PgRadius.Input))
                .clickable(interactionSource = interaction, indication = null, role = Role.Button, onClick = onClick)
                .padding(PgSpace.Xs)
                .testTag("other_vpn.details"),
            color = PgColor.Accent.Text,
            style = PgType.Small.copy(fontWeight = FontWeight.SemiBold),
            textAlign = TextAlign.Center,
        )
    }
}

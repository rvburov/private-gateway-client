package com.privategateway.client.ui.settings

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
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
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import com.privategateway.client.R
import com.privategateway.client.core.vpn.SystemVpnSource
import com.privategateway.client.core.vpn.SystemVpnView
import com.privategateway.client.ui.theme.Overline
import com.privategateway.client.ui.theme.PgColor
import com.privategateway.client.ui.theme.PgIcon
import com.privategateway.client.ui.theme.PgIcons
import com.privategateway.client.ui.theme.PgLayout
import com.privategateway.client.ui.theme.PgRadius
import com.privategateway.client.ui.theme.PgSetting
import com.privategateway.client.ui.theme.PgSheet
import com.privategateway.client.ui.theme.PgSpace
import com.privategateway.client.ui.theme.PgType

/*
  Блок при обрыве на Android — системный: «Постоянная VPN» и «Блокировать подключения без VPN» в настройках VPN телефона
  включает только пользователь (доска 26, решение 445). Поэтому вместо переключателей — то, что сообщил Android, и переход в
  его настройки: в «Основных» и в группе «Блок при обрыве» страницы «Защита и утечки».
*/

/** Подпись группы над карточкой (.t-overline досок 4 и 5): на 6 правее края карточки, 8 до нее; [top] — отступ от прежней. */
@Composable
internal fun GroupTitle(text: String, top: Dp) {
    Overline(text, Modifier.padding(start = 6.dp, top = top, end = 6.dp, bottom = PgSpace.S))
}

/**
 * Строка системной настройки Android (.setting): заголовок, подпись и справа то, что сообщил Android, — «вкл», «выкл» или
 * «неизвестно» — со стрелкой. Переключателя нет: нажатие на строку открывает настройки VPN телефона. [extra] — строки под
 * подписью (откуда значение).
 */
@Composable
internal fun SystemVpnRow(
    title: String,
    caption: String?,
    value: Boolean?,
    valueTag: String,
    tag: String,
    onClick: () -> Unit,
    padding: PaddingValues,
    divider: Boolean = false,
    extra: (@Composable ColumnScope.() -> Unit)? = null,
) {
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    PgSetting(
        title,
        caption,
        Modifier
            .clickable(interactionSource = interaction, indication = null, role = Role.Button, onClick = onClick)
            .testTag(tag),
        divider = divider,
        padding = padding,
        extra = extra,
    ) {
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(PgSpace.Xxs)) {
            SystemVpnValue(value, valueTag)
            PgIcon(PgIcons.ChevronRight, null, color = if (pressed) PgColor.Accent.Base else PgColor.Text.Muted, size = 16.dp)
        }
    }
}

/** Значение флага Android: «вкл» — зеленым, «выкл» — серым, «неизвестно» — бледным (решение 445). */
@Composable
internal fun SystemVpnValue(value: Boolean?, tag: String) {
    val (text, color) = when (value) {
        true -> R.string.android_settings_vpn_on to PgColor.Status.Success.Text
        false -> R.string.android_settings_vpn_off to PgColor.Text.Secondary
        null -> R.string.android_settings_vpn_unknown to PgColor.Text.Muted
    }
    Text(stringResource(text), Modifier.testTag(tag), color = color, style = PgType.BodyStrong)
}

/** Откуда значения: так Android сообщает сейчас, так сообщил при последнем подключении или еще не сообщал. */
@Composable
internal fun SystemVpnSourceText(source: SystemVpnSource, tag: String) {
    val text = when (source) {
        SystemVpnSource.Now -> R.string.android_settings_vpn_now
        SystemVpnSource.LastConnection -> R.string.android_settings_vpn_last
        SystemVpnSource.Unknown -> R.string.android_settings_vpn_never
    }
    Text(stringResource(text), Modifier.testTag(tag), color = PgColor.Text.Muted, style = PgType.Caption)
}

/**
 * «Открыть настройки VPN» (.btn-small.soft): заливка accent.tint, рамка accent.line, синий полужирный текст, во всю ширину; при
 * нажатии сжимается до 0,98 без ряби (решение 485).
 */
@Composable
internal fun OpenVpnSettingsButton(onClick: () -> Unit, tag: String) {
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    val shape = RoundedCornerShape(PgRadius.Input)
    Box(
        Modifier
            .fillMaxWidth()
            .scale(if (pressed) 0.98f else 1f)
            .heightIn(min = PgLayout.TouchTarget)
            .clip(shape)
            .background(PgColor.Accent.Tint)
            .border(1.dp, PgColor.Accent.Line, shape)
            .clickable(interactionSource = interaction, indication = null, role = Role.Button, onClick = onClick)
            .testTag(tag),
        contentAlignment = Alignment.Center,
    ) {
        Text(stringResource(R.string.android_settings_vpn_open), color = PgColor.Accent.Text, style = PgType.BodyStrong)
    }
}

/**
 * «Как включить блок при обрыве» (доска 4, Android; решение 738): Android не дает приложению включить блок и не открывает
 * страницу именно этого VPN — только общий список. Поэтому лист говорит, что нажать там, показывает, что Android сообщил
 * сейчас, и ведет в его настройки. Теги: лист — «ks.help», шаги — «ks.help.step1…3», кнопка — «ks.help.open».
 */
@Composable
internal fun KillSwitchHelpSheet(system: SystemVpnView, openVpnSettings: () -> Unit, onClose: () -> Unit) {
    PgSheet(onClose, "ks.help") {
        SheetHeader(stringResource(R.string.android_ks_help_title), onClose, "ks.help.close")
        Spacer(Modifier.height(PgSpace.M))
        Text(stringResource(R.string.android_ks_help_text), color = PgColor.Text.Secondary, style = PgType.Small.copy(lineHeight = PgType.Body.lineHeight))
        Spacer(Modifier.height(PgSpace.Card))
        Column(verticalArrangement = Arrangement.spacedBy(PgSpace.L)) {
            listOf(R.string.android_ks_help_step1, R.string.android_ks_help_step2, R.string.android_ks_help_step3).forEachIndexed { index, step ->
                HelpStep(index + 1, stringResource(step), "ks.help.step${index + 1}")
            }
        }
        Spacer(Modifier.height(PgSpace.M))
        Text(stringResource(R.string.android_ks_help_note), color = PgColor.Text.Muted, style = PgType.Caption)
        Spacer(Modifier.height(PgSpace.Card))
        Overline(stringResource(R.string.android_ks_help_now))
        Spacer(Modifier.height(PgSpace.S))
        HelpFlag(stringResource(R.string.android_settings_vpn_alwaysOn), system.flags.alwaysOn, "ks.help.alwaysOn")
        HelpFlag(stringResource(R.string.android_settings_vpn_lockdown), system.flags.lockdown, "ks.help.lockdown")
        Spacer(Modifier.height(PgSpace.Xs))
        SystemVpnSourceText(system.source, "ks.help.source")
        Spacer(Modifier.height(PgSpace.Card))
        OpenVpnSettingsButton(openVpnSettings, "ks.help.open")
    }
}

/** Шаг: номер в кружке accent.tint и текст. */
@Composable
private fun HelpStep(number: Int, text: String, tag: String) {
    Row(Modifier.fillMaxWidth().testTag(tag), verticalAlignment = Alignment.Top, horizontalArrangement = Arrangement.spacedBy(PgSpace.M)) {
        Box(Modifier.size(24.dp).clip(RoundedCornerShape(12.dp)).background(PgColor.Accent.Tint), contentAlignment = Alignment.Center) {
            Text(number.toString(), color = PgColor.Accent.Text, style = PgType.Caption.copy(fontWeight = PgType.BodyStrong.fontWeight))
        }
        Text(text, Modifier.weight(1f).padding(top = 2.dp), color = PgColor.Text.Primary, style = PgType.Body)
    }
}

/** Флаг Android строкой: название и «вкл», «выкл» или «неизвестно». */
@Composable
private fun HelpFlag(title: String, value: Boolean?, tag: String) {
    Row(Modifier.fillMaxWidth().padding(vertical = PgSpace.Xs), verticalAlignment = Alignment.CenterVertically) {
        Text(title, Modifier.weight(1f).padding(end = PgSpace.M), color = PgColor.Text.Primary, style = PgType.Small)
        SystemVpnValue(value, tag)
    }
}

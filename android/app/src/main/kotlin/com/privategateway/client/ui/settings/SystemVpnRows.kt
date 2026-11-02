package com.privategateway.client.ui.settings

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
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
import com.privategateway.client.ui.theme.Overline
import com.privategateway.client.ui.theme.PgColor
import com.privategateway.client.ui.theme.PgIcon
import com.privategateway.client.ui.theme.PgIcons
import com.privategateway.client.ui.theme.PgLayout
import com.privategateway.client.ui.theme.PgRadius
import com.privategateway.client.ui.theme.PgSetting
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

package com.privategateway.client.ui.settings

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
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
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.privategateway.client.R
import com.privategateway.client.ui.theme.PgColor
import com.privategateway.client.ui.theme.PgIcon
import com.privategateway.client.ui.theme.PgIcons
import com.privategateway.client.ui.theme.PgRadius
import com.privategateway.client.ui.theme.PgSpace
import com.privategateway.client.ui.theme.PgType

/*
  Общие части страниц «Подключение и ядро» и «Маршрутизация»: шапка нижнего листа, подпись поля, значение только для
  чтения в рамке и плитка программы — как на досках 6 и 13.
*/

/** Поле с адресом, шаблоном или числом — моноширинным (font-family mono у .input досок 6 и 13). */
internal val MonoField = PgType.Mono.copy(fontWeight = FontWeight.Normal)

/** Шапка листа (.sheet): заголовок t-status и крестик справа; тег крестика — [closeTag]. */
@Composable
internal fun SheetHeader(title: String, onClose: () -> Unit, closeTag: String) {
    Row(Modifier.fillMaxWidth().padding(top = 2.dp), verticalAlignment = Alignment.CenterVertically) {
        Text(title, Modifier.weight(1f), color = PgColor.Text.Primary, style = PgType.Status, maxLines = 1, overflow = TextOverflow.Ellipsis)
        SheetClose(onClose, closeTag)
    }
}

/** Крестик листа (.btn-icon 40): серый значок, при нажатии — синий и сжатие 0,98 без заливки (решение 485). */
@Composable
private fun SheetClose(onClose: () -> Unit, tag: String) {
    val label = stringResource(R.string.card_close)
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    Box(
        Modifier
            .size(40.dp)
            .scale(if (pressed) 0.98f else 1f)
            .clip(RoundedCornerShape(PgRadius.Button))
            .clickable(interactionSource = interaction, indication = null, onClickLabel = label, role = Role.Button, onClick = onClose)
            .semantics { contentDescription = label }
            .testTag(tag),
        contentAlignment = Alignment.Center,
    ) {
        PgIcon(PgIcons.Close, null, color = if (pressed) PgColor.Accent.Base else PgColor.Text.Secondary, size = 20.dp, strokeWidth = 2.2f)
    }
}

/** Подпись поля листа (t-small полужирным). */
@Composable
internal fun FieldLabel(text: String) {
    Text(text, color = PgColor.Text.Primary, style = PgType.Small.copy(fontWeight = FontWeight.SemiBold))
}

/**
 * Значение только для чтения (Border.value «Подключения и ядра»): моноширинным caption 600 в рамке line.border на
 * control.segmentTrack, поля 8 и 4, скругление 8.
 */
@Composable
internal fun ValueBox(text: String, tag: String) {
    val shape = RoundedCornerShape(8.dp)
    Text(
        text,
        Modifier
            .clip(shape)
            .background(PgColor.Control.SegmentTrack)
            .border(1.dp, PgColor.Line.Border, shape)
            .padding(horizontal = PgSpace.S, vertical = PgSpace.Xxs)
            .testTag(tag),
        color = PgColor.Text.Primary,
        style = PgType.Mono.copy(fontSize = PgType.Caption.fontSize, fontWeight = FontWeight.SemiBold),
        maxLines = 1,
    )
}

/** Плитка программы (.tile доски 13): первая буква названия на цветном квадрате 36, три цвета по кругу ([index]). */
@Composable
internal fun AppTile(name: String, index: Int) {
    val colors = listOf(PgColor.Tile.Violet, PgColor.Tile.Teal, PgColor.Tile.Amber)
    Box(
        Modifier
            .size(36.dp)
            .clip(RoundedCornerShape(PgRadius.Input))
            .background(colors[index.mod(colors.size)]),
        contentAlignment = Alignment.Center,
    ) {
        Text(initial(name), color = PgColor.Surface.Solid, style = PgType.BodyStrong.copy(fontWeight = FontWeight.Bold))
    }
}

/** Первая буква названия заглавной — целым знаком, а не половиной суррогатной пары; пустое название — «?». */
internal fun initial(name: String): String {
    val text = name.trim()
    if (text.isEmpty()) {
        return "?"
    }

    return String(Character.toChars(text.codePointAt(0))).uppercase()
}

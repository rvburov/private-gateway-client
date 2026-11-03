package com.privategateway.client.ui.theme

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.IntrinsicSize
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.scale
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.IntRect
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.LayoutDirection
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.Popup
import androidx.compose.ui.window.PopupPositionProvider
import androidx.compose.ui.window.PopupProperties

/*
  Выпадающий список макета (доска 24): кнопка .btn-select и меню .popup у нее с пунктами .btn-option. Меню — на 8 под
  кнопкой, по ее правому краю; вниз не помещается — над кнопкой (app.js). Закрывается нажатием вне него и «Назад».
*/

/**
 * Выбор из списка: кнопка с текущим значением и меню вариантов. Выбранный пункт — синим полужирным без заливки; выбор
 * закрывает меню. Теги: кнопка — [tag], меню — «[tag].menu», пункт — «[tag].значение». С [confirm] новое значение — только
 * после ответа, как у сегментов.
 */
@Composable
fun <T> PgSelect(
    options: List<T>,
    selected: T,
    label: @Composable (T) -> String,
    tag: String,
    onSelect: (T) -> Unit,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
    confirm: PgConfirm<T>? = null,
) {
    var open by remember { mutableStateOf(false) }
    Box(modifier) {
        PgSelectButton(label(selected), tag, { open = !open }, open = open, enabled = enabled)
        PgMenu(open, { open = false }, "$tag.menu") {
            for (option in options) {
                PgMenuOption(label(option), option == selected, "$tag.$option", {
                    open = false
                    pgChoose(option, selected, confirm, onSelect)
                })
            }
        }
    }
}

/**
 * Кнопка-список (.btn-select, Button.select): белая, высота 36, рамка line.strong, справа синяя галочка вниз. Пока меню
 * открыто ([open]) или кнопку нажимают — рамка и текст accent.base.
 */
@Composable
fun PgSelectButton(
    text: String,
    tag: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    open: Boolean = false,
    enabled: Boolean = true,
) {
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    val active = enabled && (open || pressed)
    val shape = RoundedCornerShape(PgRadius.Input)
    Row(
        modifier
            .alpha(if (enabled) 1f else 0.6f)
            .heightIn(min = 36.dp)
            .clip(shape)
            .background(PgColor.Surface.Solid)
            .border(1.dp, if (active) PgColor.Accent.Base else PgColor.Line.Strong, shape)
            .clickable(interactionSource = interaction, indication = null, enabled = enabled, role = Role.Button, onClick = onClick)
            .padding(start = PgSpace.L, end = PgSpace.M)
            .testTag(tag),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(PgSpace.Xs),
    ) {
        val color = when {
            !enabled -> PgColor.Text.Muted
            active -> PgColor.Accent.Base
            else -> PgColor.Text.Primary
        }
        Text(
            text,
            Modifier.weight(1f, fill = false),
            color = color,
            style = PgType.Small.copy(fontWeight = FontWeight.Medium),
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
        PgIcon(PgIcons.ChevronDown, null, color = PgColor.Accent.Text, size = 14.dp, strokeWidth = 2.6f)
    }
}

/**
 * Меню у кнопки (.popup): белое, скругление radius.card, рамка line.border, тень shadow.popup, ширина по пунктам (не меньше
 * 220). Ставится рядом с кнопкой в одном Box — от него меню и меряет место. Нажатие вне меню и «Назад» зовут [onDismiss].
 */
@Composable
fun PgMenu(expanded: Boolean, onDismiss: () -> Unit, tag: String, content: @Composable ColumnScope.() -> Unit) {
    if (!expanded) {
        return
    }

    val density = LocalDensity.current
    val position = remember(density) {
        with(density) { MenuPosition(PgSpace.S.roundToPx(), MenuShadowRoom.roundToPx(), PgSpace.S.roundToPx()) }
    }
    Popup(popupPositionProvider = position, onDismissRequest = onDismiss, properties = PopupProperties(focusable = true)) {
        val shape = RoundedCornerShape(PgRadius.Card)
        Column(
            Modifier
                .padding(MenuShadowRoom)
                .widthIn(min = 220.dp)
                .width(IntrinsicSize.Max)
                .pgShadow(PgShadow.Popup, shape)
                .clip(shape)
                .background(PgColor.Surface.Solid)
                .border(1.dp, PgColor.Line.Border, shape)
                .padding(6.dp)
                .testTag(tag),
            content = content,
        )
    }
}

/** Пункт выбора в меню (.btn-option): текущий ([current]) — заливкой оттенком акцента, синим полужирным (DECISIONS.md, п. 13); при нажатии — синий текст. */
@Composable
fun PgMenuOption(text: String, current: Boolean, tag: String, onClick: () -> Unit, enabled: Boolean = true) {
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    val color = when {
        !enabled -> PgColor.Text.Muted
        current -> PgColor.Accent.Text
        pressed -> PgColor.Accent.Base
        else -> PgColor.Text.Primary
    }
    MenuRow(
        text,
        color,
        current,
        pressed && enabled,
        enabled,
        tag,
        Modifier.selectable(selected = current, interactionSource = interaction, indication = null, enabled = enabled, onClick = onClick),
    )
}

/** Пункт-действие в меню («Обновить», «Удалить …»): строка-кнопка; [danger] — красным. */
@Composable
fun PgMenuAction(text: String, tag: String, onClick: () -> Unit, danger: Boolean = false, enabled: Boolean = true) {
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    val color = when {
        !enabled -> PgColor.Text.Muted
        danger -> PgColor.Status.Danger.Text
        pressed -> PgColor.Accent.Base
        else -> PgColor.Text.Primary
    }
    MenuRow(
        text,
        color,
        false,
        pressed && enabled,
        enabled,
        tag,
        Modifier.clickable(interactionSource = interaction, indication = null, enabled = enabled, role = Role.Button, onClick = onClick),
    )
}

@Composable
private fun MenuRow(text: String, color: Color, strong: Boolean, pressed: Boolean, enabled: Boolean, tag: String, action: Modifier) {
    Row(
        Modifier
            .fillMaxWidth()
            .scale(if (pressed) 0.98f else 1f)
            .alpha(if (enabled) 1f else 0.6f)
            .heightIn(min = PgLayout.TouchTarget)
            .clip(RoundedCornerShape(PgRadius.Input))
            .then(if (strong) Modifier.background(PgColor.Accent.Tint) else Modifier)
            .then(action)
            .padding(horizontal = PgSpace.M)
            .testTag(tag),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(text, color = color, style = if (strong) PgType.Body.copy(fontWeight = FontWeight.SemiBold) else PgType.Body)
    }
}

/** Поле вокруг меню внутри его окна: окно всплывающего меню обрезает все, что за его краем, — и тень тоже. */
private val MenuShadowRoom = 16.dp

/**
 * Место меню: на [gap] под кнопкой, правым краем по правому краю кнопки; вниз не помещается — на [gap] над ней. Не ближе
 * [edge] к краям окна. [room] — прозрачное поле вокруг самого меню (под тень), оно может уходить за край.
 */
private class MenuPosition(private val gap: Int, private val room: Int, private val edge: Int) : PopupPositionProvider {
    override fun calculatePosition(anchorBounds: IntRect, windowSize: IntSize, layoutDirection: LayoutDirection, popupContentSize: IntSize): IntOffset {
        val x = (anchorBounds.right + room - popupContentSize.width)
            .coerceAtMost(windowSize.width - popupContentSize.width + room - edge)
            .coerceAtLeast(edge - room)
        val below = anchorBounds.bottom + gap - room
        val fits = below + popupContentSize.height - room <= windowSize.height - edge
        val y = if (fits) below else (anchorBounds.top - gap + room - popupContentSize.height).coerceAtLeast(edge - room)
        return IntOffset(x, y)
    }
}

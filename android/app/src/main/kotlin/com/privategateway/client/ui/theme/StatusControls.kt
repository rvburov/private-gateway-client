package com.privategateway.client.ui.theme

import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
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
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.scale
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.selected
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp

/** Три плотности метки доски 24: версия (.badge), выбранный сервер (.pill), итог проверки. */
enum class PgBadgeKind { Badge, Pill, Result }

@Composable
fun PgBadge(
    text: String,
    modifier: Modifier = Modifier,
    ink: Color = PgColor.Accent.Text,
    tint: Color = PgColor.Accent.Tint,
    kind: PgBadgeKind = PgBadgeKind.Badge,
) {
    val horizontal = when (kind) { PgBadgeKind.Badge -> PgSpace.Xs; PgBadgeKind.Pill -> 7.dp; PgBadgeKind.Result -> 9.dp }
    Text(
        text,
        modifier.clip(RoundedCornerShape(if (kind == PgBadgeKind.Result) PgRadius.Input else PgRadius.Chip))
            .background(tint).padding(horizontal = horizontal, vertical = if (kind == PgBadgeKind.Badge) 2.dp else 3.dp),
        color = ink,
        style = PgType.Caption.copy(fontSize = PgType.Overline.fontSize, fontWeight = if (kind == PgBadgeKind.Pill) FontWeight.Bold else FontWeight.SemiBold),
        maxLines = 1,
    )
}

/** Полоса предупреждения (.banner): компактная — импорт, обычная — текст с необязательным значком. */
@Composable
fun PgBanner(
    tag: String,
    modifier: Modifier = Modifier,
    icon: String? = null,
    compact: Boolean = false,
    ink: Color = PgColor.Status.Warning.Badge,
    tint: Color = PgColor.Status.Warning.Tint,
    edge: Color = PgColor.Status.Warning.Line,
    content: @Composable ColumnScope.() -> Unit,
) {
    val shape = RoundedCornerShape(if (compact) 12.dp else 16.dp)
    Row(
        modifier.fillMaxWidth().clip(shape).background(tint).border(1.dp, edge, shape)
            .padding(horizontal = if (compact) PgSpace.L else 14.dp, vertical = if (compact) PgSpace.M else PgSpace.L)
            .semantics(mergeDescendants = true) {}.testTag(tag),
        horizontalArrangement = Arrangement.spacedBy(PgSpace.M),
    ) {
        if (icon != null) PgIcon(icon, null, Modifier.padding(top = 1.dp), color = ink, size = 18.dp, strokeWidth = 2f)
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp), content = content)
    }
}

/** Кнопка-значок 44 (.btn-icon); steady оставляет кнопку неподвижной во время обновления. */
@Composable
fun PgIconButton(
    icon: String,
    label: String,
    tag: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
    size: Dp = 18.dp,
    stroke: Float = 2f,
    steady: Boolean = false,
    spinning: Boolean = false,
) {
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    Box(
        modifier.size(PgLayout.TouchTarget).alpha(if (enabled) 1f else 0.35f)
            .scale(if (pressed && enabled && !steady) 0.98f else 1f).clip(RoundedCornerShape(PgRadius.Button))
            .clickable(interactionSource = interaction, indication = null, enabled = enabled, onClickLabel = label, role = Role.Button, onClick = onClick)
            .semantics { contentDescription = label }.testTag(tag),
        contentAlignment = Alignment.Center,
    ) {
        PgSpinningIcon(icon, spinning, if (pressed && enabled) PgColor.Accent.Base else PgColor.Text.Primary, size, stroke, tag)
    }
}

/** Небольшая кнопка (.btn-small/soft): видимая высота 36, область касания 44. Занятая не принимает повтор. */
@Composable
fun PgSmallButton(
    label: String,
    tag: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    icon: String? = null,
    soft: Boolean = false,
    spinning: Boolean = false,
    enabled: Boolean = true,
) {
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    val ink = if (!enabled) PgColor.Text.Muted else if (pressed) PgColor.Accent.Base else if (soft) PgColor.Accent.Text else PgColor.Text.Primary
    val shape = RoundedCornerShape(PgRadius.Input)
    Box(
        modifier.heightIn(min = PgLayout.TouchTarget).alpha(if (enabled) 1f else 0.6f)
            .clickable(interactionSource = interaction, indication = null, enabled = enabled && !spinning, onClickLabel = label, role = Role.Button, onClick = onClick)
            .testTag(tag),
        contentAlignment = Alignment.Center,
    ) {
        Row(
            Modifier.heightIn(min = 36.dp).clip(shape).background(if (soft) PgColor.Accent.Tint else PgColor.Surface.Solid)
                .border(1.dp, if (soft || pressed) PgColor.Accent.Line else PgColor.Line.Strong, shape).padding(horizontal = PgSpace.L),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(PgSpace.Xs),
        ) {
            if (icon != null) PgSpinningIcon(icon, spinning, ink, 15.dp, 2.2f, tag)
            Text(label, color = ink, style = if (soft) PgType.Small.copy(fontWeight = FontWeight.SemiBold) else PgType.Small, maxLines = 1)
        }
    }
}

/** Плитка действия (.btn-tile): выбранная, опасная и недоступная сохраняют смысл и для чтения с экрана. */
@Composable
fun PgTileButton(
    icon: String,
    label: String,
    tag: String,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
    danger: Boolean = false,
    on: Boolean = false,
    onClick: () -> Unit,
) {
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    val active = pressed && enabled
    val shape = RoundedCornerShape(14.dp)
    val edge = when { on -> PgColor.Accent.Base; danger -> if (active) PgColor.Status.Danger.Base else PgColor.Status.Danger.Line; active -> PgColor.Accent.Base; else -> PgColor.Line.Strong }
    val ink = when { on -> PgColor.Accent.Text; danger -> PgColor.Status.Danger.Text; active -> PgColor.Accent.Base; else -> PgColor.Text.Primary }
    Column(
        modifier.scale(if (active) 0.98f else 1f).alpha(if (enabled) 1f else 0.45f).heightIn(min = 58.dp)
            .clip(shape).background(if (on) PgColor.Accent.Tint else PgColor.Surface.Solid).border(1.dp, edge, shape)
            .clickable(interactionSource = interaction, indication = null, enabled = enabled, role = Role.Button, onClick = onClick)
            .semantics { selected = on }.padding(horizontal = PgSpace.Xxs, vertical = PgSpace.S).testTag(tag),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(PgSpace.Xxs, Alignment.CenterVertically),
    ) {
        PgIcon(icon, null, color = ink, size = 18.dp, strokeWidth = 1.9f)
        Text(label, color = ink, style = PgType.Caption, maxLines = 1, overflow = TextOverflow.Ellipsis)
    }
}

@Composable
private fun PgSpinningIcon(icon: String, spinning: Boolean, color: Color, size: Dp, stroke: Float, tag: String) {
    if (spinning) {
        val spin = rememberInfiniteTransition(label = "refresh")
        val angle by spin.animateFloat(0f, 360f, infiniteRepeatable(tween(900, easing = LinearEasing)), label = "angle")
        PgIcon(icon, null, Modifier.graphicsLayer { rotationZ = angle }.testTag("$tag.spin"), color = color, size = size, strokeWidth = stroke)
    } else {
        PgIcon(icon, null, color = color, size = size, strokeWidth = stroke)
    }
}

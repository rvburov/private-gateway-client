package com.privategateway.client.ui

import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.drawWithCache
import androidx.compose.ui.draw.scale
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Outline
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.Shape
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.vector.PathParser
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.selected
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.Density
import androidx.compose.ui.unit.LayoutDirection
import androidx.compose.ui.unit.dp
import com.privategateway.client.R
import com.privategateway.client.core.vpn.TunnelState
import com.privategateway.client.ui.theme.PgColor
import com.privategateway.client.ui.theme.PgIcon
import com.privategateway.client.ui.theme.PgIcons
import com.privategateway.client.ui.theme.PgLayout
import com.privategateway.client.ui.theme.PgShadow
import com.privategateway.client.ui.theme.PgShadowLayer
import com.privategateway.client.ui.theme.PgSpace
import com.privategateway.client.ui.theme.PgType
import com.privategateway.client.ui.theme.pgShadow
import java.util.Locale
import kotlin.math.sqrt

/** Колонка под щитом (layout.island.navColumnsMobile — «1fr 1fr 76px 1fr 1fr»; строку колонок генератор в тему не берет). */
private val PowerColumn = 76.dp

/** Поле под пунктом островка (IslandNav: Margin 0,0,0,9) и под подписью щита. */
private val NavBottom = 9.dp

/**
 * Островок внизу (IslandNav десктопа, app.js): белая плашка surface.solid с выступом вокруг кнопки-щита — один контур
 * [IslandShape] с обводкой line.divider и тенью shadow.island; четыре раздела и подпись состояния под щитом. Полоса пунктов —
 * от space.island до края экрана, как .pg-island-nav макета.
 */
@Composable
fun Island(tab: Tab, state: TunnelState, hasServer: Boolean, onTab: (Tab) -> Unit, onPower: () -> Unit, modifier: Modifier = Modifier) {
    val bump = PgLayout.Island.BumpRadius
    Box(
        modifier
            .fillMaxWidth()
            .navigationBarsPadding()
            .padding(horizontal = PgLayout.Island.SideMarginMobile)
            .padding(bottom = PgLayout.Island.BottomMargin)
            .height(PgLayout.Island.Height + bump),
    ) {
        // Плашка с выступом: заливка и обводка линией 1 по контуру, как Path со Stroke у десктопа.
        Box(
            Modifier
                .matchParentSize()
                .pgShadow(PgShadow.Island, IslandShape)
                .drawWithCache {
                    val path = islandOutline(size, this)
                    val line = Stroke(1.dp.toPx())
                    onDrawBehind {
                        drawPath(path, PgColor.Surface.Solid)
                        drawPath(path, PgColor.Line.Divider, style = line)
                    }
                },
        )
        Row(
            Modifier
                .align(Alignment.BottomCenter)
                .fillMaxWidth()
                .height(PgLayout.Island.Height)
                .padding(horizontal = PgSpace.Island - PgLayout.Island.SideMarginMobile),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            NavItem(Tab.Home, tab, PgIcons.NavHome, null, R.string.nav_home, onTab, Modifier.weight(1f))
            NavItem(Tab.Servers, tab, PgIcons.NavServers, PgIcons.NavServersDetail, R.string.nav_servers, onTab, Modifier.weight(1f))
            PowerLabel(state, hasServer, Modifier.width(PowerColumn).fillMaxHeight())
            NavItem(Tab.Check, tab, PgIcons.NavCheck, PgIcons.NavCheckDetail, R.string.nav_check, onTab, Modifier.weight(1f))
            NavItem(Tab.Settings, tab, PgIcons.NavSettings, PgIcons.NavSettingsDetail, R.string.nav_settings, onTab, Modifier.weight(1f))
        }

        // Центр щита — на верхней кромке плашки, как в макете (выступ — дуга вокруг него).
        PowerButton(state, onPower, Modifier.align(Alignment.TopCenter).offset(y = bump - PgLayout.PowerButton.Size / 2))
    }
}

/**
 * Пункт островка (Button.nav): значок 24 линией 1.7, зазор 4, подпись; кнопка по высоте содержимого с полем 9 снизу — по
 * центру полосы. Активный — accent.base, первый контур значка залит accent.navActiveFill; при нажатии — синий, без ряби.
 */
@Composable
private fun NavItem(item: Tab, current: Tab, icon: String, detail: String?, label: Int, onTab: (Tab) -> Unit, modifier: Modifier) {
    val active = item == current
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    val color = if (active || pressed) PgColor.Accent.Base else PgColor.Text.Muted
    Column(
        modifier
            .fillMaxHeight()
            .semantics { selected = active }
            .clickable(interactionSource = interaction, indication = null, role = Role.Tab) { onTab(item) }
            .testTag("nav.${item.name.lowercase()}"),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center,
    ) {
        Column(Modifier.padding(bottom = NavBottom), horizontalAlignment = Alignment.CenterHorizontally) {
            Box {
                PgIcon(icon, null, color = color, size = 24.dp, strokeWidth = 1.7f, fill = if (active) PgColor.Accent.NavActiveFill else null)
                if (detail != null) {
                    PgIcon(detail, null, color = color, size = 24.dp, strokeWidth = 1.7f)
                }
            }

            Spacer(Modifier.height(PgSpace.Xxs))
            Text(stringResource(label), color = color, style = if (active) PgType.NavMobileActive else PgType.NavMobile, maxLines = 1)
        }
    }
}

/** Подпись под кнопкой-щитом у низа полосы — состояние словом («Защищено», «Выключено»), не только цветом. */
@Composable
private fun PowerLabel(state: TunnelState, hasServer: Boolean, modifier: Modifier) {
    val look = StateLook.of(state)
    val label = if (!hasServer && state == TunnelState.Off) R.string.power_label_noServer else look.power
    Box(modifier.padding(bottom = NavBottom), contentAlignment = Alignment.BottomCenter) {
        Text(stringResource(label), color = look.icon, style = PgType.NavMobileActive, textAlign = TextAlign.Center, maxLines = 1)
    }
}

/**
 * Кнопка-щит 80 dp (Button.power): белый круг, кольцо цвета состояния, щит с отметкой; свечение 0 4 12 цветом glow. При
 * нажатии круг заливается state.fill и сжимается до 0,96 — без ряби. При подключении по кольцу бежит дуга accent.base, 0,9 с.
 */
@Composable
fun PowerButton(state: TunnelState, onClick: () -> Unit, modifier: Modifier = Modifier) {
    val look = StateLook.of(state)
    val on = state == TunnelState.Connected || state == TunnelState.Connecting || state == TunnelState.Reconnecting
    val description = stringResource(if (on) R.string.power_action_disconnect else R.string.power_action_connect)
    val ring = PgLayout.PowerButton.RingWidth
    val enabled = state != TunnelState.Stopping
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    val down = pressed && enabled
    Box(
        modifier
            .size(PgLayout.PowerButton.Size)
            .scale(if (down) 0.96f else 1f)
            .pgShadow(listOf(PgShadowLayer(0.dp, 4.dp, 12.dp, look.glow)), CircleShape)
            .clip(CircleShape)
            .background(PgColor.Surface.Solid)
            .then(if (down) Modifier.background(look.fill) else Modifier)
            .border(ring, look.ring, CircleShape)
            .clickable(interactionSource = interaction, indication = null, enabled = enabled, role = Role.Button, onClickLabel = description, onClick = onClick)
            .semantics { contentDescription = description }
            .testTag("power"),
        contentAlignment = Alignment.Center,
    ) {
        if (state == TunnelState.Connecting || state == TunnelState.Reconnecting) {
            val spin = rememberInfiniteTransition(label = "spinner")
            val angle by spin.animateFloat(0f, 360f, infiniteRepeatable(tween(900, easing = LinearEasing)), label = "angle")
            Canvas(Modifier.size(PgLayout.PowerButton.Size)) {
                val width = ring.toPx()
                drawArc(
                    PgColor.Accent.Base,
                    angle - 90f,
                    100f,
                    false,
                    topLeft = Offset(width / 2, width / 2),
                    size = Size(size.width - width, size.height - width),
                    style = Stroke(width, cap = StrokeCap.Round),
                )
            }
        }

        Box {
            PgIcon(PgIcons.Shield, null, color = look.icon, size = PgLayout.PowerButton.IconSize, strokeWidth = 1.9f)
            PgIcon(look.mark, null, color = look.icon, size = PgLayout.PowerButton.IconSize, strokeWidth = 1.9f)
        }
    }
}

/**
 * Контур островка — тот же, что IslandGeometry.Path десктопа и islandPath в app.js: плашка высотой layout.island.height со
 * скруглениями cornerRadius, над ней выступ — дуга bumpRadius вокруг щита, переходы к плашке — скругления bumpFilletRadius.
 * Верх фигуры — верх выступа: плашка начинается на bumpRadius ниже.
 */
private object IslandShape : Shape {
    override fun createOutline(size: Size, layoutDirection: LayoutDirection, density: Density): Outline =
        Outline.Generic(islandOutline(size, density))
}

private fun islandOutline(size: Size, density: Density): Path = with(density) {
    val data = islandPath(
        size.width,
        size.height,
        PgLayout.Island.BumpRadius.toPx(),
        PgLayout.Island.BumpFilletRadius.toPx(),
        PgLayout.Island.CornerRadius.toPx(),
    )
    PathParser().parsePathString(data).toPath()
}

/**
 * Данные пути SVG контура островка в пикселях: [width] × [height], выступ радиусом [bump] со скруглениями [fillet], углы
 * плашки — [corner]. Скругление касается и плашки, и дуги выступа: их центры на расстоянии bump + fillet (в макете 48 и 18
 * дают 63,5, 46,18 и 56,91 — числа IslandGeometry).
 */
internal fun islandPath(width: Float, height: Float, bump: Float, fillet: Float, corner: Float): String {
    val top = bump
    val center = width / 2
    val reach = sqrt((bump + fillet) * (bump + fillet) - fillet * fillet)
    val touchX = reach * bump / (bump + fillet)
    val touchY = top - fillet * bump / (bump + fillet)
    fun n(value: Float) = "%.2f".format(Locale.ROOT, value)
    return "M${n(corner)} ${n(top)} H${n(center - reach)} " +
        "A${n(fillet)} ${n(fillet)} 0 0 0 ${n(center - touchX)} ${n(touchY)} " +
        "A${n(bump)} ${n(bump)} 0 0 1 ${n(center + touchX)} ${n(touchY)} " +
        "A${n(fillet)} ${n(fillet)} 0 0 0 ${n(center + reach)} ${n(top)} " +
        "H${n(width - corner)} A${n(corner)} ${n(corner)} 0 0 1 ${n(width)} ${n(top + corner)} " +
        "V${n(height - corner)} A${n(corner)} ${n(corner)} 0 0 1 ${n(width - corner)} ${n(height)} " +
        "H${n(corner)} A${n(corner)} ${n(corner)} 0 0 1 0 ${n(height - corner)} " +
        "V${n(top + corner)} A${n(corner)} ${n(corner)} 0 0 1 ${n(corner)} ${n(top)} Z"
}

package com.privategateway.client.ui.theme

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxScope
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.LocalContentColor
import androidx.compose.material3.LocalTextStyle
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.drawBehind
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Shape
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.StrokeJoin
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.drawscope.scale
import androidx.compose.ui.graphics.vector.PathParser
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp

/**
 * Тема приложения — только из токенов макета (PgColor, PgType, PgSpace…, сгенерированы из design-tokens.json). Только
 * светлая тема, как решено в макете; Material — для готовых элементов ввода, его цвета тоже из токенов.
 */
@Composable
fun PgTheme(content: @Composable () -> Unit) {
    val colors = lightColorScheme(
        primary = PgColor.Accent.Base,
        onPrimary = PgColor.Surface.Solid,
        secondary = PgColor.Accent.Text,
        background = PgColor.Bg.Window,
        onBackground = PgColor.Text.Primary,
        surface = PgColor.Surface.Solid,
        onSurface = PgColor.Text.Primary,
        surfaceVariant = PgColor.Surface.Inset,
        onSurfaceVariant = PgColor.Text.Secondary,
        outline = PgColor.Line.Strong,
        outlineVariant = PgColor.Line.Divider,
        error = PgColor.Status.Danger.Base,
        scrim = PgColor.Overlay.Scrim,
    )
    MaterialTheme(colorScheme = colors) {
        CompositionLocalProvider(
            LocalTextStyle provides PgType.Body,
            LocalContentColor provides PgColor.Text.Primary,
            content = content,
        )
    }
}

/** Фон окна с цветными пятнами макета (color.decor) — под стеклянными карточками. */
@Composable
fun PgBackground(modifier: Modifier = Modifier, content: @Composable BoxScope.() -> Unit) {
    Box(
        modifier
            .fillMaxSize()
            .background(PgColor.Bg.Window)
            .drawBehind {
                val blobs = listOf(
                    Triple(PgColor.Decor.Blob1, Offset(size.width * 0.05f, size.height * 0.06f), size.width * 0.75f),
                    Triple(PgColor.Decor.Blob2, Offset(size.width * 1.0f, size.height * 0.45f), size.width * 0.7f),
                    Triple(PgColor.Decor.Blob3, Offset(size.width * 0.1f, size.height * 0.95f), size.width * 0.8f),
                )
                for ((color, center, radius) in blobs) {
                    drawCircle(Brush.radialGradient(listOf(color, Color.Transparent), center, radius), radius, center)
                }
            },
        content = content,
    )
}

/** Тень макета (shadow.*) средствами Android: самый размытый слой задает высоту, его цвет — цвет тени. */
fun Modifier.pgShadow(layers: List<PgShadowLayer>, shape: Shape): Modifier {
    val deepest = layers.maxBy { it.blur.value }
    val color = deepest.color.copy(alpha = 1f)
    return shadow(elevation = deepest.blur / 3, shape = shape, clip = false, ambientColor = color, spotColor = color)
}

/** Стеклянная карточка: заливка surface.card, рамка line.border, скругление radius.card, тень shadow.card. */
@Composable
fun PgCard(modifier: Modifier = Modifier, padding: Dp = PgSpace.Card, content: @Composable ColumnScope.() -> Unit) {
    val shape = RoundedCornerShape(PgRadius.Card)
    Column(
        modifier
            .pgShadow(PgShadow.Card, shape)
            .clip(shape)
            .background(PgColor.Surface.Card)
            .border(1.dp, PgColor.Line.Border, shape)
            .padding(padding),
        content = content,
    )
}

/** Иконка макета: контур 24×24 (PgIcons), рисуется линией; [filled] — еще и заливкой (щит в кнопке). */
@Composable
fun PgIcon(
    path: String,
    contentDescription: String?,
    modifier: Modifier = Modifier,
    color: Color = LocalContentColor.current,
    size: Dp = 20.dp,
    strokeWidth: Float = 1.8f,
    filled: Boolean = false,
) {
    val shape = remember(path) { PathParser().parsePathString(path).toPath() }
    val described = if (contentDescription != null) Modifier.semantics { this.contentDescription = contentDescription } else Modifier
    Canvas(modifier.size(size).then(described)) {
        val factor = this.size.minDimension / 24f
        scale(factor, factor, pivot = Offset.Zero) {
            if (filled) {
                drawPath(shape, color)
            }

            drawPath(shape, color, style = Stroke(width = strokeWidth, cap = StrokeCap.Round, join = StrokeJoin.Round))
        }
    }
}

/** Надзаголовок карточки: type.overline, заглавными, если так в токенах. */
@Composable
fun Overline(text: String, modifier: Modifier = Modifier, color: Color = PgColor.Text.Muted) {
    Text(if (PgType.OVERLINE_UPPERCASE) text.uppercase() else text, modifier, color = color, style = PgType.Overline)
}

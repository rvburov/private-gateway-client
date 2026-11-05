package com.privategateway.client.ui.theme

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxScope
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.PaddingValues
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
import androidx.compose.ui.graphics.compositeOver
import androidx.compose.ui.graphics.drawscope.DrawScope
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.drawscope.scale
import androidx.compose.ui.graphics.vector.PathParser
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import kotlin.math.sqrt

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

/**
 * Фон окна, как .pg-app макета и MainWindow десктопа: цвет bg.window, три пятна color.decor на своих местах и над ними слой
 * стекла glass.fill (.pg-glass) — под карточками. Места и размеры пятен — в dp от верхнего левого угла экрана, как в рамке
 * телефона макета (412 × 892, строка состояния внутри).
 */
@Composable
fun PgBackground(modifier: Modifier = Modifier, content: @Composable BoxScope.() -> Unit) {
    Box(
        modifier
            .fillMaxSize()
            .background(PgColor.Bg.Window)
            .drawBehind {
                blob(PgColor.Decor.Blob1, -110f, -90f, 360f, 360f)
                blob(PgColor.Decor.Blob2, 220f, 220f, 340f, 340f)
                blob(PgColor.Decor.Blob3, 10f, 590f, 380f, 320f)
                drawRect(PgGlass.Fill)
            },
        content = content,
    )
}

/**
 * Пятно — radial-gradient(circle | ellipse, цвет 35 %, прозрачно 70 %) в рамке [width] × [height] dp: у CSS луч градиента
 * идет до дальнего угла рамки (полудиагональ), эллипс — тот же круг, сжатый по высоте.
 */
private fun DrawScope.blob(color: Color, left: Float, top: Float, width: Float, height: Float) {
    val center = Offset((left + width / 2) * density, (top + height / 2) * density)
    val radius = width / 2 * sqrt(2f) * density
    val brush = Brush.radialGradient(0.35f to color, 0.7f to Color.Transparent, center = center, radius = radius)
    scale(1f, height / width, pivot = center) {
        drawCircle(brush, radius, center)
    }
}

/** Тень макета (shadow.*) средствами Android: самый размытый слой задает высоту, его цвет — цвет тени. */
fun Modifier.pgShadow(layers: List<PgShadowLayer>, shape: Shape): Modifier {
    val deepest = layers.maxBy { it.blur.value }
    val color = deepest.color.copy(alpha = 1f)
    return shadow(elevation = deepest.blur / 3, shape = shape, clip = false, ambientColor = color, spotColor = color)
}

/** Карточка: оттенок surface.card на bg.window, рамка line.border, скругление radius.card, тень shadow.card. */
@Composable
fun PgCard(modifier: Modifier = Modifier, padding: Dp = PgSpace.Card, content: @Composable ColumnScope.() -> Unit) {
    PgCard(modifier, PaddingValues(padding), content)
}

/**
 * Карточка со своими полями: строки настроек и строки-ссылки сами до краев карточки (.card с padding 4px 0).
 * Фон заранее смешан с bg.window и непрозрачен: системная тень не просвечивает внутрь карточки. Оттенок получается из
 * исходных токенов, без отдельного графического буфера на всю высоту длинного текста лицензии или списка правил.
 */
@Composable
fun PgCard(modifier: Modifier = Modifier, contentPadding: PaddingValues, content: @Composable ColumnScope.() -> Unit) {
    val shape = RoundedCornerShape(PgRadius.Card)
    val background = PgColor.Surface.Card.compositeOver(PgColor.Bg.Window)
    Column(
        modifier
            .pgShadow(PgShadow.Card, shape)
            .clip(shape)
            .background(background)
            .border(1.dp, PgColor.Line.Border, shape)
            .padding(contentPadding),
        content = content,
    )
}

/**
 * Иконка макета: контур 24×24 (PgIcons), рисуется линией; [filled] — еще и заливкой того же цвета, [fill] — заливкой своим
 * цветом (активный пункт островка — accent.navActiveFill).
 */
@Composable
fun PgIcon(
    path: String,
    contentDescription: String?,
    modifier: Modifier = Modifier,
    color: Color = LocalContentColor.current,
    size: Dp = 20.dp,
    strokeWidth: Float = 1.8f,
    filled: Boolean = false,
    fill: Color? = null,
) {
    val shape = remember(path) { PathParser().parsePathString(path).toPath() }
    val described = if (contentDescription != null) Modifier.semantics { this.contentDescription = contentDescription } else Modifier
    Canvas(modifier.size(size).then(described)) {
        val factor = this.size.minDimension / 24f
        scale(factor, factor, pivot = Offset.Zero) {
            val inside = fill ?: color.takeIf { filled }
            if (inside != null) {
                drawPath(shape, inside)
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

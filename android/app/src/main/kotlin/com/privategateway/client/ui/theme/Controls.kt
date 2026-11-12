package com.privategateway.client.ui.theme

import androidx.compose.animation.core.animateDpAsState
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsFocusedAsState
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.selection.selectableGroup
import androidx.compose.foundation.selection.toggleable
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.drawBehind
import androidx.compose.ui.draw.scale
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.input.VisualTransformation
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.dp
import com.privategateway.client.R

/*
  Блоки дизайн-системы телефона — те же, что app.css HTML-макета v1.1 (доска 24) и Styles.axaml десктопа. Наведения у
  телефона нет: его роль играет нажатие — синий текст и значки (accent.base), без серой заливки и ряби (решение 485);
  недоступные кнопки-надписи и строки — серый текст и прозрачность 0,6, кнопки действия — 0,45 (решение 530).
*/

/**
 * Вопрос перед выбором (как ConfirmRadioButton и ConfirmSwitch десктопа, решение 529): получает новое значение и функцию,
 * которая его применяет. Колбэк решает сам — применить сразу, после «Да» в диалоге или не применять вовсе; пока apply не
 * вызвана, выбранным остается прежнее значение: оно приходит от вызывающего, блок сам себя не переключает.
 */
typealias PgConfirm<T> = (value: T, apply: () -> Unit) -> Unit

/** Выбор с вопросом: то же значение — ничего, без вопроса — сразу, с вопросом — когда колбэк вызовет apply. */
internal fun <T> pgChoose(value: T, current: T, confirm: PgConfirm<T>?, onSelect: (T) -> Unit) {
    if (value == current) {
        return
    }

    if (confirm == null) {
        onSelect(value)
    } else {
        confirm(value) { onSelect(value) }
    }
}

/** Полоса line.divider по верхнему краю строки — между строками карточки (.setting + .setting, .link-row + .link-row). */
internal fun Modifier.pgTopDivider(shown: Boolean): Modifier =
    if (shown) drawBehind { drawRect(PgColor.Line.Divider, size = Size(size.width, 1.dp.toPx())) } else this

/** Кнопка макета: акцентная заливка, скругление radius.button, высота не меньше layout.touchTarget. */
@Composable
fun PgButton(label: String, enabled: Boolean, tag: String, onClick: () -> Unit) {
    val shape = RoundedCornerShape(PgRadius.Button)
    Box(
        Modifier
            .fillMaxWidth()
            .heightIn(min = PgLayout.TouchTarget)
            .clip(shape)
            .background(if (enabled) PgColor.Accent.Base else PgColor.Control.TrackOff)
            .clickable(enabled = enabled, role = Role.Button, onClick = onClick)
            .testTag(tag),
        contentAlignment = Alignment.Center,
    ) {
        Text(label, color = PgColor.Surface.Solid, style = PgType.BodyStrong)
    }
}

/** Основная кнопка действия (.btn-primary, Button.primary): accent.base, при нажатии — accent.text и сжатие 0,98; danger — красная. */
@Composable
fun PgPrimaryButton(
    label: String,
    tag: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
    danger: Boolean = false,
    height: Dp = 40.dp,
) {
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    val fill = when {
        danger -> if (pressed) PgColor.Status.Danger.Text else PgColor.Status.Danger.Base
        else -> if (pressed) PgColor.Accent.Text else PgColor.Accent.Base
    }
    ActionButton(label, tag, onClick, modifier, enabled, height, interaction, pressed, fill, null, PgColor.Surface.Solid, PgType.BodyStrong, 18.dp)
}

/**
 * Второстепенная кнопка действия (.btn-secondary, Button.secondary): белая с рамкой line.strong, при нажатии рамка и текст —
 * accent.base; danger — красный текст и рамка status.danger.line.
 */
@Composable
fun PgSecondaryButton(
    label: String,
    tag: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
    danger: Boolean = false,
    height: Dp = 40.dp,
) {
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    val edge = when {
        danger -> if (pressed) PgColor.Status.Danger.Base else PgColor.Status.Danger.Line
        else -> if (pressed) PgColor.Accent.Base else PgColor.Line.Strong
    }
    val ink = when {
        danger -> PgColor.Status.Danger.Text
        pressed -> PgColor.Accent.Base
        else -> PgColor.Text.Primary
    }
    ActionButton(label, tag, onClick, modifier, enabled, height, interaction, pressed, PgColor.Surface.Solid, edge, ink, PgType.Body, 14.dp)
}

@Composable
private fun ActionButton(
    label: String,
    tag: String,
    onClick: () -> Unit,
    modifier: Modifier,
    enabled: Boolean,
    height: Dp,
    interaction: MutableInteractionSource,
    pressed: Boolean,
    fill: Color,
    edge: Color?,
    ink: Color,
    style: TextStyle,
    side: Dp,
) {
    val shape = RoundedCornerShape(PgRadius.Input)
    Box(
        modifier
            .scale(if (pressed && enabled) 0.98f else 1f)
            .alpha(if (enabled) 1f else 0.45f)
            .heightIn(min = height)
            .clip(shape)
            .background(fill)
            .then(if (edge != null) Modifier.border(1.dp, edge, shape) else Modifier)
            .clickable(interactionSource = interaction, indication = null, enabled = enabled, role = Role.Button, onClick = onClick)
            .padding(horizontal = side, vertical = PgSpace.S)
            .testTag(tag),
        contentAlignment = Alignment.Center,
    ) {
        Text(label, color = ink, style = style, textAlign = TextAlign.Center)
    }
}

/**
 * Переключатель (.switch, ToggleButton.switch): дорожка 48 × 28, кружок 22 с тенью «карточка»; включен — accent.base,
 * выключен — control.trackOff. Сам не нажимается — нажимается строка настройки вокруг него.
 */
@Composable
fun PgSwitch(checked: Boolean, modifier: Modifier = Modifier, enabled: Boolean = true) {
    val knob by animateDpAsState(if (checked) 20.dp else 0.dp, tween(150), label = "knob")
    Box(
        modifier
            .alpha(if (enabled) 1f else 0.45f)
            .size(48.dp, 28.dp)
            .clip(CircleShape)
            .background(if (checked) PgColor.Accent.Base else PgColor.Control.TrackOff)
            .padding(3.dp),
    ) {
        Box(
            Modifier
                .offset { IntOffset(knob.roundToPx(), 0) }
                .size(22.dp)
                .pgShadow(PgShadow.Card, CircleShape)
                .clip(CircleShape)
                .background(PgColor.Surface.Solid),
        )
    }
}

/** Поля строки настройки (.setting): 12 сверху и снизу, 16 по бокам — строка сама до краев карточки. */
val PgSettingPadding = PaddingValues(horizontal = PgSpace.Card, vertical = PgSpace.L)

/**
 * Строка настройки (.setting): заголовок полужирным, подпись caption цветом text.muted, справа — [control] (переключатель,
 * кнопка-список). [divider] — полоса line.divider сверху: ее ставят всем строкам карточки, кроме первой. Недоступная
 * строка — серые заголовок и подпись. [extra] — еще строки под подписью (почему настройка сейчас не действует).
 */
@Composable
fun PgSetting(
    title: String,
    caption: String?,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
    divider: Boolean = false,
    padding: PaddingValues = PgSettingPadding,
    extra: (@Composable ColumnScope.() -> Unit)? = null,
    control: @Composable RowScope.() -> Unit = {},
) {
    Row(
        modifier
            .fillMaxWidth()
            .pgTopDivider(divider)
            .padding(padding),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(PgSpace.L),
    ) {
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
            Text(title, color = if (enabled) PgColor.Text.Primary else PgColor.Text.Muted, style = PgType.BodyStrong)
            if (!caption.isNullOrEmpty()) {
                Text(caption, color = PgColor.Text.Muted, style = PgType.Caption)
            }

            extra?.invoke(this)
        }

        control()
    }
}

/**
 * Строка настройки с переключателем: нажимается вся строка. С [confirm] строка сама не переключается — сначала вопрос
 * (выключить блок при обрыве — только через диалог, доска 24).
 */
@Composable
fun PgSettingSwitch(
    title: String,
    caption: String?,
    checked: Boolean,
    tag: String,
    onChange: (Boolean) -> Unit,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
    divider: Boolean = false,
    confirm: PgConfirm<Boolean>? = null,
    padding: PaddingValues = PgSettingPadding,
    extra: (@Composable ColumnScope.() -> Unit)? = null,
) {
    val interaction = remember { MutableInteractionSource() }
    PgSetting(
        title,
        caption,
        modifier
            .toggleable(
                value = checked,
                interactionSource = interaction,
                indication = null,
                enabled = enabled,
                role = Role.Switch,
                onValueChange = { pgChoose(it, checked, confirm, onChange) },
            )
            .testTag(tag),
        enabled,
        divider,
        padding,
        extra,
    ) {
        PgSwitch(checked, enabled = enabled)
    }
}

/** Переключатель с названием и пояснением в карточке с полями: строка настройки без своих боковых полей. */
@Composable
fun PgToggleRow(title: String, text: String?, checked: Boolean, tag: String, onChange: (Boolean) -> Unit) {
    PgSettingSwitch(title, text, checked, tag, onChange, padding = PaddingValues(vertical = PgSpace.L))
}

/**
 * Вариант выбора (один из нескольких) — как пункт списка .btn-option: выбранный — синим полужирным (accent.text 600) без
 * заливки, остальные — обычным; при нажатии — синий текст.
 */
@Composable
fun PgChoiceRow(title: String, text: String?, selected: Boolean, tag: String, onClick: () -> Unit) {
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    Row(
        Modifier
            .fillMaxWidth()
            .heightIn(min = PgLayout.TouchTarget)
            .clip(RoundedCornerShape(PgRadius.Input))
            .selectable(selected = selected, interactionSource = interaction, indication = null, role = Role.RadioButton, onClick = onClick)
            .padding(vertical = PgSpace.Xs)
            .testTag(tag),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
            val color = when {
                selected -> PgColor.Accent.Text
                pressed -> PgColor.Accent.Base
                else -> PgColor.Text.Primary
            }
            Text(title, color = color, style = if (selected) PgType.BodyStrong else PgType.Body)
            if (!text.isNullOrEmpty()) {
                Text(text, color = PgColor.Text.Muted, style = PgType.Caption)
            }
        }
    }
}

/**
 * Флажок (.check, CheckBox.check): квадрат 20 со скруглением 6 и рамкой control.checkboxBorder, отмечен — accent.base с
 * белой галочкой Chip.Ok. Нажимается вся строка; при нажатии рамка и подпись синеют. Ширину строки задает [modifier].
 */
@Composable
fun PgCheck(
    text: String,
    checked: Boolean,
    tag: String,
    onChange: (Boolean) -> Unit,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
) {
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    val box = RoundedCornerShape(6.dp)
    Row(
        modifier
            .alpha(if (enabled) 1f else 0.6f)
            .heightIn(min = 40.dp)
            .clip(RoundedCornerShape(PgRadius.Input))
            .toggleable(
                value = checked,
                interactionSource = interaction,
                indication = null,
                enabled = enabled,
                role = Role.Checkbox,
                onValueChange = onChange,
            )
            .padding(horizontal = PgSpace.S)
            .testTag(tag),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(
            Modifier
                .size(20.dp)
                .clip(box)
                .background(if (checked) PgColor.Accent.Base else PgColor.Surface.Solid)
                .border(1.5.dp, if (checked || (pressed && enabled)) PgColor.Accent.Base else PgColor.Control.CheckboxBorder, box),
            contentAlignment = Alignment.Center,
        ) {
            if (checked) {
                PgIcon(PgIcons.ChipOk, null, color = PgColor.Surface.Solid, size = 12.dp, strokeWidth = 3f)
            }
        }

        Spacer(Modifier.width(PgSpace.M))
        val color = when {
            !enabled -> PgColor.Text.Muted
            pressed -> PgColor.Accent.Base
            else -> PgColor.Text.Primary
        }
        Text(text, color = color, style = PgType.Body)
    }
}

/**
 * Сегменты (.segments, RadioButton.segment): дорожка control.segmentTrack с полем 4, выбранный сегмент — белая плашка с
 * тенью «карточка» и полужирным текстом. Тег сегмента — «[tag].значение». С [confirm] переход — только после ответа
 * («Только прокси», решение 529).
 */
@Composable
fun <T> PgSegments(
    options: List<T>,
    selected: T,
    label: @Composable (T) -> String,
    tag: String,
    onSelect: (T) -> Unit,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
    confirm: PgConfirm<T>? = null,
) {
    Row(
        modifier
            .fillMaxWidth()
            .alpha(if (enabled) 1f else 0.6f)
            .clip(RoundedCornerShape(PgRadius.Button))
            .background(PgColor.Control.SegmentTrack)
            .padding(PgSpace.Xxs)
            .selectableGroup()
            .testTag(tag),
    ) {
        for (option in options) {
            Segment(label(option), option == selected, enabled, "$tag.$option", Modifier.weight(1f)) {
                pgChoose(option, selected, confirm, onSelect)
            }
        }
    }
}

@Composable
private fun Segment(text: String, on: Boolean, enabled: Boolean, tag: String, modifier: Modifier, onClick: () -> Unit) {
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    val shape = RoundedCornerShape(PgRadius.Input)
    val plate = if (on) Modifier.pgShadow(PgShadow.Card, shape).clip(shape).background(PgColor.Surface.Solid) else Modifier.clip(shape)
    Box(
        modifier
            .heightIn(min = 38.dp)
            .then(plate)
            .selectable(selected = on, interactionSource = interaction, indication = null, enabled = enabled, role = Role.RadioButton, onClick = onClick)
            .padding(horizontal = PgSpace.Xxs)
            .testTag(tag),
        contentAlignment = Alignment.Center,
    ) {
        val color = when {
            !enabled -> PgColor.Text.Muted
            on -> PgColor.Text.Primary
            pressed -> PgColor.Accent.Base
            else -> PgColor.Text.Secondary
        }
        Text(
            text,
            color = color,
            style = if (on) PgType.Small.copy(fontWeight = FontWeight.SemiBold) else PgType.Small,
            textAlign = TextAlign.Center,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
    }
}

/** Плитка значка строки-ссылки (.row-icon): accent — синий значок на accent.tint, остальные — цветной на surface.inset. */
enum class PgTileTone(val background: Color, val content: Color) {
    Accent(PgColor.Accent.Tint, PgColor.Accent.Text),
    Violet(PgColor.Surface.Inset, PgColor.Tile.Violet),
    Teal(PgColor.Surface.Inset, PgColor.Tile.Teal),
    Amber(PgColor.Surface.Inset, PgColor.Tile.Amber),
}

/** Поля строки-ссылки (.link-row): 16 по бокам — строка сама до краев карточки, 6 сверху и снизу. */
val PgLinkRowPadding = PaddingValues(horizontal = PgSpace.Card, vertical = PgSpace.Xs)

/**
 * Строка-ссылка в карточке (.link-row): значок 18 в плитке 34, заголовок полужирным, подпись caption в одну строку, серая
 * стрелка «›» по правому краю. При нажатии заголовок и стрелка синеют. [divider] — полоса сверху, всем строкам кроме первой.
 * В карточке со своими полями — [padding] без боковых: полоса тогда тоже внутри полей. [tile] и [iconSize] — плитка и значок
 * крупнее («Для опытных» доски 4: плитка 36 со значком 20).
 */
@Composable
fun PgLinkRow(
    icon: String,
    title: String,
    caption: String?,
    tag: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    detail: String? = null,
    tone: PgTileTone = PgTileTone.Accent,
    enabled: Boolean = true,
    divider: Boolean = false,
    padding: PaddingValues = PgLinkRowPadding,
    tile: Dp = 34.dp,
    iconSize: Dp = 18.dp,
) {
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    val active = pressed && enabled
    Row(
        modifier
            .fillMaxWidth()
            .alpha(if (enabled) 1f else 0.6f)
            .pgTopDivider(divider)
            .heightIn(min = 52.dp)
            .clickable(interactionSource = interaction, indication = null, enabled = enabled, role = Role.Button, onClick = onClick)
            .padding(padding)
            .testTag(tag),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(PgSpace.L),
    ) {
        Box(
            Modifier
                .size(tile)
                .clip(RoundedCornerShape(PgRadius.Input))
                .background(tone.background),
            contentAlignment = Alignment.Center,
        ) {
            PgIcon(icon, null, color = tone.content, size = iconSize, strokeWidth = 1.9f)
            if (detail != null) {
                PgIcon(detail, null, color = tone.content, size = iconSize, strokeWidth = 1.9f)
            }
        }

        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
            val color = when {
                !enabled -> PgColor.Text.Muted
                active -> PgColor.Accent.Base
                else -> PgColor.Text.Primary
            }
            Text(title, color = color, style = PgType.BodyStrong)
            if (!caption.isNullOrEmpty()) {
                Text(caption, color = PgColor.Text.Muted, style = PgType.Caption, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
        }

        PgIcon(PgIcons.ChevronRight, null, color = if (active) PgColor.Accent.Base else PgColor.Text.Muted, size = 16.dp)
    }
}

/**
 * Поле ввода (.input, TextBox.input): белое, рамка line.strong, в фокусе — 1,5 accent.base; пустое — [placeholder] серым.
 * Строка ([singleLine]) — по центру высоты 44, в несколько строк — поля 10, как textarea макета; высоту больше задает
 * [modifier]. [label] — подпись для доступности, тег поля — [tag]. [keyboard] — какая клавиатура нужна полю (числа, адрес);
 * [actions] — что делает кнопка действия клавиатуры (Enter): например, отправить форму входа.
 */
@Composable
fun PgInput(
    value: String,
    onChange: (String) -> Unit,
    label: String,
    tag: String,
    modifier: Modifier = Modifier,
    placeholder: String? = null,
    style: TextStyle = PgType.Body,
    singleLine: Boolean = true,
    keyboard: KeyboardOptions = KeyboardOptions.Default,
    visual: VisualTransformation = VisualTransformation.None,
    actions: KeyboardActions = KeyboardActions.Default,
) {
    val shape = RoundedCornerShape(PgRadius.Input)
    val interaction = remember { MutableInteractionSource() }
    val focused by interaction.collectIsFocusedAsState()
    BasicTextField(
        value = value,
        onValueChange = onChange,
        singleLine = singleLine,
        keyboardOptions = keyboard,
        keyboardActions = actions,
        visualTransformation = visual,
        textStyle = style.copy(color = PgColor.Text.Primary),
        cursorBrush = SolidColor(PgColor.Accent.Base),
        interactionSource = interaction,
        modifier = modifier
            .fillMaxWidth()
            .heightIn(min = PgLayout.TouchTarget)
            .clip(shape)
            .background(PgColor.Surface.Solid)
            .border(if (focused) 1.5.dp else 1.dp, if (focused) PgColor.Accent.Base else PgColor.Line.Strong, shape)
            .padding(horizontal = PgSpace.L, vertical = if (singleLine) PgSpace.L else PgSpace.M)
            .semantics { contentDescription = label }
            .testTag(tag),
        decorationBox = { field ->
            Box {
                if (value.isEmpty() && placeholder != null) {
                    Text(placeholder, color = PgColor.Text.Muted, style = style)
                }

                field()
            }
        },
    )
}

/** Кнопка справа в заголовке вложенной страницы: значок и подпись — для доступности («+» — «Добавить подписку»). */
class PgHeaderAction(val icon: String, val label: String, val tag: String, val onClick: () -> Unit)

/** Заголовок вложенной страницы: «Назад», название и, если есть, кнопка-значок справа ([action], .btn-icon 44). */
@Composable
fun PgPageHeader(title: String, onBack: () -> Unit, action: PgHeaderAction? = null) {
    Row(Modifier.fillMaxWidth().padding(top = PgSpace.L), verticalAlignment = Alignment.CenterVertically) {
        val back = stringResource(R.string.common_back)
        Box(
            Modifier.size(PgLayout.TouchTarget).clip(CircleShape).clickable(onClickLabel = back, role = Role.Button, onClick = onBack),
            contentAlignment = Alignment.Center,
        ) {
            PgIcon(PgIcons.Back, back, color = PgColor.Text.Primary, size = 22.dp, strokeWidth = 2f)
        }

        Spacer(Modifier.width(PgSpace.S))
        Text(title, style = PgType.H1, modifier = Modifier.weight(1f))
        if (action != null) {
            HeaderAction(action)
        }
    }
}

/** Кнопка-значок заголовка: при нажатии значок синеет и кнопка сжимается до 0,98, без заливки (решение 485). */
@Composable
private fun HeaderAction(action: PgHeaderAction) {
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    Box(
        Modifier
            .size(PgLayout.TouchTarget)
            .scale(if (pressed) 0.98f else 1f)
            .clip(RoundedCornerShape(PgRadius.Button))
            .clickable(interactionSource = interaction, indication = null, onClickLabel = action.label, role = Role.Button, onClick = action.onClick)
            .semantics { contentDescription = action.label }
            .testTag(action.tag),
        contentAlignment = Alignment.Center,
    ) {
        PgIcon(action.icon, null, color = if (pressed) PgColor.Accent.Base else PgColor.Text.Primary, size = 22.dp, strokeWidth = 2f)
    }
}

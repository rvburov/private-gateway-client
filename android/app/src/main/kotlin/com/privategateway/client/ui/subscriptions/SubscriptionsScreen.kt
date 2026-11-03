package com.privategateway.client.ui.subscriptions

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
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.drawBehind
import androidx.compose.ui.draw.scale
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.PathEffect
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.stateDescription
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.privategateway.client.R
import com.privategateway.client.core.settings.SubscriptionSettings
import com.privategateway.client.core.subscriptions.FailureReason
import com.privategateway.client.core.subscriptions.RefreshInterval
import com.privategateway.client.core.text.Ago
import com.privategateway.client.core.text.Day
import com.privategateway.client.core.text.Plural
import com.privategateway.client.core.text.TimeText
import com.privategateway.client.core.vpn.TunnelState
import com.privategateway.client.data.GroupItem
import com.privategateway.client.ui.MainUi
import com.privategateway.client.ui.Texts
import com.privategateway.client.ui.theme.Overline
import com.privategateway.client.ui.theme.PgCard
import com.privategateway.client.ui.theme.PgColor
import com.privategateway.client.ui.theme.PgDialog
import com.privategateway.client.ui.theme.PgHeaderAction
import com.privategateway.client.ui.theme.PgIcon
import com.privategateway.client.ui.theme.PgIcons
import com.privategateway.client.ui.theme.PgInput
import com.privategateway.client.ui.theme.PgLayout
import com.privategateway.client.ui.theme.PgPageHeader
import com.privategateway.client.ui.theme.PgPrimaryButton
import com.privategateway.client.ui.theme.PgRadius
import com.privategateway.client.ui.theme.PgSecondaryButton
import com.privategateway.client.ui.theme.PgSelect
import com.privategateway.client.ui.theme.PgSetting
import com.privategateway.client.ui.theme.PgSettingSwitch
import com.privategateway.client.ui.theme.PgSheet
import com.privategateway.client.ui.theme.PgSpace
import com.privategateway.client.ui.theme.PgTone
import com.privategateway.client.ui.theme.PgType
import java.time.Clock
import java.time.format.DateTimeFormatter

/** Что умеет «Профили и подписки». */
class SubscriptionsActions(
    val settings: (SubscriptionSettings) -> Unit,
    val add: () -> Unit,
    val update: (GroupItem) -> Unit,
    val edit: (GroupItem, String, String) -> Boolean,
    val remove: (String?, String, Boolean) -> Unit,
)

/** Название группы — не длиннее 64 знаков, как у десктопа (MaxLength поля «Название»). */
private const val NAME_LIMIT = 64

/**
 * «Профили и подписки» (доска 12, SubscriptionsPage десктопа): шапка с «Назад» и «+» стоит, прокручивается остальное.
 * Как обновлять — автообновление (выпадающий список), «только через VPN» (защита выключена — подписки не обновляются),
 * сообщения о выбранном сервере; карточки подписок и профилей — состояние, маска адреса, когда обновлялась, причина
 * неудачи, «Обновить» (крутится стрелка), «Изменить» (нижний лист), корзина (диалог); «Мои серверы» — последней, под всеми —
 * «Добавить подписку».
 */
@Composable
fun SubscriptionsScreen(state: SubscriptionsUi, main: MainUi, actions: SubscriptionsActions, onBack: () -> Unit) {
    var editing by rememberSaveable { mutableStateOf<String?>(null) }
    var removing by rememberSaveable { mutableStateOf<String?>(null) }
    val language = Texts.language(LocalConfiguration.current.locales)
    // Группа сервера, через который идет или поднимается защита (и в ошибке — как у десктопа и в макете): удалять ее — значит
    // отключить VPN. «Мои серверы» — пустая строка.
    val status = main.status
    val inUse = status.serverId?.takeIf { status.state != TunnelState.Off }
        ?.let { id -> main.servers.firstOrNull { it.id == id } }
        ?.let { it.subscriptionId ?: MINE }
    val add = stringResource(R.string.subs_add)
    Column(Modifier.fillMaxSize().statusBarsPadding().testTag("subscriptions")) {
        Box(Modifier.padding(horizontal = PgSpace.WindowSideMobile)) {
            PgPageHeader(stringResource(R.string.settings_adv_profiles_title), onBack, PgHeaderAction(PgIcons.Plus, add, "subs.add.header", actions.add))
        }

        Column(
            Modifier
                .fillMaxWidth()
                .weight(1f)
                .verticalScroll(rememberScrollState())
                .padding(start = PgSpace.WindowSideMobile, top = PgSpace.L, end = PgSpace.WindowSideMobile, bottom = PgLayout.ScrollBottomPadding),
            verticalArrangement = Arrangement.spacedBy(14.dp),
        ) {
            SettingsCard(state, actions.settings)
            // Считаются все карточки, и «Мои серверы» тоже (SectionTitle десктопа).
            val cards = state.groups.size + if (state.manualCount > 0) 1 else 0
            Overline(stringResource(R.string.subs_section, cards.toString()), Modifier.padding(start = PgSpace.Xs, top = PgSpace.Xxs, end = PgSpace.Xs))
            if (cards == 0) {
                Text(stringResource(R.string.subs_empty), Modifier.padding(horizontal = PgSpace.Xs), color = PgColor.Text.Secondary, style = PgType.Small)
            }

            for (group in state.groups) {
                GroupCard(group, language, busy = group.id in state.busy, actions, onEdit = { editing = group.id }, onRemove = { removing = group.id })
            }

            if (state.manualCount > 0) {
                MineCard(state.manualCount, language) { removing = MINE }
            }

            AddButton(add, actions.add)
        }
    }

    val edited = editing?.let { id -> state.groups.firstOrNull { it.id == id } }
    if (edited != null) {
        EditSheet(edited, onSave = { name, url -> actions.edit(edited, name, url) }, onClose = { editing = null })
    }

    val target = removing
    val group = target?.let { id -> state.groups.firstOrNull { it.id == id } }
    // Группа пропала, пока диалог был открыт (удалена обновлением в фоне), — спрашивать не о чем.
    if (target != null && (target == MINE || group != null)) {
        val name = group?.name ?: stringResource(R.string.subs_mine_name)
        val count = group?.servers ?: state.manualCount
        val servers = stringResource(serversText(count, language), count.toString())
        val active = inUse == target
        PgDialog(
            stringResource(R.string.subs_remove_title, name),
            stringResource(if (active) R.string.subs_remove_textInUse else R.string.subs_remove_text, servers),
            PgIcons.Trash,
            PgTone.Danger,
            stringResource(R.string.dialog_deleteServer_delete),
            {
                removing = null
                actions.remove(group?.id, name, active)
            },
            stringResource(R.string.dialog_cancel),
            { removing = null },
            { removing = null },
            "dialog.removeGroup",
            primaryDanger = true,
        )
    }
}

/** «Мои серверы» в [SubscriptionsScreen]: метка группы без подписки. */
private const val MINE = ""

/**
 * «Как обновлять» (Border.card 16,2): три строки настройки с полосами между ними — автообновление со списком справа, «только
 * через VPN» (выключена защита — третьей строкой подписи, почему подписки не обновляются) и сообщения о выбранном сервере.
 */
@Composable
private fun SettingsCard(state: SubscriptionsUi, onChange: (SubscriptionSettings) -> Unit) {
    val settings = state.settings
    val rows = PaddingValues(vertical = PgSpace.L)
    PgCard(Modifier.fillMaxWidth(), contentPadding = PaddingValues(horizontal = PgSpace.Card, vertical = 2.dp)) {
        PgSetting(stringResource(R.string.subs_refresh_title), stringResource(R.string.subs_refresh_text), padding = rows) {
            PgSelect(RefreshInterval.entries, settings.refresh, { refreshText(it) }, "refresh", { onChange(settings.copy(refresh = it)) })
        }

        val blocked = settings.onlyViaVpn && !state.connected
        PgSettingSwitch(
            stringResource(R.string.subs_vpn_title),
            stringResource(R.string.subs_vpn_text),
            settings.onlyViaVpn,
            "subs.vpn",
            { onChange(settings.copy(onlyViaVpn = it)) },
            divider = true,
            padding = rows,
            extra = {
                if (blocked) {
                    Text(
                        stringResource(R.string.subs_vpn_blocked_off),
                        Modifier.testTag("subs.vpn.blocked"),
                        color = PgColor.Status.Warning.Badge,
                        style = PgType.Caption,
                    )
                }
            },
        )
        PgSettingSwitch(
            stringResource(R.string.subs_notify_title),
            stringResource(R.string.subs_notify_text),
            settings.notifySelected,
            "subs.notify",
            { onChange(settings.copy(notifySelected = it)) },
            divider = true,
            padding = rows,
        )
    }
}

/** «выключено», «каждые 6 ч», «каждые 12 ч», «каждые 24 ч». */
@Composable
private fun refreshText(interval: RefreshInterval): String = stringResource(
    when (interval) {
        RefreshInterval.Off -> R.string.subs_refresh_off
        RefreshInterval.Every6Hours -> R.string.subs_refresh_6
        RefreshInterval.Every12Hours -> R.string.subs_refresh_12
        RefreshInterval.Every24Hours -> R.string.subs_refresh_24
    },
)

/** Название группы (.sb-name): размер title, жирным. */
private val GroupName = PgType.Title.copy(fontWeight = FontWeight.Bold)

/** Адрес группы (.sb-address): моноширинным размера caption. */
private val GroupAddress = PgType.Mono.copy(fontSize = PgType.Caption.fontSize, fontWeight = FontWeight.Normal)

/** Карточка группы (Border.subscription): surface.card, рамка line.border, скругление 18, поля 16 / 14 / 12 / 12, без тени. */
@Composable
private fun GroupFrame(tag: String, content: @Composable ColumnScope.() -> Unit) {
    val shape = RoundedCornerShape(18.dp)
    Column(
        Modifier
            .fillMaxWidth()
            .clip(shape)
            .background(PgColor.Surface.Card)
            .border(1.dp, PgColor.Line.Border, shape)
            .padding(start = PgSpace.Card, top = 14.dp, end = PgSpace.L, bottom = PgSpace.L)
            .testTag(tag),
        verticalArrangement = Arrangement.spacedBy(PgSpace.S),
        content = content,
    )
}

/** Верх карточки: название и адрес (маска или «без адреса») слева, метка состояния справа. */
@Composable
private fun GroupHead(name: String, address: String, chip: Chip) {
    Row(verticalAlignment = Alignment.Top) {
        Column(Modifier.weight(1f).padding(end = PgSpace.M), verticalArrangement = Arrangement.spacedBy(3.dp)) {
            Text(name, color = PgColor.Text.Primary, style = GroupName, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Text(address, color = PgColor.Text.Muted, style = GroupAddress, maxLines = 1, overflow = TextOverflow.Ellipsis)
        }

        StateChip(chip)
    }
}

/**
 * Карточка подписки или профиля: название и состояние, адрес (маска), серверы и время, причина неудачи — оранжевой плашкой;
 * «Обновить» (только у подписки с адресом), «Изменить» и корзина справа.
 */
@Composable
private fun GroupCard(group: GroupItem, language: String, busy: Boolean, actions: SubscriptionsActions, onEdit: () -> Unit, onRemove: () -> Unit) {
    val chip = when {
        !group.canUpdate -> Chip.Manual
        group.failure != null -> Chip.Error
        else -> Chip.Ok
    }
    GroupFrame("group.${group.id}") {
        GroupHead(group.name, group.maskedUrl ?: stringResource(R.string.subs_manual_address), chip)
        Text(meta(group, language), Modifier.testTag("group.meta"), color = PgColor.Text.Secondary, style = PgType.Small)
        val failure = group.failure
        if (failure != null) {
            val (text, arg) = Texts.failure(FailureReason.of(failure, group.status))
            val reason = if (arg != null) stringResource(text, arg) else stringResource(text)
            val locale = LocalConfiguration.current.locales[0]
            val shape = RoundedCornerShape(PgRadius.Input)
            Text(
                stringResource(R.string.subs_error, reason.replaceFirstChar { it.titlecase(locale) }),
                Modifier
                    .fillMaxWidth()
                    .clip(shape)
                    .background(PgColor.Status.Warning.Tint)
                    .border(1.dp, PgColor.Status.Warning.Line, shape)
                    .padding(horizontal = PgSpace.M, vertical = PgSpace.S)
                    .testTag("group.error"),
                color = PgColor.Status.Warning.Badge,
                style = PgType.Caption,
            )
        }

        Row(Modifier.fillMaxWidth().padding(top = 2.dp), verticalAlignment = Alignment.CenterVertically) {
            if (group.canUpdate) {
                UpdateButton(busy) { actions.update(group) }
                Spacer(Modifier.width(PgSpace.Xs))
            }

            SmallButton(stringResource(R.string.subs_edit), "subs.edit", onEdit)
            Spacer(Modifier.weight(1f))
            RemoveButton(stringResource(R.string.subs_remove, group.name), onRemove)
        }
    }
}

/** «Мои серверы»: серверы без группы — добавлены ссылками; не обновляются и не меняются, удалить можно разом. */
@Composable
private fun MineCard(count: Int, language: String, onRemove: () -> Unit) {
    val name = stringResource(R.string.subs_mine_name)
    GroupFrame("group.mine") {
        GroupHead(name, stringResource(R.string.subs_mine_address), Chip.Manual)
        Text(stringResource(serversText(count, language), count.toString()), color = PgColor.Text.Secondary, style = PgType.Small)
        Row(Modifier.fillMaxWidth().padding(top = 2.dp), verticalAlignment = Alignment.CenterVertically) {
            Spacer(Modifier.weight(1f))
            RemoveButton(stringResource(R.string.subs_remove, name), onRemove)
        }
    }
}

/** Небольшая кнопка (.btn-small): высота 36, скругление 10, поля 12; [soft] — синяя мягкая (accent.tint, рамка accent.line). */
@Composable
private fun SmallFrame(
    soft: Boolean,
    tag: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
    content: @Composable (pressed: Boolean) -> Unit,
) {
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    val shape = RoundedCornerShape(PgRadius.Input)
    val edge = when {
        soft -> PgColor.Accent.Line
        pressed -> PgColor.Accent.Line
        else -> PgColor.Line.Strong
    }
    Row(
        modifier
            .height(36.dp)
            .clip(shape)
            .background(if (soft) PgColor.Accent.Tint else PgColor.Surface.Solid)
            .border(1.dp, edge, shape)
            .clickable(interactionSource = interaction, indication = null, enabled = enabled, role = Role.Button, onClick = onClick)
            .padding(horizontal = PgSpace.L)
            .testTag(tag),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(PgSpace.Xs),
    ) {
        content(pressed && enabled)
    }
}

/** «Изменить» (.btn-small): белая, текст small; при нажатии рамка accent.line и синий текст. */
@Composable
private fun SmallButton(label: String, tag: String, onClick: () -> Unit) {
    SmallFrame(soft = false, tag = tag, onClick = onClick) { pressed ->
        Text(label, color = if (pressed) PgColor.Accent.Base else PgColor.Text.Primary, style = PgType.Small, maxLines = 1)
    }
}

/**
 * «Обновить» (.btn-small.soft) со стрелкой 15: пока подписка загружается, вид не меняется — крутится только стрелка
 * (Classes.spin десктопа), а повторное нажатие не считается; для чтения с экрана — «Загружаем подписку…».
 */
@Composable
private fun UpdateButton(busy: Boolean, onClick: () -> Unit) {
    val downloading = stringResource(R.string.import_downloading)
    SmallFrame(
        soft = true,
        tag = "subs.update",
        onClick = onClick,
        modifier = Modifier.semantics {
            if (busy) {
                stateDescription = downloading
            }
        },
        enabled = !busy,
    ) { pressed ->
        val color = if (pressed) PgColor.Accent.Base else PgColor.Accent.Text
        if (busy) {
            val spin = rememberInfiniteTransition(label = "update")
            val angle by spin.animateFloat(0f, 360f, infiniteRepeatable(tween(900, easing = LinearEasing)), label = "angle")
            PgIcon(PgIcons.Refresh, null, Modifier.graphicsLayer { rotationZ = angle }.testTag("subs.update.spin"), color = color, size = 15.dp, strokeWidth = 2.2f)
        } else {
            PgIcon(PgIcons.Refresh, null, color = color, size = 15.dp, strokeWidth = 2.2f)
        }

        Text(stringResource(R.string.subs_update), color = color, style = PgType.Small.copy(fontWeight = FontWeight.SemiBold), maxLines = 1)
    }
}

/** Корзина (.sb-remove, 36): своего цвета status.danger.text — при нажатии не синеет (решение 485), только сжимается. */
@Composable
private fun RemoveButton(label: String, onClick: () -> Unit) {
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    Box(
        Modifier
            .size(36.dp)
            .scale(if (pressed) 0.98f else 1f)
            .clip(RoundedCornerShape(PgRadius.Button))
            .clickable(interactionSource = interaction, indication = null, onClickLabel = label, role = Role.Button, onClick = onClick)
            .semantics { contentDescription = label }
            .testTag("subs.remove"),
        contentAlignment = Alignment.Center,
    ) {
        PgIcon(PgIcons.Trash, null, color = PgColor.Status.Danger.Text, size = 18.dp, strokeWidth = 1.9f)
    }
}

/**
 * «Добавить подписку» (.sb-add): высота 46, пунктирная рамка accent.line на accent.tint, «+» и текст accent.text; при нажатии
 * рамка и текст accent.base, сжатие 0,98. Ведет на «Добавить профиль», как и «+» в шапке.
 */
@Composable
private fun AddButton(label: String, onClick: () -> Unit) {
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    val edge = if (pressed) PgColor.Accent.Base else PgColor.Accent.Line
    val ink = if (pressed) PgColor.Accent.Base else PgColor.Accent.Text
    val shape = RoundedCornerShape(14.dp)
    Row(
        Modifier
            .fillMaxWidth()
            .height(46.dp)
            .scale(if (pressed) 0.98f else 1f)
            .clip(shape)
            .background(PgColor.Accent.Tint)
            .drawBehind {
                val stroke = 1.5.dp.toPx()
                drawRoundRect(
                    color = edge,
                    topLeft = Offset(stroke / 2, stroke / 2),
                    size = Size(size.width - stroke, size.height - stroke),
                    cornerRadius = CornerRadius(14.dp.toPx() - stroke / 2),
                    style = Stroke(width = stroke, pathEffect = PathEffect.dashPathEffect(floatArrayOf(4.dp.toPx(), 3.dp.toPx()))),
                )
            }
            .clickable(interactionSource = interaction, indication = null, role = Role.Button, onClick = onClick)
            .testTag("subs.add"),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(PgSpace.S, Alignment.CenterHorizontally),
    ) {
        PgIcon(PgIcons.Plus, null, color = ink, size = 18.dp, strokeWidth = 2.2f)
        Text(label, color = ink, style = PgType.BodyStrong)
    }
}

/**
 * «Изменить» — нижний лист (SubscriptionEditView десктопа): ручка, заголовок «Изменить «…»» и крестик; название (до 64
 * знаков) и новый адрес (пустой — прежний; подсказка — маска текущего), адрес хранится только в Android Keystore; внизу рядом
 * «Отмена» и «Сохранить». Неверный адрес — лист остается и говорит, какой нужен.
 */
@Composable
private fun EditSheet(group: GroupItem, onSave: (String, String) -> Boolean, onClose: () -> Unit) {
    var name by rememberSaveable(group.id) { mutableStateOf(group.name) }
    // Новый адрес подписки — секрет: в сохраненное состояние окна он не пишется.
    var url by remember(group.id) { mutableStateOf("") }
    var bad by rememberSaveable(group.id) { mutableStateOf(false) }
    PgSheet(onClose, "subs.editor", scroll = false) {
        Row(Modifier.fillMaxWidth().padding(top = 2.dp), verticalAlignment = Alignment.CenterVertically) {
            Text(
                stringResource(R.string.subs_edit_title, group.name),
                Modifier.weight(1f).testTag("subs.edit.title"),
                color = PgColor.Text.Primary,
                style = PgType.Status,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
            CloseButton(onClose)
        }

        Column(
            Modifier
                .fillMaxWidth()
                .weight(1f)
                .verticalScroll(rememberScrollState())
                .padding(vertical = PgSpace.Card),
            verticalArrangement = Arrangement.spacedBy(14.dp),
        ) {
            val nameLabel = stringResource(R.string.subs_edit_name)
            Field(nameLabel) {
                PgInput(name, { name = it.take(NAME_LIMIT) }, nameLabel, "subs.edit.name")
            }

            // Профилю без адреса тоже можно дать адрес — он станет подпиской.
            val urlLabel = stringResource(R.string.subs_edit_url)
            Field(urlLabel) {
                PgInput(
                    url,
                    {
                        url = it
                        bad = false
                    },
                    urlLabel,
                    "subs.edit.url",
                    placeholder = group.maskedUrl ?: "https://…",
                    style = GroupAddress,
                )
                Text(stringResource(R.string.subs_edit_url_note, "Android Keystore"), color = PgColor.Text.Muted, style = PgType.Caption)
                if (bad) {
                    Text(stringResource(R.string.subs_edit_url_bad), Modifier.testTag("subs.edit.bad"), color = PgColor.Status.Warning.Badge, style = PgType.Caption)
                }
            }
        }

        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(PgSpace.S)) {
            PgSecondaryButton(stringResource(R.string.dialog_cancel), "subs.edit.cancel", onClose, Modifier.weight(1f), height = 50.dp)
            PgPrimaryButton(
                stringResource(R.string.subs_edit_save),
                "subs.edit.save",
                {
                    if (onSave(name, url)) {
                        onClose()
                    } else {
                        bad = true
                    }
                },
                Modifier.weight(1f),
                enabled = name.isNotBlank(),
                height = 50.dp,
            )
        }
    }
}

/** Поле листа: подпись small полужирным цветом text.secondary и под ней само поле с пояснениями. */
@Composable
private fun Field(label: String, content: @Composable ColumnScope.() -> Unit) {
    Column(verticalArrangement = Arrangement.spacedBy(PgSpace.Xs)) {
        Text(label, color = PgColor.Text.Secondary, style = PgType.Small.copy(fontWeight = FontWeight.SemiBold))
        content()
    }
}

/** Крестик листа (40): значок text.secondary, при нажатии — синий и сжатие 0,98. */
@Composable
private fun CloseButton(onClose: () -> Unit) {
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
            .testTag("subs.edit.close"),
        contentAlignment = Alignment.Center,
    ) {
        PgIcon(PgIcons.Close, null, color = if (pressed) PgColor.Accent.Base else PgColor.Text.Secondary, size = 20.dp, strokeWidth = 2.2f)
    }
}

private enum class Chip {
    Ok,
    Error,
    Manual,
}

/**
 * Состояние группы (Border.health): круглая метка caption полужирным — «Работает» (success.tint / success.badge), «Ошибка»
 * (warning.tint / warning.badge), «Вручную» (control.segmentTrack / text.secondary).
 */
@Composable
private fun StateChip(chip: Chip) {
    val (text, color, tint) = when (chip) {
        Chip.Ok -> Triple(R.string.subs_chip_ok, PgColor.Status.Success.Badge, PgColor.Status.Success.Tint)
        Chip.Error -> Triple(R.string.subs_chip_error, PgColor.Status.Warning.Badge, PgColor.Status.Warning.Tint)
        Chip.Manual -> Triple(R.string.subs_chip_manual, PgColor.Text.Secondary, PgColor.Control.SegmentTrack)
    }

    Text(
        stringResource(text),
        Modifier
            .clip(CircleShape)
            .background(tint)
            .padding(horizontal = 9.dp, vertical = 3.dp)
            .testTag("group.chip"),
        color = color,
        style = PgType.Caption.copy(fontWeight = FontWeight.SemiBold),
        maxLines = 1,
    )
}

/** «12 серверов · обновлено в 18:10» или после неудачи «12 серверов · последняя попытка 5 мин назад». */
@Composable
private fun meta(group: GroupItem, language: String): String {
    val servers = stringResource(serversText(group.servers, language), group.servers.toString())
    val clock = Clock.systemDefaultZone()
    val attempt = group.lastAttemptAt
    val updated = group.updatedAt
    return when {
        group.failure != null && attempt != null -> stringResource(R.string.subs_meta_attempt, servers, ago(TimeText.ago(attempt, clock)))
        updated != null -> stringResource(R.string.subs_meta_updated, servers, day(TimeText.day(updated, clock)))
        else -> servers
    }
}

/** «в 18:10», «вчера», «22 окт.» — как `TimeText.Day` десктопа; им пользуются и «Обновления» в «О программе». */
@Composable
internal fun day(day: Day): String = when (day) {
    is Day.Today -> stringResource(R.string.when_at, day.time.format(DateTimeFormatter.ofPattern("HH:mm")))
    Day.Yesterday -> stringResource(R.string.when_yesterday)
    is Day.Date -> day.date.format(DateTimeFormatter.ofPattern("d MMM", LocalConfiguration.current.locales[0]))
}

@Composable
private fun ago(ago: Ago): String = when (ago) {
    Ago.Now -> stringResource(R.string.ago_now)
    is Ago.Minutes -> stringResource(R.string.ago_minutes, ago.count.toString())
    is Ago.Hours -> stringResource(R.string.ago_hours, ago.count.toString())
    is Ago.Days -> stringResource(R.string.ago_days, ago.count.toString())
}

/** «1 сервер», «3 сервера», «12 серверов». */
private fun serversText(count: Int, language: String): Int = when (Plural.form(count, language)) {
    "one" -> R.string.subs_servers_one
    "few" -> R.string.subs_servers_few
    else -> R.string.subs_servers_many
}

package com.privategateway.client.ui.servers

import android.os.Build
import androidx.activity.compose.LocalActivity
import androidx.annotation.StringRes
import androidx.compose.foundation.Image
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
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.scale
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.FilterQuality
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LifecycleEventEffect
import com.privategateway.client.R
import com.privategateway.client.core.qr.QrCode
import com.privategateway.client.core.servers.LatencyFailure
import com.privategateway.client.core.servers.LatencyKind
import com.privategateway.client.core.servers.ServerFacts
import com.privategateway.client.core.servers.ServerItem
import com.privategateway.client.core.servers.ServerNames
import com.privategateway.client.core.servers.SubscriptionRef
import com.privategateway.client.ui.MainUi
import com.privategateway.client.ui.qr.qrBitmap
import com.privategateway.client.ui.theme.LocalPgToast
import com.privategateway.client.ui.theme.Overline
import com.privategateway.client.ui.theme.PgColor
import com.privategateway.client.ui.theme.PgDialog
import com.privategateway.client.ui.theme.PgIcon
import com.privategateway.client.ui.theme.PgIcons
import com.privategateway.client.ui.theme.PgPrimaryButton
import com.privategateway.client.ui.theme.PgRadius
import com.privategateway.client.ui.theme.PgSecondaryButton
import com.privategateway.client.ui.theme.PgSheet
import com.privategateway.client.ui.theme.PgSpace
import com.privategateway.client.ui.theme.PgTone
import com.privategateway.client.ui.theme.PgType
import com.privategateway.client.ui.theme.pgTopDivider
import kotlinx.coroutines.launch

/**
 * Что умеет карточка сервера. [close] закрывает лист (крестик, нажатие мимо, «Назад», «Подключиться»); [address] открывает
 * запечатанную ссылку — только когда карточка открыта; [exit] — последний IP, который сайты видели через сервер (решение 583),
 * null — не мерили; [share] — ссылка для QR-кода, null — ключа нет.
 */
class CardActions(
    val close: () -> Unit,
    val connect: (String) -> Unit,
    val favorite: (String, Boolean) -> Unit,
    val rename: (String, String) -> Unit,
    val delete: (String) -> Unit,
    val address: suspend (String) -> String?,
    val share: suspend (String) -> String? = { null },
    val copy: (String) -> Unit = {},
    val exit: suspend (String) -> String? = { null },
)

/**
 * Карточка сервера (доска 11, ServerCardView десктопа) — нижний лист поверх «Серверов»: значок страны, название и откуда
 * сервер, звезда избранного и крестик; чипы «Активен»/«Выбран» и задержки; сведения (адрес, протокол, транспорт, защита,
 * ядро) — или на их месте поле названия («Переименовать») или QR-код («Поделиться»); внизу «Подключиться» или «Используется
 * сейчас» и плитки «Переименовать» · «Поделиться» · «Удалить». «Подключиться» закрывает лист и включает защиту; удаление —
 * после подтверждения. Заголовок и кнопки стоят, середина прокручивается.
 */
@Composable
fun ServerCardScreen(ui: MainUi, id: String, actions: CardActions) {
    val server = ui.servers.firstOrNull { it.id == id }
    if (server == null) {
        // Сервер удален (здесь или обновлением подписки) — карточке нечего показывать.
        LaunchedEffect(id) { actions.close() }
        return
    }

    val active = ui.active(server.id)
    val selected = server.id == ui.selectedId
    var renaming by rememberSaveable(server.id) { mutableStateOf(false) }
    var deleting by rememberSaveable(server.id) { mutableStateOf(false) }
    // Ссылка с ключом — только пока QR-код на экране: уход окна в фон его прячет (решение 436).
    var link by remember(server.id) { mutableStateOf<String?>(null) }
    // Ключа в хранилище нет — делиться нечем: плитка «Поделиться» недоступна.
    var noKey by remember(server.id) { mutableStateOf(false) }
    LifecycleEventEffect(Lifecycle.Event.ON_STOP) { link = null }
    val toast = LocalPgToast.current
    val noKeyText = stringResource(R.string.card_share_noKey)
    val scope = rememberCoroutineScope()

    PgSheet(actions.close, "card", toast = toast, scroll = false) {
        Head(server, ui.subscriptions, onFavorite = { actions.favorite(server.id, it) }, onClose = actions.close)
        Column(
            Modifier
                .fillMaxWidth()
                .weight(1f)
                .verticalScroll(rememberScrollState())
                .padding(vertical = PgSpace.L),
            verticalArrangement = Arrangement.spacedBy(PgSpace.L),
        ) {
            Chips(server, active, selected)
            val shown = link
            when {
                shown != null -> Share(shown) { actions.copy(shown) }
                renaming -> Rename(server, onCancel = { renaming = false }) { name ->
                    actions.rename(server.id, name)
                    renaming = false
                }

                else -> Details(server, actions.address, actions.exit)
            }
        }

        Column(verticalArrangement = Arrangement.spacedBy(PgSpace.S)) {
            if (active) {
                InUse()
            } else {
                // Hysteria2 и TUIC ядру телефона не по силам — к ним не подключиться (строка «Ядро» это объясняет).
                PgPrimaryButton(
                    stringResource(R.string.card_connect),
                    "card.connect",
                    {
                        actions.close()
                        actions.connect(server.id)
                    },
                    Modifier.fillMaxWidth(),
                    enabled = ServerFacts.coreName(server.protocol) != null,
                    height = 50.dp,
                )
            }

            Row(horizontalArrangement = Arrangement.spacedBy(PgSpace.S)) {
                Tile(PgIcons.Edit, stringResource(R.string.card_rename), "card.rename", Modifier.weight(1f)) {
                    link = null
                    renaming = true
                }

                val sharing = link != null
                Tile(
                    PgIcons.Qr,
                    stringResource(if (sharing) R.string.card_share_hide else R.string.card_share),
                    if (sharing) "card.share.hide" else "card.share",
                    Modifier.weight(1f),
                    enabled = !noKey,
                    on = sharing,
                ) {
                    if (sharing) {
                        link = null
                    } else {
                        // Ключ открывается из хранилища только сейчас, по нажатию.
                        scope.launch {
                            val opened = actions.share(server.id)
                            if (opened == null) {
                                noKey = true
                                toast.show(noKeyText)
                            } else {
                                renaming = false
                                link = opened
                            }
                        }
                    }
                }

                Tile(PgIcons.Trash, stringResource(R.string.card_delete), "card.delete", Modifier.weight(1f), danger = true) { deleting = true }
            }
        }
    }

    if (deleting) {
        PgDialog(
            stringResource(R.string.dialog_deleteServer_title, server.displayName),
            stringResource(if (active) R.string.dialog_deleteServer_textInUse else R.string.dialog_deleteServer_text),
            PgIcons.Trash,
            PgTone.Danger,
            stringResource(R.string.dialog_deleteServer_delete),
            {
                deleting = false
                actions.delete(server.id)
            },
            stringResource(R.string.dialog_cancel),
            { deleting = false },
            { deleting = false },
            "dialog.deleteServer",
            primaryDanger = true,
        )
    }
}

/** Заголовок листа: значок страны 52 × 38, название (t-status) и «откуда» под ним, звезда избранного и крестик. */
@Composable
private fun Head(server: ServerItem, subscriptions: List<SubscriptionRef>, onFavorite: (Boolean) -> Unit, onClose: () -> Unit) {
    Row(Modifier.fillMaxWidth().padding(top = 2.dp), verticalAlignment = Alignment.CenterVertically) {
        CountryCode(server.countryCode, large = true)
        Column(Modifier.weight(1f).padding(start = PgSpace.L, end = PgSpace.S), verticalArrangement = Arrangement.spacedBy(2.dp)) {
            Text(server.displayName, Modifier.testTag("card.title"), style = PgType.Status, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Text(source(server, subscriptions), Modifier.testTag("card.subtitle"), color = PgColor.Text.Muted, style = PgType.Caption)
        }

        val favorite = stringResource(if (server.favorite) R.string.card_favorite_remove else R.string.card_favorite_add)
        HeadButton(favorite, "card.favorite", { onFavorite(!server.favorite) }) { pressed ->
            if (server.favorite) {
                FavoriteMark(22.dp)
            } else {
                PgIcon(PgIcons.Star, null, color = if (pressed) PgColor.Favorite.Stroke else PgColor.Text.Muted, size = 22.dp)
            }
        }

        HeadButton(stringResource(R.string.card_close), "card.close", onClose) { pressed ->
            PgIcon(PgIcons.Close, null, color = if (pressed) PgColor.Accent.Base else PgColor.Text.Secondary, size = 20.dp, strokeWidth = 2.2f)
        }
    }
}

/**
 * Откуда сервер (подзаголовок карточки, Subtitle десктопа): «из подписки «…»», «добавлен ссылкой», а пропавший из подписки —
 * «Пропал из подписки — остается, пока выбран». Города в ссылке нет — у десктопа он идет перед источником.
 */
@Composable
private fun source(server: ServerItem, subscriptions: List<SubscriptionRef>): String {
    val group = server.subscriptionId?.let { id -> subscriptions.firstOrNull { it.id == id } }
    return when {
        server.missingSince != null -> stringResource(R.string.card_missing)
        group != null -> stringResource(R.string.card_from_subscription, group.name)
        else -> stringResource(R.string.card_from_link)
    }
}

/** Кнопка-значок заголовка (.btn-icon, 40): подпись — только для доступности; при нажатии сжатие 0,98. */
@Composable
private fun HeadButton(label: String, tag: String, onClick: () -> Unit, icon: @Composable (pressed: Boolean) -> Unit) {
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    Box(
        Modifier
            .size(40.dp)
            .scale(if (pressed) 0.98f else 1f)
            .clip(RoundedCornerShape(PgRadius.Button))
            .clickable(interactionSource = interaction, indication = null, onClickLabel = label, role = Role.Button, onClick = onClick)
            .semantics { contentDescription = label }
            .testTag(tag),
        contentAlignment = Alignment.Center,
    ) {
        icon(pressed)
    }
}

/** Звезда избранного: заливка и контур цветами favorite.* макета. */
@Composable
private fun FavoriteMark(size: Dp) {
    Box {
        PgIcon(PgIcons.Star, null, color = PgColor.Favorite.Fill, size = size, filled = true)
        PgIcon(PgIcons.Star, null, color = PgColor.Favorite.Stroke, size = size)
    }
}

/** Текст чипа (.sc-chip): caption, полужирным. */
private val ChipText = PgType.Caption.copy(fontWeight = FontWeight.SemiBold)

/**
 * Ряд чипов: «Активен» — защита идет или поднимается через этот сервер (зеленый, с точкой), «Выбран» — к нему подключит
 * щит (синий, с точкой); задержка — «42 мс», «нет ответа» или «—», если еще не мерили.
 */
@Composable
private fun Chips(server: ServerItem, active: Boolean, selected: Boolean) {
    Row(horizontalArrangement = Arrangement.spacedBy(PgSpace.S)) {
        when {
            active -> Chip(PgColor.Status.Success.Tint, PgColor.Status.Success.Base, "card.status") {
                Text(stringResource(R.string.card_status_active), color = PgColor.Status.Success.Badge, style = ChipText, maxLines = 1)
            }

            selected -> Chip(PgColor.Accent.Tint, PgColor.Accent.Base, "card.status") {
                Text(stringResource(R.string.card_status_selected), color = PgColor.Accent.Text, style = ChipText, maxLines = 1)
            }

            else -> Unit
        }

        Chip(PgColor.Control.SegmentTrack, null, "card.latency.chip") {
            CardLatency(server)
        }
    }
}

/** Чип (.sc-chip): высота 28, скругление 8, точка 7 цветом [dot] перед текстом. */
@Composable
private fun Chip(fill: Color, dot: Color?, tag: String, content: @Composable () -> Unit) {
    Row(
        Modifier
            .heightIn(min = 28.dp)
            .clip(RoundedCornerShape(8.dp))
            .background(fill)
            .padding(horizontal = PgSpace.M)
            .testTag(tag),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(PgSpace.Xs),
    ) {
        if (dot != null) {
            Box(Modifier.size(7.dp).clip(CircleShape).background(dot))
        }

        content()
    }
}

/**
 * Задержка в чипе (.sc-ping): быстрый — зеленым, медленный — серым, любой отказ — «нет ответа» оранжевым (почему — строка
 * «Проверка» ниже); не мерили — «Не проверен», как карточка десктопа (решение пользователя 2026-11-08: отсутствие замера —
 * не отказ сервера).
 */
@Composable
private fun CardLatency(server: ServerItem) {
    val kind = LatencyKind.of(server.latency)
    val ms = server.latency?.ms
    val (text, color) = when (kind) {
        LatencyKind.Good -> "$ms ${stringResource(R.string.unit_ms)}" to PgColor.Status.Success.Text
        LatencyKind.Slow -> "$ms ${stringResource(R.string.unit_ms)}" to PgColor.Text.Muted
        LatencyKind.NoAnswer -> stringResource(R.string.card_latency_none) to PgColor.Status.Warning.Text
        LatencyKind.Never -> stringResource(R.string.latency_unchecked) to PgColor.Text.Muted
    }
    val hint = if (kind == LatencyKind.Never) stringResource(R.string.android_servers_latency_never) else text
    Text(
        text,
        Modifier.semantics { contentDescription = hint }.testTag("card.latency"),
        color = color,
        style = PgType.Mono.copy(fontSize = PgType.Caption.fontSize, fontWeight = FontWeight.SemiBold),
        maxLines = 1,
    )
}

/** Вставка карточки (.sc-inset): surface.inset, рамка line.divider, скругление 16. */
@Composable
private fun Inset(modifier: Modifier, padding: PaddingValues, spacing: Dp = 0.dp, content: @Composable ColumnScope.() -> Unit) {
    val shape = RoundedCornerShape(16.dp)
    Column(
        modifier
            .fillMaxWidth()
            .clip(shape)
            .background(PgColor.Surface.Inset)
            .border(1.dp, PgColor.Line.Divider, shape)
            .padding(padding),
        verticalArrangement = Arrangement.spacedBy(spacing),
        content = content,
    )
}

/**
 * Сведения: адрес (из запечатанной ссылки — по открытии карточки), выходной IP — последний, который сайты видели через сервер
 * (у сервера «VPS + прокси» он не совпадает с адресом; решения 583 и пользователя 2026-11-08, как у десктопа), протокол,
 * транспорт, защита, ядро.
 */
@Composable
private fun Details(server: ServerItem, address: suspend (String) -> String?, exit: suspend (String) -> String?) {
    val host by produceState("…", server.id) { value = address(server.id) ?: "—" }
    val unknown = stringResource(R.string.card_exitIp_unknown)
    val seen by produceState("…", server.id, unknown) { value = exit(server.id) ?: unknown }
    Inset(Modifier.testTag("card.details"), PaddingValues(horizontal = 14.dp, vertical = 2.dp)) {
        Detail(stringResource(R.string.card_address), host, "card.address", divider = false)
        Detail(stringResource(R.string.card_exitIp), seen, "card.exitIp", lines = 2)
        Detail(stringResource(R.string.card_protocol), ServerFacts.protocolName(server.protocol), "card.protocol")
        Detail(stringResource(R.string.card_transport), ServerFacts.transportName(server.transport) ?: server.transport.name.uppercase(), "card.transport")
        val security = ServerFacts.securityName(server.security)
            ?: stringResource(if (server.encrypted) R.string.card_security_builtin else R.string.card_security_none)
        Detail(stringResource(R.string.card_security), security, "card.security")
        Detail(stringResource(R.string.card_core), ServerFacts.coreName(server.protocol) ?: stringResource(R.string.android_card_core_none), "card.core")
        latencyReason(server.latency?.failure)?.let { Detail(stringResource(R.string.card_latency_reason), stringResource(it), "card.latencyReason", lines = 2) }
    }
}

/** Почему замера задержки нет — строкой «Проверка», как `ServerLatencyText.Reason` десктопа; замер есть или не мерили — null. */
@StringRes
private fun latencyReason(failure: LatencyFailure?): Int? = when (failure) {
    LatencyFailure.Unsupported -> R.string.latency_unsupported
    LatencyFailure.CoreFailed -> R.string.latency_coreFailed
    LatencyFailure.NoResponse -> R.string.latency_noResponse
    null -> null
}

/** Значение сведений (.sc-val): моноширинным, по правому краю. */
private val DetailValue = PgType.Mono.copy(textAlign = TextAlign.End)

/** Строка сведений (.sc-detail): подпись слева серым, значение справа; строки разделены line.divider. */
@Composable
private fun Detail(label: String, value: String, tag: String, divider: Boolean = true, lines: Int = 1) {
    Row(
        Modifier.fillMaxWidth().pgTopDivider(divider).padding(vertical = PgSpace.S),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(PgSpace.L),
    ) {
        Text(label, color = PgColor.Text.Muted, style = PgType.Small)
        Text(value, Modifier.weight(1f).testTag(tag), style = DetailValue, maxLines = lines, overflow = TextOverflow.Ellipsis)
    }
}

/**
 * Переименование — на месте сведений: надпись «Название», поле, «Отмена» и «Сохранить». Свое название — 1–64 знака без
 * пробелов по краям; «Сохранить» гаснет, пока сохранять нечего.
 */
@Composable
private fun Rename(server: ServerItem, onCancel: () -> Unit, onSave: (String) -> Unit) {
    var name by rememberSaveable(server.id) { mutableStateOf(server.displayName) }
    val valid = ServerNames.validate(name)
    val label = stringResource(R.string.card_rename_label)
    Inset(Modifier.testTag("card.rename.block"), PaddingValues(14.dp), spacing = PgSpace.M) {
        Overline(label)
        NameInput(name, label) { name = it }
        Row(verticalAlignment = Alignment.CenterVertically) {
            PgSecondaryButton(stringResource(R.string.dialog_cancel), "card.rename.cancel", onCancel)
            Spacer(Modifier.weight(1f))
            PgPrimaryButton(stringResource(R.string.card_rename_save), "card.rename.save", { valid?.let(onSave) }, enabled = valid != null && valid != server.displayName)
        }
    }
}

/** Поле ввода (.input): белое, рамка line.strong, в фокусе — 1,5 accent.base. */
@Composable
private fun NameInput(name: String, label: String, onChange: (String) -> Unit) {
    val shape = RoundedCornerShape(PgRadius.Input)
    val interaction = remember { MutableInteractionSource() }
    val focused by interaction.collectIsFocusedAsState()
    Box(
        Modifier
            .fillMaxWidth()
            .heightIn(min = 44.dp)
            .clip(shape)
            .background(PgColor.Surface.Solid)
            .border(if (focused) 1.5.dp else 1.dp, if (focused) PgColor.Accent.Base else PgColor.Line.Strong, shape)
            .padding(horizontal = PgSpace.L),
        contentAlignment = Alignment.CenterStart,
    ) {
        BasicTextField(
            value = name,
            onValueChange = onChange,
            singleLine = true,
            textStyle = PgType.Body.copy(color = PgColor.Text.Primary),
            cursorBrush = SolidColor(PgColor.Accent.Base),
            interactionSource = interaction,
            modifier = Modifier.fillMaxWidth().semantics { contentDescription = label }.testTag("card.rename.text"),
            decorationBox = { field ->
                Box {
                    if (name.isEmpty()) {
                        Text(label, color = PgColor.Text.Muted, style = PgType.Body)
                    }

                    field()
                }
            },
        )
    }
}

/**
 * «Поделиться» (решение 436) — на месте сведений: QR-код 168 в белой рамке, предупреждение о ключе в плашке caution и
 * «Копировать ссылку» (в буфер — с пометкой «секрет»). Пока код на экране, Android 13+ не снимает окно для «Недавних».
 */
@Composable
private fun Share(link: String, onCopy: () -> Unit) {
    NoRecentsScreenshot()
    val bitmap = remember(link) { qrBitmap(QrCode.encode(link), scale = 6).asImageBitmap() }
    val frame = RoundedCornerShape(12.dp)
    Inset(Modifier.testTag("card.share.block"), PaddingValues(PgSpace.Card), spacing = PgSpace.L) {
        Box(
            Modifier
                .align(Alignment.CenterHorizontally)
                .clip(frame)
                .background(PgColor.Surface.Solid)
                .border(1.dp, PgColor.Line.Divider, frame)
                .padding(10.dp),
        ) {
            Image(
                bitmap,
                stringResource(R.string.card_share_qr),
                modifier = Modifier.size(168.dp).testTag("card.share.qr"),
                filterQuality = FilterQuality.None,
            )
        }

        Text(
            stringResource(R.string.card_share_warning),
            Modifier
                .fillMaxWidth()
                .clip(frame)
                .background(PgColor.Status.Caution.Tint)
                .border(1.dp, PgColor.Status.Caution.Line, frame)
                .padding(horizontal = PgSpace.L, vertical = PgSpace.M)
                .testTag("card.share.warning"),
            color = PgColor.Status.Caution.Text,
            style = PgType.Small,
        )
        PgSecondaryButton(stringResource(R.string.card_share_copy), "card.share.copy", onCopy, Modifier.fillMaxWidth(), height = 44.dp)
    }
}

/** Пока на экране ключ сервера, Android 13+ не снимает окно для «Недавних». */
@Composable
private fun NoRecentsScreenshot() {
    val activity = LocalActivity.current ?: return
    DisposableEffect(activity) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            activity.setRecentsScreenshotEnabled(false)
        }

        onDispose {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                activity.setRecentsScreenshotEnabled(true)
            }
        }
    }
}

/** «Используется сейчас» (.sc-inuse): полоса 50 на status.success.tint с рамкой success.line, значок Chip.Ok и текст. */
@Composable
private fun InUse() {
    val shape = RoundedCornerShape(14.dp)
    Row(
        Modifier
            .fillMaxWidth()
            .heightIn(min = 50.dp)
            .clip(shape)
            .background(PgColor.Status.Success.Tint)
            .border(1.5.dp, PgColor.Status.Success.Line, shape)
            .testTag("card.inUse"),
        horizontalArrangement = Arrangement.spacedBy(PgSpace.S, Alignment.CenterHorizontally),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        PgIcon(PgIcons.ChipOk, null, color = PgColor.Status.Success.Badge, size = 18.dp, strokeWidth = 2.4f)
        Text(stringResource(R.string.card_inUse), color = PgColor.Status.Success.Badge, style = TitleStrong)
    }
}

/** Текст полосы «Используется сейчас»: размер title, полужирным. */
private val TitleStrong = TextStyle(fontSize = PgType.Title.fontSize, fontWeight = FontWeight.SemiBold)

/**
 * Плитка действия (.sc-tile, Button.secondary.tile): 58 в высоту, значок 18 над подписью caption; при нажатии рамка и текст
 * синеют, сжатие 0,98. [danger] — красная («Удалить»), [on] — переключена («Скрыть QR»): accent.tint и рамка accent.base;
 * недоступная — прозрачность 0,45.
 */
@Composable
private fun Tile(
    icon: String,
    label: String,
    tag: String,
    modifier: Modifier,
    enabled: Boolean = true,
    danger: Boolean = false,
    on: Boolean = false,
    onClick: () -> Unit,
) {
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    val active = pressed && enabled
    val shape = RoundedCornerShape(14.dp)
    val edge = when {
        on -> PgColor.Accent.Base
        danger -> if (active) PgColor.Status.Danger.Base else PgColor.Status.Danger.Line
        active -> PgColor.Accent.Base
        else -> PgColor.Line.Strong
    }
    val ink = when {
        on -> PgColor.Accent.Text
        danger -> PgColor.Status.Danger.Text
        active -> PgColor.Accent.Base
        else -> PgColor.Text.Primary
    }
    Column(
        modifier
            .scale(if (active) 0.98f else 1f)
            .alpha(if (enabled) 1f else 0.45f)
            .heightIn(min = 58.dp)
            .clip(shape)
            .background(if (on) PgColor.Accent.Tint else PgColor.Surface.Solid)
            .border(1.dp, edge, shape)
            .clickable(interactionSource = interaction, indication = null, enabled = enabled, role = Role.Button, onClick = onClick)
            .padding(horizontal = PgSpace.Xxs, vertical = PgSpace.S)
            .testTag(tag),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(PgSpace.Xxs, Alignment.CenterVertically),
    ) {
        PgIcon(icon, null, color = ink, size = 18.dp, strokeWidth = 1.9f)
        Text(label, color = ink, style = PgType.Caption, maxLines = 1, overflow = TextOverflow.Ellipsis)
    }
}

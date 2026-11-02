package com.privategateway.client.ui.home

import android.os.SystemClock
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
import androidx.compose.foundation.layout.IntrinsicSize
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.scale
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.unit.dp
import com.privategateway.client.BuildConfig
import com.privategateway.client.R
import com.privategateway.client.core.inspect.PhoneNetworkKind
import com.privategateway.client.core.servers.QuickSwitch
import com.privategateway.client.core.servers.ServerFacts
import com.privategateway.client.core.servers.ServerItem
import com.privategateway.client.core.vpn.TunnelErrors
import com.privategateway.client.core.vpn.TunnelState
import com.privategateway.client.ui.ChipLook
import com.privategateway.client.ui.MainUi
import com.privategateway.client.ui.ProtectionChip
import com.privategateway.client.ui.ProtectionChips
import com.privategateway.client.ui.ProtectionView
import com.privategateway.client.ui.StateLook
import com.privategateway.client.ui.Texts
import com.privategateway.client.ui.maskIp
import com.privategateway.client.ui.servers.LatencyText
import com.privategateway.client.ui.theme.LocalPgToast
import com.privategateway.client.ui.theme.Overline
import com.privategateway.client.ui.theme.PgCard
import com.privategateway.client.ui.theme.PgColor
import com.privategateway.client.ui.theme.PgIcon
import com.privategateway.client.ui.theme.PgIcons
import com.privategateway.client.ui.theme.PgLayout
import com.privategateway.client.ui.theme.PgMenu
import com.privategateway.client.ui.theme.PgRadius
import com.privategateway.client.ui.theme.PgSelectButton
import com.privategateway.client.ui.theme.PgSpace
import com.privategateway.client.ui.theme.PgState
import com.privategateway.client.ui.theme.PgType
import com.privategateway.client.vpn.TrafficSpeed
import kotlinx.coroutines.delay
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.Locale

/** Что делают нажатия на главной, кроме переходов по разделам. */
class HomeActions(
    /** «Проверить» в «Защищено»: замерить внешний IP снова — проверка выхода (MainViewModel.verify). */
    val verify: () -> Unit = {},
    /** «Повторить» в «Ошибке» и «Блокировке»: подключить снова к выбранному серверу. */
    val retry: () -> Unit = {},
    /** «Открыть настройки VPN» в «Блокировке» Android: блок без VPN выключается там. */
    val vpnSettings: () -> Unit = {},
    /** Скопировать IP в буфер; «IP скопирован» главная показывает сама. */
    val copy: (String) -> Unit = {},
)

/**
 * Главная (доски 01, 09 и 25): шапка, карточка состояния с сетью, четырьмя плитками проверок и строкой «Проверено …»,
 * карточка «Сайты видят ваш IP» со строкой сервера и быстрой сменой, карточка «Пинг · Загрузка · Отдача». Показывается только
 * измеренное: внешний IP — замер приложения, пинг — последний замер задержки через ядро, скорость — счетчики байт туннеля;
 * нет замера — «—».
 */
@Composable
fun HomeScreen(
    ui: MainUi,
    onServers: () -> Unit,
    onCheck: () -> Unit,
    onAdd: () -> Unit,
    onSelect: (String) -> Unit = {},
    onRefresh: () -> Unit = {},
    speed: TrafficSpeed? = null,
    actions: HomeActions = HomeActions(),
) {
    Column(
        Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .statusBarsPadding()
            .padding(horizontal = PgSpace.WindowSideMobile)
            .padding(bottom = PgLayout.ScrollBottomPadding),
        verticalArrangement = Arrangement.spacedBy(PgSpace.M),
    ) {
        Header(onAdd)
        StatusCard(ui, actions)
        IpCard(ui, onServers, onAdd, onSelect, onRefresh, actions.copy)
        SpeedCard(ui, speed, onCheck)
    }
}

/** Бейдж (.badge): размер overline, полужирный, без разрядки и заглавных. */
private val BadgeText = PgType.Caption.copy(fontSize = PgType.Overline.fontSize, fontWeight = FontWeight.SemiBold)

/** Надпись-кнопка (.btn-link): small, полужирный. */
private val LinkText = PgType.Small.copy(fontWeight = FontWeight.SemiBold)

/** Число карточки скорости: t-title моноширинным. */
private val SpeedValue = PgType.Title.copy(fontFamily = PgType.Mono.fontFamily)

/** Ничего не измерено — прочерк, как у десктопа. */
private const val NONE = "—"

/** «Проверено только что» — первые 10 с после проверки, как у десктопа. */
private const val JUST_NOW_MS = 10_000L

/** Шапка (.pg-header, 60): знак — щит с отметкой, название, полная версия, «+». */
@Composable
private fun Header(onAdd: () -> Unit) {
    Row(Modifier.fillMaxWidth().height(60.dp), verticalAlignment = Alignment.CenterVertically) {
        Box(Modifier.testTag("logo")) {
            PgIcon(PgIcons.Shield, null, color = PgColor.Accent.Base, size = 28.dp)
            PgIcon(PgIcons.MarkOk, null, color = PgColor.Accent.Base, size = 28.dp)
        }

        Spacer(Modifier.width(PgSpace.M))
        Text(stringResource(R.string.app_title), style = PgType.Brand)
        Spacer(Modifier.width(PgSpace.S))
        Text(
            BuildConfig.VERSION_NAME,
            Modifier
                .clip(RoundedCornerShape(PgRadius.Chip))
                .background(PgColor.Accent.Tint)
                .padding(horizontal = PgSpace.Xs, vertical = 2.dp)
                .testTag("version"),
            color = PgColor.Accent.Text,
            style = BadgeText,
        )

        Spacer(Modifier.weight(1f))
        val add = stringResource(R.string.header_add)
        Box(
            Modifier
                .size(PgLayout.TouchTarget)
                .clip(CircleShape)
                .clickable(onClickLabel = add, role = Role.Button, onClick = onAdd)
                .testTag("add"),
            contentAlignment = Alignment.Center,
        ) {
            PgIcon(PgIcons.Plus, add, color = PgColor.Text.Primary, size = 22.dp, strokeWidth = 2f)
        }
    }
}

/** Карточка состояния: плашка-щит, заголовок, подпись, сеть, время в сети; плитки проверок; строка «Проверено …». */
@Composable
private fun StatusCard(ui: MainUi, actions: HomeActions) {
    val view = ui.view
    val look = StateLook.of(view)
    PgCard(Modifier.fillMaxWidth().testTag("status"), contentPadding = PaddingValues(start = PgSpace.Card, top = 14.dp, end = 14.dp, bottom = PgSpace.L)) {
        Column(verticalArrangement = Arrangement.spacedBy(PgSpace.L)) {
            Row {
                Box(
                    Modifier
                        .size(46.dp)
                        .clip(RoundedCornerShape(PgRadius.Button))
                        .background(look.tint),
                    contentAlignment = Alignment.Center,
                ) {
                    PgIcon(PgIcons.Shield, null, color = look.icon, size = 26.dp, strokeWidth = 1.9f)
                    PgIcon(look.mark, null, color = look.icon, size = 26.dp, strokeWidth = 1.9f)
                }

                Column(Modifier.weight(1f).padding(start = PgSpace.L, end = PgSpace.S), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                    Text(stringResource(look.title), color = look.icon, style = PgType.Status, modifier = Modifier.testTag("status.title"))
                    Text(
                        subtitle(ui, view),
                        color = PgColor.Text.Secondary,
                        style = PgType.Small,
                        maxLines = 2,
                        overflow = TextOverflow.Ellipsis,
                        modifier = Modifier.testTag("status.sub"),
                    )
                    NetworkLine(ui.network)
                }

                val since = ui.status.since
                if (ui.status.state == TunnelState.Connected && since != null) {
                    Column(horizontalAlignment = Alignment.End, verticalArrangement = Arrangement.spacedBy(1.dp)) {
                        Text(stringResource(R.string.status_online), color = PgColor.Text.Muted, style = PgType.Caption)
                        Text(online(since), style = PgType.Mono.copy(fontWeight = FontWeight.SemiBold))
                    }
                }
            }

            Chips(ProtectionChips.of(ui.status.state, ui.status.lockdown, ui.closedWithoutTunnel, ui.leak))
            CheckedLine(ui, view, actions)
        }
    }
}

/** Под заголовком: шифруется ли трафик, что с подключением, код ошибки — тексты status.* десктопа. */
@Composable
private fun subtitle(ui: MainUi, view: ProtectionView): String {
    // Кода нет — общий код службы, как у десктопа.
    val code = ui.status.error ?: "PGC-SVC"
    return when (view) {
        ProtectionView.Connected -> stringResource(if (ui.inUse?.encrypted == false) R.string.status_connected_subPlain else R.string.status_connected_sub)
        ProtectionView.Leak -> stringResource(R.string.status_leak_sub)
        ProtectionView.Connecting -> stringResource(R.string.status_connecting_sub)
        ProtectionView.Reconnecting -> stringResource(R.string.status_reconnecting_sub, ui.status.attempt.toString())
        // Под блоком Android: сервер не ответил — «Сервер не отвечает», иначе код и «трафик заблокирован».
        ProtectionView.Blocked ->
            if (code == TunnelErrors.SERVER_NOT_RESPONDING) stringResource(R.string.status_blocked_sub) else stringResource(R.string.status_error_sub, code)
        ProtectionView.Error -> stringResource(R.string.status_error_sub_direct, code)
        // «Блокировать подключения без VPN» — это Android, а не строгий режим приложения (решение 445).
        ProtectionView.Strict -> stringResource(R.string.android_status_off_closed)
        ProtectionView.Off -> stringResource(R.string.status_off_sub)
    }
}

/**
 * Сеть под состоянием (NetworkIcon/NetworkText десктопа): только вид — имя Wi-Fi Android отдает лишь с доступом к
 * местоположению, а его приложение не спрашивает. Сети, которую Android не называет (Bluetooth и т. п.), — строки нет.
 */
@Composable
private fun NetworkLine(kind: PhoneNetworkKind?) {
    val (icon, text) = when (kind) {
        null -> PgIcons.NetNone to R.string.network_none
        PhoneNetworkKind.Wifi -> PgIcons.NetWiFi to R.string.network_wifiNoName
        PhoneNetworkKind.Cellular -> PgIcons.NetMobile to R.string.network_mobile
        PhoneNetworkKind.Ethernet -> PgIcons.NetEthernet to R.string.network_ethernet
        PhoneNetworkKind.Vpn, PhoneNetworkKind.Other -> return
    }

    Row(
        Modifier.padding(top = 1.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(5.dp),
    ) {
        PgIcon(icon, null, color = PgColor.Text.Muted, size = 13.dp, strokeWidth = 2f)
        // Тег — на самом тексте: строка не сливает потомков, и у нее самой текста нет.
        Text(
            stringResource(text),
            Modifier.testTag("status.network"),
            color = PgColor.Text.Muted,
            style = PgType.Caption,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
    }
}

/** «02:14:09» — сколько защита в сети; тикает раз в секунду. */
@Composable
private fun online(since: Long): String {
    val now by produceState(SystemClock.elapsedRealtime(), since) {
        while (true) {
            value = SystemClock.elapsedRealtime()
            delay(1000)
        }
    }

    val seconds = ((now - since) / 1000).coerceAtLeast(0)
    return "%02d:%02d:%02d".format(Locale.ROOT, seconds / 3600, seconds / 60 % 60, seconds % 60)
}

/**
 * Плитки проверок (.chips): сетка 2 × 2. Плитка — кнопка: нажатие выделяет ее рамкой цвета тона и открывает под сеткой
 * пояснение (check.*.text), второе нажатие закрывает; выбрана другая — пояснение ее (CheckChipViewModel.Toggle десктопа).
 */
@Composable
private fun Chips(chips: List<ProtectionChip>) {
    var selected by rememberSaveable { mutableStateOf<String?>(null) }
    Column(verticalArrangement = Arrangement.spacedBy(PgSpace.Xs)) {
        for (row in chips.chunked(2)) {
            Row(horizontalArrangement = Arrangement.spacedBy(PgSpace.Xs)) {
                for (chip in row) {
                    Chip(chip, chip.id == selected, Modifier.weight(1f)) {
                        selected = if (selected == chip.id) null else chip.id
                    }
                }
            }
        }
    }

    val info = chips.firstOrNull { it.id == selected } ?: return
    Column(
        Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(PgRadius.Button))
            .background(PgColor.Accent.Tint)
            .padding(horizontal = PgSpace.L, vertical = PgSpace.M)
            .testTag("chip.info"),
        verticalArrangement = Arrangement.spacedBy(2.dp),
    ) {
        Text(stringResource(info.title), style = LinkText)
        Text(stringResource(info.text), color = PgColor.Text.Secondary, style = PgType.Caption)
    }
}

/**
 * Плитка (.btn-chip): высота 46, фон tint тона, кружок 22 со значком; заголовок small 600, значение размером overline цветом
 * текста тона. Выбранная — фон hover и рамка 1,5 цветом кружка; при нажатии сжимается до 0,98.
 */
@Composable
private fun Chip(chip: ProtectionChip, selected: Boolean, modifier: Modifier, onClick: () -> Unit) {
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    val shape = RoundedCornerShape(PgRadius.Button)
    val look: ChipLook = chip.look
    Row(
        modifier
            .scale(if (pressed) 0.98f else 1f)
            .height(46.dp)
            .clip(shape)
            .background(if (selected) look.hover else look.tint)
            .then(if (selected) Modifier.border(1.5.dp, look.base, shape) else Modifier)
            .selectable(selected = selected, interactionSource = interaction, indication = null, role = Role.Button, onClick = onClick)
            .padding(horizontal = PgSpace.M)
            .testTag("chip.${chip.id}"),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(Modifier.size(22.dp).clip(CircleShape).background(look.base), contentAlignment = Alignment.Center) {
            PgIcon(look.mark, null, color = PgColor.Surface.Solid, size = 14.dp, strokeWidth = 2.6f)
        }

        Spacer(Modifier.width(PgSpace.S))
        Column(Modifier.weight(1f)) {
            Text(stringResource(chip.title), style = LinkText, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Text(
                stringResource(chip.value),
                color = look.text,
                style = PgType.Caption.copy(fontSize = PgType.Overline.fontSize),
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
        }
    }
}

/**
 * Строка под плитками без разделителя (CheckedText и HasCheckAction десктопа): в «Защищено» — когда проверено и
 * «Проверить» (пока идет проверка — «Проверяем…», кнопки нет), в «Блокировке» и «Ошибке» — «Повторить», в «Блокировке»
 * Android — «Открыть настройки VPN»; в остальных — только текст. «Вход в Wi-Fi» нет: паузы у телефона нет.
 */
@Composable
private fun CheckedLine(ui: MainUi, view: ProtectionView, actions: HomeActions) {
    val checked = ui.ip.checkedAt?.takeIf { ui.ip.session == ui.status.since }
    val text = when (view) {
        ProtectionView.Off, ProtectionView.Strict -> stringResource(R.string.checked_off)
        ProtectionView.Connecting -> stringResource(R.string.checked_connecting)
        ProtectionView.Reconnecting -> stringResource(R.string.checked_reconnecting)
        ProtectionView.Connected, ProtectionView.Leak -> checkedText(checked, ui.ip.measuring)
        ProtectionView.Blocked -> stringResource(R.string.checked_blocked)
        ProtectionView.Error -> stringResource(R.string.checked_error)
    }

    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
        Text(text, color = PgColor.Text.Muted, style = PgType.Caption, modifier = Modifier.weight(1f).testTag("status.checked"))
        when (view) {
            ProtectionView.Connected, ProtectionView.Leak -> if (!ui.ip.measuring) {
                LinkAction(stringResource(R.string.action_check), PgState.Connecting.Icon, "status.verify", actions.verify)
            }

            ProtectionView.Blocked -> LinkAction(stringResource(R.string.action_retry), PgState.Blocked.Icon, "status.retry", actions.retry)
            ProtectionView.Error -> LinkAction(stringResource(R.string.action_retry), PgState.Error.Icon, "status.retry", actions.retry)
            ProtectionView.Strict -> LinkAction(stringResource(R.string.android_settings_vpn_open), PgState.Blocked.Icon, "status.vpnSettings", actions.vpnSettings)
            else -> Unit
        }
    }
}

/** «Проверено только что» первые 10 с, потом «Проверено в 14:05»; пока идет замер — «Проверяем…». */
@Composable
private fun checkedText(at: Long?, measuring: Boolean): String {
    val now by produceState(System.currentTimeMillis(), at) {
        while (at != null && System.currentTimeMillis() - at < JUST_NOW_MS) {
            delay(1000)
            value = System.currentTimeMillis()
        }

        // Последнее значение — уже после 10 с: иначе «только что» могло бы остаться навсегда.
        value = System.currentTimeMillis()
    }

    return when {
        measuring -> stringResource(R.string.checked_running)
        at == null -> stringResource(R.string.checked_connecting)
        now - at < JUST_NOW_MS -> stringResource(R.string.checked_now)
        else -> stringResource(R.string.checked_at, DateTimeFormatter.ofPattern("HH:mm", Locale.ROOT).format(Instant.ofEpochMilli(at).atZone(ZoneId.systemDefault())))
    }
}

/** Надпись-кнопка (.btn-link) цветом тона: при нажатии сжимается до 0,98, без заливки. */
@Composable
private fun LinkAction(text: String, color: Color, tag: String, onClick: () -> Unit) {
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    Text(
        text,
        Modifier
            .scale(if (pressed) 0.98f else 1f)
            .clip(RoundedCornerShape(PgRadius.Input))
            .clickable(interactionSource = interaction, indication = null, role = Role.Button, onClick = onClick)
            .padding(PgSpace.Xs)
            .testTag(tag),
        color = color,
        style = LinkText,
    )
}

/**
 * «Сайты видят ваш IP» (доска 01): адрес моно-крупным, «Копировать IP» с подсказкой «IP скопирован», глаз — скрыть адреса
 * звездочками; что это за адрес по состоянию и реальный IP; под полосой — строка сервера.
 */
@Composable
private fun IpCard(ui: MainUi, onServers: () -> Unit, onAdd: () -> Unit, onSelect: (String) -> Unit, onRefresh: () -> Unit, copy: (String) -> Unit) {
    var masked by rememberSaveable { mutableStateOf(false) }
    val toast = LocalPgToast.current
    val view = ui.view
    val address = ui.webAddress
    PgCard(Modifier.fillMaxWidth().testTag("ip"), contentPadding = PaddingValues(0.dp)) {
        Column(Modifier.padding(start = PgSpace.Card, top = 14.dp, end = PgSpace.S, bottom = PgSpace.L), verticalArrangement = Arrangement.spacedBy(3.dp)) {
            Overline(stringResource(R.string.ip_title))
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(
                    address?.let { if (masked) maskIp(it) else it } ?: NONE,
                    Modifier.weight(1f).testTag("ip.address"),
                    style = PgType.MonoLarge,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
                // Скрытый адрес не копируется (CanCopyIp десктопа): в буфер ушли бы звездочки или то, что пользователь спрятал.
                val copied = stringResource(R.string.ip_copied)
                IconAction(PgIcons.Copy, stringResource(R.string.ip_copy), "ip.copy", enabled = address != null && !masked) {
                    if (address != null) {
                        copy(address)
                        toast.show(copied)
                    }
                }
                IconAction(if (masked) PgIcons.EyeOff else PgIcons.Eye, stringResource(if (masked) R.string.ip_show else R.string.ip_hide), "ip.mask") {
                    masked = !masked
                }
            }

            val meta = when (view) {
                ProtectionView.Off, ProtectionView.Error -> R.string.ip_meta_off
                ProtectionView.Strict, ProtectionView.Blocked -> R.string.ip_meta_stopped
                ProtectionView.Connecting, ProtectionView.Reconnecting -> R.string.ip_meta_waiting
                // Город, тип сети и AS выхода знает только RIPEstat, а его приложение спрашивает лишь по кнопке на «Проверке».
                ProtectionView.Connected, ProtectionView.Leak -> null
            }
            if (meta != null) {
                Text(stringResource(meta), color = PgColor.Text.Secondary, style = PgType.Small, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.testTag("ip.meta"))
            }

            val direct = ui.ip.direct
            if (direct != null) {
                val visible = view == ProtectionView.Off || view == ProtectionView.Error || view == ProtectionView.Leak
                val state = stringResource(if (visible) R.string.ip_real_visible else R.string.ip_real_hidden)
                val label = stringResource(R.string.ip_real)
                Text(
                    buildAnnotatedString {
                        append(label)
                        append(' ')
                        withStyle(SpanStyle(fontFamily = PgType.Mono.fontFamily)) { append(if (masked) maskIp(direct) else direct) }
                        append(" · ")
                        append(state)
                    },
                    Modifier.testTag("ip.real"),
                    color = PgColor.Text.Secondary,
                    style = PgType.Small,
                )
            }
        }

        HorizontalDivider(Modifier.padding(horizontal = PgSpace.Card), color = PgColor.Line.Divider)
        ServerRow(ui, onServers, onAdd, onSelect, onRefresh)
    }
}

/** Кнопка-значок (.btn-icon, 44): недоступная — прозрачность 0,35. */
@Composable
private fun IconAction(icon: String, label: String, tag: String, enabled: Boolean = true, onClick: () -> Unit) {
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    Box(
        Modifier
            .size(PgLayout.TouchTarget)
            .alpha(if (enabled) 1f else 0.35f)
            .scale(if (pressed && enabled) 0.98f else 1f)
            .clip(RoundedCornerShape(PgRadius.Button))
            .clickable(interactionSource = interaction, indication = null, enabled = enabled, onClickLabel = label, role = Role.Button, onClick = onClick)
            .semantics { contentDescription = label }
            .testTag(tag),
        contentAlignment = Alignment.Center,
    ) {
        PgIcon(icon, null, color = if (pressed && enabled) PgColor.Accent.Base else PgColor.Text.Primary, size = 18.dp, strokeWidth = 2f)
    }
}

/**
 * Строка сервера внутри карточки IP (доска 01): слева «Обновить серверы» — синяя плашка, пока идет замер, крутится только
 * стрелка, кнопка не гаснет; посередине «NL · Нидерланды 01» и протокол с портом; справа «Сменить ▾» — меню быстрой смены.
 * Сервера нет — что сделать: добавить первый или выбрать в списке.
 */
@Composable
private fun ServerRow(ui: MainUi, onServers: () -> Unit, onAdd: () -> Unit, onSelect: (String) -> Unit, onRefresh: () -> Unit) {
    val server = ui.inUse
    Row(
        Modifier.fillMaxWidth().padding(start = PgSpace.M, top = PgSpace.S, end = PgSpace.M, bottom = PgSpace.M),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        RefreshButton(ui.measuring, onRefresh)
        val title = when {
            server == null -> stringResource(R.string.server_none)
            server.countryCode.isEmpty() -> server.displayName
            else -> server.countryCode + " · " + server.displayName
        }
        val meta = when {
            server != null -> server.summary + " · " + stringResource(R.string.server_port, server.port.toString())
            ui.servers.isEmpty() -> stringResource(R.string.server_addFirst)
            else -> stringResource(R.string.android_server_choose)
        }
        // Сервера нет: серверов нет вовсе — нажатие ведет к добавлению; есть, но ни один не выбран (выбранный удален) — к списку.
        val action = when {
            server != null -> Modifier
            ui.servers.isEmpty() -> Modifier.clickable(role = Role.Button, onClick = onAdd).testTag("server.add")
            else -> Modifier.clickable(role = Role.Button, onClick = onServers).testTag("server.choose")
        }
        Column(Modifier.weight(1f).then(action).padding(start = PgSpace.L, end = PgSpace.S), verticalArrangement = Arrangement.spacedBy(2.dp)) {
            Text(title, style = PgType.Title, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.testTag("server.title"))
            Text(meta, color = PgColor.Text.Secondary, style = PgType.Small, maxLines = 1, overflow = TextOverflow.Ellipsis)
        }

        if (ui.servers.isNotEmpty()) {
            QuickSwitchButton(ui, server, onServers, onSelect)
        }
    }
}

/** «Обновить серверы» (.btn-icon.steady.tinted): accent.tint, значок accent.text; во время замера крутится стрелка, 0,9 с. */
@Composable
private fun RefreshButton(spinning: Boolean, onClick: () -> Unit) {
    val label = stringResource(R.string.server_refresh)
    Box(
        Modifier
            .size(PgLayout.TouchTarget)
            .clip(RoundedCornerShape(PgRadius.Button))
            .background(PgColor.Accent.Tint)
            .clickable(interactionSource = remember { MutableInteractionSource() }, indication = null, onClickLabel = label, role = Role.Button, onClick = onClick)
            .semantics { contentDescription = label }
            .testTag("server.refresh"),
        contentAlignment = Alignment.Center,
    ) {
        if (spinning) {
            val spin = rememberInfiniteTransition(label = "refresh")
            val angle by spin.animateFloat(0f, 360f, infiniteRepeatable(tween(900, easing = LinearEasing)), label = "angle")
            PgIcon(PgIcons.Refresh, null, Modifier.graphicsLayer { rotationZ = angle }.testTag("server.refresh.spin"), color = PgColor.Accent.Text, size = 20.dp, strokeWidth = 2f)
        } else {
            PgIcon(PgIcons.Refresh, null, color = PgColor.Accent.Text, size = 20.dp, strokeWidth = 2f)
        }
    }
}

/**
 * «Сменить ▾» (.btn-select) и меню у кнопки (.popup, QuickServers десктопа): «Быстрая смена · вручную», сервер в работе и два
 * самых быстрых измеренных, «Все серверы ›». Hysteria2 и TUIC телефон не подключает — их среди предложенных нет. Выбор
 * закрывает меню; под защитой — сразу переподключение (MainViewModel.select).
 */
@Composable
private fun QuickSwitchButton(ui: MainUi, server: ServerItem?, onServers: () -> Unit, onSelect: (String) -> Unit) {
    var open by remember { mutableStateOf(false) }
    Box {
        PgSelectButton(stringResource(R.string.server_switch), "server.switch", { open = !open }, open = open)
        PgMenu(open, { open = false }, "server.switch.menu") {
            Overline(stringResource(R.string.server_quick), Modifier.padding(start = PgSpace.M, top = PgSpace.Xs, end = PgSpace.M, bottom = PgSpace.Xxs))
            val offered = ui.servers.filter { it.id == server?.id || ServerFacts.coreName(it.protocol) != null }
            for (item in QuickSwitch.pick(offered, server)) {
                QuickRow(item, current = item.id == server?.id) {
                    open = false
                    onSelect(item.id)
                }
            }

            HorizontalDivider(Modifier.padding(horizontal = PgSpace.Xs, vertical = 2.dp), color = PgColor.Line.Divider)
            Text(
                stringResource(R.string.server_all),
                Modifier
                    .fillMaxWidth()
                    .clip(RoundedCornerShape(PgRadius.Input))
                    .clickable(role = Role.Button) {
                        open = false
                        onServers()
                    }
                    .padding(horizontal = PgSpace.M, vertical = PgSpace.S)
                    .testTag("quick.all"),
                color = PgColor.Accent.Text,
                style = LinkText,
            )
        }
    }
}

/**
 * Строка быстрой смены (.btn-option): код страны в бейдже 30 × 22, название и протокол второй строкой, задержка. Сервер в
 * работе — синим полужирным (.current); при нажатии — синий текст и сжатие 0,98.
 */
@Composable
private fun QuickRow(server: ServerItem, current: Boolean, onClick: () -> Unit) {
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    val color = when {
        current -> PgColor.Accent.Text
        pressed -> PgColor.Accent.Base
        else -> PgColor.Text.Primary
    }
    Row(
        Modifier
            .fillMaxWidth()
            .scale(if (pressed) 0.98f else 1f)
            .heightIn(min = PgLayout.TouchTarget)
            .clip(RoundedCornerShape(PgRadius.Input))
            .selectable(selected = current, interactionSource = interaction, indication = null, onClick = onClick)
            .padding(horizontal = PgSpace.M)
            .testTag("quick.${server.id}"),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(
            Modifier
                .size(30.dp, 22.dp)
                .clip(RoundedCornerShape(PgRadius.Chip))
                .background(PgColor.Accent.Tint),
            contentAlignment = Alignment.Center,
        ) {
            Text(server.countryCode, color = PgColor.Accent.Text, style = BadgeText.copy(fontWeight = FontWeight.Bold), maxLines = 1)
        }

        Column(Modifier.weight(1f).padding(horizontal = PgSpace.M, vertical = PgSpace.Xs), verticalArrangement = Arrangement.spacedBy(1.dp)) {
            Text(server.displayName, color = color, style = PgType.BodyStrong, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Text(server.summary, color = PgColor.Text.Muted, style = PgType.Caption, maxLines = 1, overflow = TextOverflow.Ellipsis)
        }

        LatencyText(server)
    }
}

/**
 * «Пинг · ↓ Загрузка · ↑ Отдача» (доска 01) — только в «Защищено» (и «Утечке IP»: туннель работает), иначе «—». Пинг —
 * последний замер задержки сервера в работе через ядро (решение 424), нажатие ведет на «Проверку»; скорость — счетчики байт
 * туннеля за секунду.
 */
@Composable
private fun SpeedCard(ui: MainUi, speed: TrafficSpeed?, onCheck: () -> Unit) {
    val working = ui.view == ProtectionView.Connected || ui.view == ProtectionView.Leak
    val ping = ui.inUse?.latency?.ms?.takeIf { working }
    val current = speed?.takeIf { working }
    val locale = Locale.forLanguageTag(Texts.language(LocalConfiguration.current.locales))
    fun mbps(value: Double) = String.format(locale, "%.1f", value)
    PgCard(Modifier.fillMaxWidth().testTag("speed"), contentPadding = PaddingValues(horizontal = PgSpace.Xs, vertical = PgSpace.M)) {
        Row(Modifier.fillMaxWidth().height(IntrinsicSize.Min), verticalAlignment = Alignment.CenterVertically) {
            val open = stringResource(R.string.speed_ping_open)
            SpeedColumn(
                stringResource(R.string.speed_ping),
                ping?.toString() ?: NONE,
                stringResource(R.string.unit_ms),
                PgColor.Status.Success.Text,
                "speed.ping",
                Modifier
                    .weight(1f)
                    .clip(RoundedCornerShape(PgRadius.Button))
                    .clickable(onClickLabel = open, role = Role.Button, onClick = onCheck)
                    .semantics { contentDescription = open }
                    .testTag("speed.ping.open"),
            )
            SpeedDivider()
            SpeedColumn(stringResource(R.string.speed_down), current?.let { mbps(it.downMbps) } ?: NONE, stringResource(R.string.unit_mbps), PgColor.Text.Primary, "speed.down", Modifier.weight(1f))
            SpeedDivider()
            SpeedColumn(stringResource(R.string.speed_up), current?.let { mbps(it.upMbps) } ?: NONE, stringResource(R.string.unit_mbps), PgColor.Text.Primary, "speed.up", Modifier.weight(1f))
        }
    }
}

@Composable
private fun SpeedColumn(title: String, value: String, unit: String, color: Color, tag: String, modifier: Modifier) {
    Column(modifier, horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(2.dp)) {
        Text(title, color = PgColor.Text.Muted, style = PgType.Caption, textAlign = TextAlign.Center)
        Text(value, color = color, style = SpeedValue, textAlign = TextAlign.Center, modifier = Modifier.testTag(tag))
        Text(unit, color = PgColor.Text.Muted, style = PgType.Caption, textAlign = TextAlign.Center)
    }
}

@Composable
private fun SpeedDivider() {
    Box(Modifier.width(1.dp).fillMaxHeight().background(PgColor.Line.Divider))
}

/** Значок страны: две буквы кода на подложке (цветные флаги шрифт рисует не везде); кода нет — щит. */
@Composable
fun CountryBadge(code: String) {
    Box(
        Modifier
            .size(PgLayout.TouchTarget - 4.dp)
            .clip(RoundedCornerShape(PgRadius.Input))
            .background(PgColor.Accent.Tint),
        contentAlignment = Alignment.Center,
    ) {
        if (code.isEmpty()) {
            PgIcon(PgIcons.Shield, null, color = PgColor.Accent.Text, size = 18.dp)
        } else {
            Text(code, color = PgColor.Accent.Text, style = PgType.BodyStrong)
        }
    }
}

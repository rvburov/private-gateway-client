package com.privategateway.client.ui.home

import android.os.SystemClock
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
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
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.privategateway.client.BuildConfig
import com.privategateway.client.R
import com.privategateway.client.core.servers.QuickSwitch
import com.privategateway.client.core.servers.ServerItem
import com.privategateway.client.core.vpn.TunnelState
import com.privategateway.client.ui.MainUi
import com.privategateway.client.ui.ProtectionChip
import com.privategateway.client.ui.ProtectionChips
import com.privategateway.client.ui.StateLook
import com.privategateway.client.ui.Texts
import com.privategateway.client.ui.servers.LatencyText
import com.privategateway.client.ui.theme.Overline
import com.privategateway.client.ui.theme.PgCard
import com.privategateway.client.ui.theme.PgColor
import com.privategateway.client.ui.theme.PgIcon
import com.privategateway.client.ui.theme.PgIcons
import com.privategateway.client.ui.theme.PgLayout
import com.privategateway.client.ui.theme.PgRadius
import com.privategateway.client.ui.theme.PgSpace
import com.privategateway.client.ui.theme.PgType
import kotlinx.coroutines.delay
import java.util.Locale

/**
 * Главная (доска 25): шапка, карточка состояния с четырьмя чипами защиты, сервер в работе. Внешний IP и скорость — после
 * замера (этапы 9.2 и 9.5): показывается только измеренное, без предполагаемых значений.
 */
@Composable
fun HomeScreen(
    ui: MainUi,
    onServers: () -> Unit,
    onCheck: () -> Unit,
    onAdd: () -> Unit,
    onSelect: (String) -> Unit = {},
    onRefresh: () -> Unit = {},
) {
    Column(
        Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .statusBarsPadding()
            .padding(horizontal = PgSpace.WindowSideMobile)
            .padding(bottom = PgLayout.ScrollBottomPadding),
        verticalArrangement = Arrangement.spacedBy(PgSpace.L),
    ) {
        Header(onAdd)
        StatusCard(ui, onCheck)
        ServerCard(ui, onServers, onAdd, onSelect, onRefresh)
    }
}

@Composable
private fun Header(onAdd: () -> Unit) {
    Row(Modifier.fillMaxWidth().padding(top = PgSpace.L), verticalAlignment = Alignment.CenterVertically) {
        PgIcon(PgIcons.Shield, null, color = PgColor.Accent.Base, size = 26.dp)
        Spacer(Modifier.width(PgSpace.S))
        Text(stringResource(R.string.app_title), style = PgType.Brand)
        Spacer(Modifier.width(PgSpace.S))
        Box(
            Modifier
                .clip(RoundedCornerShape(PgRadius.Chip))
                .background(PgColor.Accent.Tint)
                .padding(horizontal = PgSpace.Xs, vertical = 2.dp),
        ) {
            Text(BuildConfig.VERSION_NAME.substringBeforeLast('.'), color = PgColor.Accent.Text, style = PgType.Caption)
        }

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
            PgIcon(PgIcons.Plus, add, color = PgColor.Text.Primary, size = 24.dp, strokeWidth = 2f)
        }
    }
}

@Composable
private fun StatusCard(ui: MainUi, onCheck: () -> Unit) {
    val state = ui.status.state
    val look = StateLook.of(state)
    val server = ui.inUse
    PgCard(Modifier.fillMaxWidth().testTag("status")) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Box(
                Modifier
                    .size(48.dp)
                    .clip(RoundedCornerShape(PgRadius.Button))
                    .background(look.tint),
                contentAlignment = Alignment.Center,
            ) {
                PgIcon(PgIcons.Shield, null, color = look.icon, size = 26.dp, strokeWidth = 2f)
                PgIcon(look.mark, null, color = look.icon, size = 26.dp, strokeWidth = 2.2f)
            }

            Spacer(Modifier.width(PgSpace.L))
            Column(Modifier.weight(1f)) {
                Text(stringResource(look.title), color = look.icon, style = PgType.Status, modifier = Modifier.testTag("status.title"))
                Text(subtitle(ui), color = PgColor.Text.Secondary, style = PgType.Small, maxLines = 2, overflow = TextOverflow.Ellipsis)
            }

            val since = ui.status.since
            if (state == TunnelState.Connected && since != null) {
                Column(horizontalAlignment = Alignment.End) {
                    Text(stringResource(R.string.status_online), color = PgColor.Text.Muted, style = PgType.Caption)
                    Text(online(since), style = PgType.Mono)
                }
            }
        }

        Spacer(Modifier.height(PgSpace.L))
        val chips = ProtectionChips.of(state, ui.status.lockdown, ui.closedWithoutTunnel)
        for (row in chips.chunked(2)) {
            Row(horizontalArrangement = Arrangement.spacedBy(PgSpace.S)) {
                for (chip in row) {
                    Chip(chip, Modifier.weight(1f))
                }
            }

            Spacer(Modifier.height(PgSpace.S))
        }

        HorizontalDivider(color = PgColor.Line.Divider)
        Row(Modifier.fillMaxWidth().padding(top = PgSpace.M), verticalAlignment = Alignment.CenterVertically) {
            val note = when (state) {
                TunnelState.Off -> when {
                    server != null -> R.string.checked_off
                    ui.servers.isEmpty() -> R.string.server_addFirst
                    else -> R.string.android_server_choose
                }

                TunnelState.Connecting, TunnelState.Stopping -> R.string.checked_connecting
                TunnelState.Reconnecting -> R.string.checked_reconnecting
                TunnelState.Error -> Texts.error(ui.status.error)
                TunnelState.Connected -> null
            }

            Text(note?.let { stringResource(it) } ?: "", color = PgColor.Text.Muted, style = PgType.Caption, modifier = Modifier.weight(1f))
            Text(
                stringResource(R.string.action_check),
                color = PgColor.Accent.Text,
                style = PgType.BodyStrong,
                modifier = Modifier.clickable(role = Role.Button, onClick = onCheck).padding(PgSpace.Xs),
            )
        }
    }
}

/** Под заголовком: шифруется ли трафик, к какому серверу подключаемся, код ошибки. */
@Composable
private fun subtitle(ui: MainUi): String {
    val server = ui.inUse
    return when (ui.status.state) {
        TunnelState.Connected -> stringResource(if (server?.encrypted == false) R.string.status_connected_subPlain else R.string.status_connected_sub)
        TunnelState.Connecting, TunnelState.Stopping -> server?.displayName ?: ""
        TunnelState.Reconnecting -> stringResource(R.string.status_reconnecting_sub, ui.status.attempt.toString())
        // Android закрыл интернет без туннеля — трафик не идет напрямую (решение 445).
        TunnelState.Error -> stringResource(if (ui.closedWithoutTunnel) R.string.status_error_sub else R.string.status_error_sub_direct, ui.status.error ?: "")
        TunnelState.Off -> stringResource(if (ui.closedWithoutTunnel) R.string.android_status_off_closed else R.string.status_off_sub)
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

@Composable
private fun Chip(chip: ProtectionChip, modifier: Modifier) {
    Row(
        modifier
            .clip(RoundedCornerShape(PgRadius.Button))
            .background(chip.look.tint)
            .padding(PgSpace.M),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(Modifier.size(20.dp).clip(CircleShape).background(chip.look.base), contentAlignment = Alignment.Center) {
            PgIcon(chip.look.mark, null, color = PgColor.Surface.Solid, size = 14.dp, strokeWidth = 3f)
        }

        Spacer(Modifier.width(PgSpace.S))
        Column {
            Text(stringResource(chip.title), style = PgType.BodyStrong, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Text(stringResource(chip.value), color = PgColor.Text.Secondary, style = PgType.Caption, maxLines = 1, overflow = TextOverflow.Ellipsis)
        }
    }
}

/**
 * Сервер в работе (или выбранный): флаг страны, название, протокол и порт. «Сменить ▾» раскрывает быструю смену (доска
 * 25, решение 431): сервер в работе и два самых быстрых измеренных, «Обновить» — замер задержки, «Все серверы ›».
 */
@Composable
private fun ServerCard(ui: MainUi, onServers: () -> Unit, onAdd: () -> Unit, onSelect: (String) -> Unit, onRefresh: () -> Unit) {
    val server = ui.inUse
    var quick by rememberSaveable { mutableStateOf(false) }
    PgCard(Modifier.fillMaxWidth()) {
        if (server == null && ui.servers.isEmpty()) {
            Overline(stringResource(R.string.server_none))
            Spacer(Modifier.height(PgSpace.S))
            Row(Modifier.fillMaxWidth().clickable(role = Role.Button, onClick = onAdd), verticalAlignment = Alignment.CenterVertically) {
                Text(stringResource(R.string.servers_none), color = PgColor.Text.Secondary, style = PgType.Small, modifier = Modifier.weight(1f))
                PgIcon(PgIcons.Plus, stringResource(R.string.header_add), color = PgColor.Accent.Base)
            }

        } else if (server == null) {
            // Серверы есть, но ни один не выбран (выбранный удален): сам сервер не выбирается — ведем к списку.
            Overline(stringResource(R.string.server_none))
            Spacer(Modifier.height(PgSpace.S))
            Row(
                Modifier.fillMaxWidth().clickable(role = Role.Button, onClick = onServers).testTag("server.choose"),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(stringResource(R.string.android_server_choose), color = PgColor.Text.Secondary, style = PgType.Small, modifier = Modifier.weight(1f))
                Text(stringResource(R.string.server_all), color = PgColor.Accent.Text, style = PgType.BodyStrong)
            }

        } else {
            Row(
                Modifier
                    .fillMaxWidth()
                    .clickable(role = Role.Button, onClickLabel = stringResource(R.string.server_quick)) { quick = !quick }
                    .testTag("server.switch"),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                CountryBadge(server.countryCode)
                Spacer(Modifier.width(PgSpace.L))
                Column(Modifier.weight(1f)) {
                    Text(server.displayName, style = PgType.BodyStrong, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    Text(server.summary + " · " + stringResource(R.string.server_port, server.port.toString()), color = PgColor.Text.Secondary, style = PgType.Caption)
                }

                Text(stringResource(R.string.server_switch), color = PgColor.Accent.Text, style = PgType.BodyStrong)
                PgIcon(PgIcons.ChevronDown, null, color = PgColor.Accent.Text, size = 16.dp)
            }

            if (quick) {
                QuickSwitchList(ui, server, onRefresh, onServers) { id ->
                    quick = false
                    onSelect(id)
                }
            }
        }
    }
}

/** Быстрая смена: сервер в работе и самые быстрые измеренные; нажатие — выбор (под защитой — сразу переподключение). */
@Composable
private fun QuickSwitchList(ui: MainUi, inUse: ServerItem, onRefresh: () -> Unit, onServers: () -> Unit, onPick: (String) -> Unit) {
    Spacer(Modifier.height(PgSpace.M))
    HorizontalDivider(color = PgColor.Line.Divider)
    Row(Modifier.fillMaxWidth().padding(top = PgSpace.S), verticalAlignment = Alignment.CenterVertically) {
        Overline(stringResource(R.string.server_quick), Modifier.weight(1f))
        val refresh = stringResource(R.string.server_refresh)
        Box(
            Modifier
                .size(PgLayout.TouchTarget)
                .clip(CircleShape)
                .clickable(enabled = !ui.measuring, onClickLabel = refresh, role = Role.Button, onClick = onRefresh)
                .testTag("quick.refresh"),
            contentAlignment = Alignment.Center,
        ) {
            PgIcon(PgIcons.Refresh, refresh, color = if (ui.measuring) PgColor.Text.Muted else PgColor.Accent.Text, size = 20.dp)
        }
    }

    for (server in QuickSwitch.pick(ui.servers, inUse)) {
        Row(
            Modifier
                .fillMaxWidth()
                .clip(RoundedCornerShape(PgRadius.Button))
                .clickable(role = Role.Button) { onPick(server.id) }
                .padding(vertical = PgSpace.S)
                .testTag("quick.${server.id}"),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            CountryBadge(server.countryCode)
            Spacer(Modifier.width(PgSpace.M))
            Text(server.displayName, style = PgType.Body, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f))
            LatencyText(server)
        }
    }

    Text(
        stringResource(R.string.server_all),
        color = PgColor.Accent.Text,
        style = PgType.BodyStrong,
        modifier = Modifier
            .clip(RoundedCornerShape(PgRadius.Button))
            .clickable(role = Role.Button, onClick = onServers)
            .padding(vertical = PgSpace.S)
            .testTag("quick.all"),
    )
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

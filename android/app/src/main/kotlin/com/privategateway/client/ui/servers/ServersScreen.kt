package com.privategateway.client.ui.servers

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.selected
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.privategateway.client.R
import com.privategateway.client.data.ServerItem
import com.privategateway.client.ui.MainUi
import com.privategateway.client.ui.home.CountryBadge
import com.privategateway.client.ui.theme.PgCard
import com.privategateway.client.ui.theme.PgColor
import com.privategateway.client.ui.theme.PgIcon
import com.privategateway.client.ui.theme.PgIcons
import com.privategateway.client.ui.theme.PgLayout
import com.privategateway.client.ui.theme.PgRadius
import com.privategateway.client.ui.theme.PgSpace
import com.privategateway.client.ui.theme.PgType
import com.privategateway.client.vpn.TunnelState

/**
 * «Серверы»: список, выбор нажатием, удаление с подтверждением. Сервер меняется только нажатием пользователя; подписки,
 * QR и поиск — пункт 9.2.
 */
@Composable
fun ServersScreen(ui: MainUi, onSelect: (String) -> Unit, onDelete: (String) -> Unit, onAdd: () -> Unit) {
    var deleting by rememberSaveable { mutableStateOf<String?>(null) }
    LazyColumn(
        Modifier.fillMaxSize().statusBarsPadding().testTag("servers"),
        contentPadding = PaddingValues(start = PgSpace.WindowSideMobile, end = PgSpace.WindowSideMobile, bottom = PgLayout.ScrollBottomPadding),
        verticalArrangement = Arrangement.spacedBy(PgSpace.S),
    ) {
        item {
            Row(Modifier.fillMaxWidth().padding(top = PgSpace.L, bottom = PgSpace.S), verticalAlignment = Alignment.CenterVertically) {
                Text(stringResource(R.string.nav_servers), style = PgType.H1, modifier = Modifier.weight(1f))
                val add = stringResource(R.string.header_add)
                Box(
                    Modifier.size(PgLayout.TouchTarget).clip(CircleShape).clickable(onClickLabel = add, role = Role.Button, onClick = onAdd),
                    contentAlignment = Alignment.Center,
                ) {
                    PgIcon(PgIcons.Plus, add, color = PgColor.Text.Primary, size = 24.dp, strokeWidth = 2f)
                }
            }
        }

        if (ui.servers.isEmpty()) {
            item {
                PgCard(Modifier.fillMaxWidth()) {
                    Text(stringResource(R.string.servers_none), color = PgColor.Text.Secondary, style = PgType.Body)
                }
            }
        }

        items(ui.servers, key = { it.id }) { server ->
            val active = ui.status.state != TunnelState.Off && ui.status.serverId == server.id
            ServerRow(server, selected = server.id == ui.selectedId, active = active, onSelect = { onSelect(server.id) }, onDelete = { deleting = server.id })
        }

        item {
            Text(stringResource(R.string.servers_note), color = PgColor.Text.Muted, style = PgType.Caption, modifier = Modifier.padding(top = PgSpace.S))
        }
    }

    val target = ui.servers.firstOrNull { it.id == deleting }
    if (target != null) {
        val inUse = ui.status.state != TunnelState.Off && ui.status.serverId == target.id
        AlertDialog(
            onDismissRequest = { deleting = null },
            title = { Text(stringResource(R.string.dialog_deleteServer_title, target.name), style = PgType.Title) },
            text = { Text(stringResource(if (inUse) R.string.dialog_deleteServer_textInUse else R.string.dialog_deleteServer_text), style = PgType.Body) },
            confirmButton = {
                TextButton(onClick = {
                    deleting = null
                    onDelete(target.id)
                }) {
                    Text(stringResource(R.string.dialog_deleteServer_delete), color = PgColor.Status.Danger.Text)
                }
            },
            dismissButton = {
                TextButton(onClick = { deleting = null }) {
                    Text(stringResource(R.string.common_back))
                }
            },
            containerColor = PgColor.Surface.Solid,
        )
    }
}

@Composable
private fun ServerRow(server: ServerItem, selected: Boolean, active: Boolean, onSelect: () -> Unit, onDelete: () -> Unit) {
    val shape = RoundedCornerShape(PgRadius.Card)
    Row(
        Modifier
            .fillMaxWidth()
            .clip(shape)
            .background(if (selected) PgColor.Accent.Tint else PgColor.Surface.Card)
            .semantics { this.selected = selected }
            .clickable(role = Role.RadioButton, onClick = onSelect)
            .padding(PgSpace.M)
            .testTag("server.${server.id}"),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        CountryBadge(server.countryCode)
        Spacer(Modifier.width(PgSpace.L))
        Column(Modifier.weight(1f)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(server.name, style = PgType.BodyStrong, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f, fill = false))
                val tag = when {
                    active -> R.string.servers_tag_active
                    selected -> R.string.servers_tag_selected
                    else -> null
                }

                if (tag != null) {
                    Spacer(Modifier.width(PgSpace.S))
                    Text(
                        stringResource(tag),
                        color = if (active) PgColor.Status.Success.Text else PgColor.Accent.Text,
                        style = PgType.Caption,
                        modifier = Modifier
                            .clip(RoundedCornerShape(PgRadius.Chip))
                            .background(if (active) PgColor.Status.Success.Tint else PgColor.Accent.Tint)
                            .padding(horizontal = PgSpace.Xs),
                    )
                }
            }

            Text(server.summary + " · " + stringResource(R.string.server_port, server.port.toString()), color = PgColor.Text.Secondary, style = PgType.Caption)
        }

        val delete = stringResource(R.string.card_delete)
        Box(
            Modifier.size(PgLayout.TouchTarget).clip(CircleShape).clickable(onClickLabel = delete, role = Role.Button, onClick = onDelete),
            contentAlignment = Alignment.Center,
        ) {
            PgIcon(PgIcons.Trash, delete, color = PgColor.Text.Muted, size = 20.dp)
        }
    }
}

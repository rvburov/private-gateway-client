package com.privategateway.client.ui.servers

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.selection.toggleable
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.privategateway.client.R
import com.privategateway.client.core.servers.ServerFacts
import com.privategateway.client.core.servers.ServerItem
import com.privategateway.client.core.servers.ServerNames
import com.privategateway.client.ui.MainUi
import com.privategateway.client.ui.home.CountryBadge
import com.privategateway.client.ui.theme.Overline
import com.privategateway.client.ui.theme.PgButton
import com.privategateway.client.ui.theme.PgCard
import com.privategateway.client.ui.theme.PgColor
import com.privategateway.client.ui.theme.PgIcon
import com.privategateway.client.ui.theme.PgIcons
import com.privategateway.client.ui.theme.PgLayout
import com.privategateway.client.ui.theme.PgPageHeader
import com.privategateway.client.ui.theme.PgRadius
import com.privategateway.client.ui.theme.PgSpace
import com.privategateway.client.ui.theme.PgType

/** Что умеет карточка сервера. [address] открывает запечатанную ссылку — только когда карточка открыта. */
class CardActions(
    val back: () -> Unit,
    val connect: (String) -> Unit,
    val favorite: (String, Boolean) -> Unit,
    val rename: (String, String) -> Unit,
    val delete: (String) -> Unit,
    val address: suspend (String) -> String?,
)

/**
 * Карточка сервера (доска 11; на телефоне — своя страница): избранное, состояние, задержка, сведения (адрес, протокол,
 * транспорт, защита, ядро), «Подключиться» или «Используется сейчас», свое название, удаление с подтверждением.
 */
@Composable
fun ServerCardScreen(ui: MainUi, id: String, actions: CardActions) {
    val server = ui.servers.firstOrNull { it.id == id }
    if (server == null) {
        // Сервер удален (здесь или обновлением подписки) — карточке нечего показывать.
        LaunchedEffect(id) { actions.back() }
        return
    }

    val active = ui.active(server.id)
    val selected = server.id == ui.selectedId
    var deleting by rememberSaveable { mutableStateOf(false) }
    Column(
        Modifier
            .fillMaxSize()
            .statusBarsPadding()
            .verticalScroll(rememberScrollState())
            .padding(start = PgSpace.WindowSideMobile, end = PgSpace.WindowSideMobile, bottom = PgLayout.ScrollBottomPadding)
            .testTag("card"),
    ) {
        PgPageHeader(server.displayName, actions.back)
        Spacer(Modifier.size(PgSpace.S))
        Row(verticalAlignment = Alignment.CenterVertically) {
            CountryBadge(server.countryCode)
            Spacer(Modifier.width(PgSpace.M))
            Column(Modifier.weight(1f)) {
                val group = server.subscriptionId?.let { group -> ui.subscriptions.firstOrNull { it.id == group } }
                val from = if (group != null) stringResource(R.string.card_from_subscription, group.name) else stringResource(R.string.card_from_link)
                Text(from, color = PgColor.Text.Secondary, style = PgType.Small, maxLines = 2, overflow = TextOverflow.Ellipsis)
                if (server.missingSince != null) {
                    Text(stringResource(R.string.card_missing), color = PgColor.Status.Caution.Text, style = PgType.Caption)
                }
            }

            LatencyText(server)
        }

        Spacer(Modifier.size(PgSpace.M))
        Row(verticalAlignment = Alignment.CenterVertically) {
            FavoriteToggle(server) { actions.favorite(server.id, it) }
            Spacer(Modifier.weight(1f))
            val status = when {
                active -> R.string.card_status_active
                selected -> R.string.card_status_selected
                else -> null
            }

            if (status != null) {
                Text(
                    stringResource(status),
                    color = if (active) PgColor.Status.Success.Text else PgColor.Accent.Text,
                    style = PgType.Caption,
                    modifier = Modifier
                        .clip(RoundedCornerShape(PgRadius.Chip))
                        .background(if (active) PgColor.Status.Success.Tint else PgColor.Accent.Tint)
                        .padding(horizontal = PgSpace.S, vertical = 2.dp)
                        .testTag("card.status"),
                )
            }
        }

        Spacer(Modifier.size(PgSpace.M))
        Details(server, actions.address)
        Spacer(Modifier.size(PgSpace.L))
        if (active) {
            Text(
                stringResource(R.string.card_inUse),
                color = PgColor.Status.Success.Text,
                style = PgType.BodyStrong,
                modifier = Modifier.fillMaxWidth().padding(vertical = PgSpace.M).testTag("card.inUse"),
            )
        } else {
            PgButton(stringResource(R.string.card_connect), enabled = true, tag = "card.connect") { actions.connect(server.id) }
        }

        Spacer(Modifier.size(PgSpace.L))
        Rename(server) { actions.rename(server.id, it) }
        Spacer(Modifier.size(PgSpace.L))
        Text(
            stringResource(R.string.card_delete),
            color = PgColor.Status.Danger.Text,
            style = PgType.BodyStrong,
            modifier = Modifier
                .heightIn(min = PgLayout.TouchTarget)
                .clip(RoundedCornerShape(PgRadius.Button))
                .clickable(role = Role.Button) { deleting = true }
                .padding(vertical = PgSpace.M)
                .testTag("card.delete"),
        )
    }

    if (deleting) {
        AlertDialog(
            onDismissRequest = { deleting = false },
            title = { Text(stringResource(R.string.dialog_deleteServer_title, server.displayName), style = PgType.Title) },
            text = { Text(stringResource(if (active) R.string.dialog_deleteServer_textInUse else R.string.dialog_deleteServer_text), style = PgType.Body) },
            confirmButton = {
                TextButton(onClick = {
                    deleting = false
                    actions.delete(server.id)
                }) {
                    Text(stringResource(R.string.dialog_deleteServer_delete), color = PgColor.Status.Danger.Text)
                }
            },
            dismissButton = {
                TextButton(onClick = { deleting = false }) {
                    Text(stringResource(R.string.dialog_cancel))
                }
            },
            containerColor = PgColor.Surface.Solid,
        )
    }
}

/** «В избранное» / «Убрать из избранного»: звезда с подписью, переключатель. */
@Composable
private fun FavoriteToggle(server: ServerItem, onChange: (Boolean) -> Unit) {
    Row(
        Modifier
            .heightIn(min = PgLayout.TouchTarget)
            .clip(RoundedCornerShape(PgRadius.Button))
            .toggleable(value = server.favorite, role = Role.Switch, onValueChange = onChange)
            .padding(horizontal = PgSpace.Xs)
            .testTag("card.favorite"),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        if (server.favorite) {
            FavoriteMark(20.dp)
        } else {
            PgIcon(PgIcons.Star, null, color = PgColor.Text.Muted, size = 20.dp)
        }

        Spacer(Modifier.width(PgSpace.S))
        Text(stringResource(if (server.favorite) R.string.card_favorite_remove else R.string.card_favorite_add), style = PgType.Body)
    }
}

/** Сведения: адрес (из запечатанной ссылки — по открытии карточки), протокол, транспорт, защита, ядро. */
@Composable
private fun Details(server: ServerItem, address: suspend (String) -> String?) {
    val host by produceState("…", server.id) { value = address(server.id) ?: "—" }
    PgCard(Modifier.fillMaxWidth().testTag("card.details")) {
        Detail(stringResource(R.string.card_address), host, "card.address")
        Detail(stringResource(R.string.card_protocol), ServerFacts.protocolName(server.protocol), "card.protocol")
        Detail(stringResource(R.string.card_transport), ServerFacts.transportName(server.transport) ?: server.transport.name.uppercase(), "card.transport")
        val security = ServerFacts.securityName(server.security)
            ?: stringResource(if (server.encrypted) R.string.card_security_builtin else R.string.card_security_none)
        Detail(stringResource(R.string.card_security), security, "card.security")
        Detail(stringResource(R.string.card_core), ServerFacts.coreName(server.protocol) ?: stringResource(R.string.android_card_core_none), "card.core")
    }
}

@Composable
private fun Detail(label: String, value: String, tag: String) {
    Row(Modifier.fillMaxWidth().padding(vertical = PgSpace.Xs), verticalAlignment = Alignment.Top) {
        Text(label, color = PgColor.Text.Secondary, style = PgType.Small, modifier = Modifier.width(110.dp))
        Text(value, style = PgType.Mono, modifier = Modifier.weight(1f).testTag(tag))
    }
}

/** Свое название: 1–64 знака без пробелов по краям; «Сохранить» гаснет, пока сохранять нечего. */
@Composable
private fun Rename(server: ServerItem, onSave: (String) -> Unit) {
    var name by rememberSaveable(server.id) { mutableStateOf(server.displayName) }
    val valid = ServerNames.validate(name)
    Overline(stringResource(R.string.card_rename))
    Spacer(Modifier.size(PgSpace.Xs))
    val shape = RoundedCornerShape(PgRadius.Input)
    val label = stringResource(R.string.card_rename_label)
    BasicTextField(
        value = name,
        onValueChange = { name = it },
        singleLine = true,
        textStyle = PgType.Body.copy(color = PgColor.Text.Primary),
        cursorBrush = SolidColor(PgColor.Accent.Base),
        modifier = Modifier
            .fillMaxWidth()
            .clip(shape)
            .background(PgColor.Surface.Inset)
            .border(1.dp, PgColor.Line.Border, shape)
            .padding(PgSpace.M)
            .testTag("card.rename.text"),
        decorationBox = { field ->
            Box {
                if (name.isEmpty()) {
                    Text(label, color = PgColor.Text.Muted, style = PgType.Body)
                }

                field()
            }
        },
    )
    Spacer(Modifier.size(PgSpace.S))
    PgButton(stringResource(R.string.card_rename_save), enabled = valid != null && valid != server.displayName, tag = "card.rename.save") {
        valid?.let(onSave)
    }
}

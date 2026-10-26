package com.privategateway.client.ui.subscriptions

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
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
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
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
import com.privategateway.client.data.GroupItem
import com.privategateway.client.ui.MainUi
import com.privategateway.client.ui.Texts
import com.privategateway.client.ui.theme.Overline
import com.privategateway.client.ui.theme.PgButton
import com.privategateway.client.ui.theme.PgCard
import com.privategateway.client.ui.theme.PgChoiceRow
import com.privategateway.client.ui.theme.PgColor
import com.privategateway.client.ui.theme.PgLayout
import com.privategateway.client.ui.theme.PgPageHeader
import com.privategateway.client.ui.theme.PgRadius
import com.privategateway.client.ui.theme.PgSpace
import com.privategateway.client.ui.theme.PgToggleRow
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

/**
 * «Профили и подписки» (доска 12 в одну колонку): автообновление, «только через VPN» (защита выключена — подписки не
 * обновляются), сообщения о выбранном сервере; карточки подписок и профилей — состояние, маска адреса, когда обновлялась,
 * причина неудачи, «Обновить», «Изменить», «Удалить»; «Мои серверы» — последней.
 */
@Composable
fun SubscriptionsScreen(state: SubscriptionsUi, main: MainUi, actions: SubscriptionsActions, onBack: () -> Unit) {
    var editing by rememberSaveable { mutableStateOf<String?>(null) }
    var removing by rememberSaveable { mutableStateOf<String?>(null) }
    val language = Texts.language(LocalConfiguration.current.locales)
    // Группа сервера, через который идет защита: удалять ее — значит отключить VPN. «Мои серверы» — пустая строка.
    val inUse = main.servers.firstOrNull { main.active(it.id) }?.let { it.subscriptionId ?: MINE }
    Column(
        Modifier
            .fillMaxSize()
            .statusBarsPadding()
            .verticalScroll(rememberScrollState())
            .padding(start = PgSpace.WindowSideMobile, end = PgSpace.WindowSideMobile, bottom = PgLayout.ScrollBottomPadding)
            .testTag("subscriptions"),
        verticalArrangement = Arrangement.spacedBy(PgSpace.L),
    ) {
        PgPageHeader(stringResource(R.string.settings_adv_profiles_title), onBack)
        SettingsCard(state, actions.settings)
        Overline(stringResource(R.string.subs_section, state.groups.size.toString()))

        PgButton(stringResource(R.string.subs_add), enabled = true, tag = "subs.add", onClick = actions.add)
        if (state.groups.isEmpty() && state.manualCount == 0) {
            Text(stringResource(R.string.subs_empty), color = PgColor.Text.Secondary, style = PgType.Body)
        }

        for (group in state.groups) {
            if (editing == group.id) {
                EditCard(group, onSave = { name, url -> actions.edit(group, name, url) }, onClose = { editing = null })
            } else {
                GroupCard(group, language, busy = group.id in state.busy, actions, onEdit = { editing = group.id }, onRemove = { removing = group.id })
            }
        }

        if (state.manualCount > 0) {
            MineCard(state.manualCount, language) { removing = MINE }
        }
    }

    val target = removing
    if (target != null) {
        val group = state.groups.firstOrNull { it.id == target }
        val name = group?.name ?: stringResource(R.string.subs_mine_name)
        val count = group?.servers ?: state.manualCount
        val servers = stringResource(serversText(count, language), count.toString())
        val active = inUse == target
        AlertDialog(
            onDismissRequest = { removing = null },
            title = { Text(stringResource(R.string.subs_remove_title, name), style = PgType.Title) },
            text = { Text(stringResource(if (active) R.string.subs_remove_textInUse else R.string.subs_remove_text, servers), style = PgType.Body) },
            confirmButton = {
                TextButton(
                    onClick = {
                        removing = null
                        actions.remove(group?.id, name, active)
                    },
                    modifier = Modifier.testTag("subs.remove.confirm"),
                ) {
                    Text(stringResource(R.string.dialog_deleteServer_delete), color = PgColor.Status.Danger.Text)
                }
            },
            dismissButton = {
                TextButton(onClick = { removing = null }) {
                    Text(stringResource(R.string.dialog_cancel))
                }
            },
            containerColor = PgColor.Surface.Solid,
        )
    }
}

/** «Мои серверы» в [SubscriptionsScreen]: метка группы без подписки. */
private const val MINE = ""

@Composable
private fun SettingsCard(state: SubscriptionsUi, onChange: (SubscriptionSettings) -> Unit) {
    val settings = state.settings
    PgCard(Modifier.fillMaxWidth()) {
        Text(stringResource(R.string.subs_refresh_title), style = PgType.BodyStrong)
        Text(stringResource(R.string.subs_refresh_text), color = PgColor.Text.Secondary, style = PgType.Caption)
        for (interval in RefreshInterval.entries) {
            val label = when (interval) {
                RefreshInterval.Off -> R.string.subs_refresh_off
                RefreshInterval.Every6Hours -> R.string.subs_refresh_6
                RefreshInterval.Every12Hours -> R.string.subs_refresh_12
                RefreshInterval.Every24Hours -> R.string.subs_refresh_24
            }

            PgChoiceRow(stringResource(label), null, settings.refresh == interval, "refresh.${interval.name}") {
                onChange(settings.copy(refresh = interval))
            }
        }

        Spacer(Modifier.size(PgSpace.M))
        PgToggleRow(stringResource(R.string.subs_vpn_title), stringResource(R.string.subs_vpn_text), settings.onlyViaVpn, "subs.vpn") {
            onChange(settings.copy(onlyViaVpn = it))
        }

        if (settings.onlyViaVpn && !state.connected) {
            Text(
                stringResource(R.string.subs_vpn_blocked_off),
                color = PgColor.Status.Caution.Text,
                style = PgType.Small,
                modifier = Modifier
                    .fillMaxWidth()
                    .clip(RoundedCornerShape(PgRadius.Button))
                    .background(PgColor.Status.Caution.Tint)
                    .padding(PgSpace.S)
                    .testTag("subs.vpn.blocked"),
            )
        }

        PgToggleRow(stringResource(R.string.subs_notify_title), stringResource(R.string.subs_notify_text), settings.notifySelected, "subs.notify") {
            onChange(settings.copy(notifySelected = it))
        }
    }
}

/** Карточка подписки или профиля: название и состояние, адрес (маска), серверы и время, причина неудачи, действия. */
@Composable
private fun GroupCard(group: GroupItem, language: String, busy: Boolean, actions: SubscriptionsActions, onEdit: () -> Unit, onRemove: () -> Unit) {
    PgCard(Modifier.fillMaxWidth().testTag("group.${group.id}")) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(group.name, style = PgType.BodyStrong, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f))
            val chip = when {
                !group.canUpdate -> Chip.Manual
                group.failure != null -> Chip.Error
                else -> Chip.Ok
            }

            StateChip(chip)
        }

        Text(
            group.maskedUrl ?: stringResource(R.string.subs_manual_address),
            color = PgColor.Text.Secondary,
            style = if (group.maskedUrl != null) PgType.Mono else PgType.Small,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
        Text(meta(group, language), color = PgColor.Text.Muted, style = PgType.Caption, modifier = Modifier.testTag("group.meta"))
        val failure = group.failure
        if (failure != null) {
            val (text, arg) = Texts.failure(FailureReason.of(failure, group.status))
            val reason = if (arg != null) stringResource(text, arg) else stringResource(text)
            val locale = LocalConfiguration.current.locales[0]
            Text(
                stringResource(R.string.subs_error, reason.replaceFirstChar { it.titlecase(locale) }),
                color = PgColor.Status.Danger.Text,
                style = PgType.Small,
                modifier = Modifier
                    .padding(top = PgSpace.S)
                    .fillMaxWidth()
                    .clip(RoundedCornerShape(PgRadius.Button))
                    .background(PgColor.Status.Danger.Tint)
                    .padding(PgSpace.S)
                    .testTag("group.error"),
            )
        }

        Row(Modifier.padding(top = PgSpace.S), horizontalArrangement = Arrangement.spacedBy(PgSpace.M)) {
            if (group.canUpdate) {
                Action(stringResource(if (busy) R.string.import_downloading else R.string.subs_update), "subs.update", enabled = !busy) { actions.update(group) }
            }

            Action(stringResource(R.string.subs_edit), "subs.edit", onClick = onEdit)
            Action(stringResource(R.string.subs_remove, group.name), "subs.remove", color = PgColor.Status.Danger.Text, onClick = onRemove)
        }
    }
}

/** «Мои серверы»: серверы без группы — добавлены ссылками; не обновляются, удалить можно разом. */
@Composable
private fun MineCard(count: Int, language: String, onRemove: () -> Unit) {
    PgCard(Modifier.fillMaxWidth().testTag("group.mine")) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(stringResource(R.string.subs_mine_name), style = PgType.BodyStrong, modifier = Modifier.weight(1f))
            StateChip(Chip.Manual)
        }

        Text(stringResource(R.string.subs_mine_address), color = PgColor.Text.Secondary, style = PgType.Small)
        Text(stringResource(serversText(count, language), count.toString()), color = PgColor.Text.Muted, style = PgType.Caption)
        Row(Modifier.padding(top = PgSpace.S)) {
            Action(stringResource(R.string.subs_remove, stringResource(R.string.subs_mine_name)), "subs.remove", color = PgColor.Status.Danger.Text, onClick = onRemove)
        }
    }
}

/** «Изменить»: название и новый адрес (пустой — прежний); адрес хранится только в Android Keystore. */
@Composable
private fun EditCard(group: GroupItem, onSave: (String, String) -> Boolean, onClose: () -> Unit) {
    var name by rememberSaveable(group.id) { mutableStateOf(group.name) }
    // Новый адрес подписки — секрет: в сохраненное состояние окна он не пишется.
    var url by remember(group.id) { mutableStateOf("") }
    var bad by rememberSaveable(group.id) { mutableStateOf(false) }
    PgCard(Modifier.fillMaxWidth().testTag("subs.editor")) {
        Text(stringResource(R.string.subs_edit_title, group.name), style = PgType.Title)
        Spacer(Modifier.size(PgSpace.M))
        Field(stringResource(R.string.subs_edit_name), name, "subs.edit.name") { name = it }
        // Профилю без адреса тоже можно дать адрес — он станет подпиской.
        Spacer(Modifier.size(PgSpace.M))
        Field(stringResource(R.string.subs_edit_url), url, "subs.edit.url") {
            url = it
            bad = false
        }

        Text(stringResource(R.string.subs_edit_url_note, "Android Keystore"), color = PgColor.Text.Muted, style = PgType.Caption)
        if (bad) {
            Text(stringResource(R.string.subs_edit_url_bad), color = PgColor.Status.Danger.Text, style = PgType.Small, modifier = Modifier.testTag("subs.edit.bad"))
        }

        Spacer(Modifier.size(PgSpace.M))
        PgButton(stringResource(R.string.subs_edit_save), enabled = name.isNotBlank(), tag = "subs.edit.save") {
            if (onSave(name, url)) {
                onClose()
            } else {
                bad = true
            }
        }

        Spacer(Modifier.size(PgSpace.S))
        Action(stringResource(R.string.dialog_cancel), "subs.edit.cancel", onClick = onClose)
    }
}

@Composable
private fun Field(label: String, value: String, tag: String, onChange: (String) -> Unit) {
    Text(label, color = PgColor.Text.Secondary, style = PgType.Caption)
    Spacer(Modifier.size(PgSpace.Xs))
    val shape = RoundedCornerShape(PgRadius.Input)
    BasicTextField(
        value = value,
        onValueChange = onChange,
        singleLine = true,
        textStyle = PgType.Body.copy(color = PgColor.Text.Primary),
        cursorBrush = SolidColor(PgColor.Accent.Base),
        modifier = Modifier
            .fillMaxWidth()
            .clip(shape)
            .background(PgColor.Surface.Inset)
            .border(1.dp, PgColor.Line.Border, shape)
            .padding(PgSpace.M)
            .testTag(tag),
    )
}

@Composable
private fun Action(label: String, tag: String, enabled: Boolean = true, color: Color = PgColor.Accent.Text, onClick: () -> Unit) {
    Text(
        label,
        color = if (enabled) color else PgColor.Text.Muted,
        style = PgType.BodyStrong,
        maxLines = 1,
        overflow = TextOverflow.Ellipsis,
        modifier = Modifier
            .heightIn(min = PgLayout.TouchTarget)
            .clip(RoundedCornerShape(PgRadius.Button))
            .clickable(enabled = enabled, role = Role.Button, onClick = onClick)
            .padding(vertical = PgSpace.M)
            .testTag(tag),
    )
}

private enum class Chip {
    Ok,
    Error,
    Manual,
}

/** Состояние группы: «Работает», «Ошибка», «Вручную» — словом и цветом. */
@Composable
private fun StateChip(chip: Chip) {
    val (text, color, tint) = when (chip) {
        Chip.Ok -> Triple(R.string.subs_chip_ok, PgColor.Status.Success.Text, PgColor.Status.Success.Tint)
        Chip.Error -> Triple(R.string.subs_chip_error, PgColor.Status.Danger.Text, PgColor.Status.Danger.Tint)
        Chip.Manual -> Triple(R.string.subs_chip_manual, PgColor.Text.Secondary, PgColor.Control.SegmentTrack)
    }

    Box(
        Modifier
            .clip(RoundedCornerShape(PgRadius.Chip))
            .background(tint)
            .padding(horizontal = PgSpace.S, vertical = 2.dp),
    ) {
        Text(stringResource(text), color = color, style = PgType.Caption)
    }
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

@Composable
private fun day(day: Day): String = when (day) {
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

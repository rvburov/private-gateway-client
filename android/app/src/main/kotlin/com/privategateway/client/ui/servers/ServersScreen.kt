package com.privategateway.client.ui.servers

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
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
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.selected
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import com.privategateway.client.R
import com.privategateway.client.core.servers.LatencyKind
import com.privategateway.client.core.servers.ProxyProtocol
import com.privategateway.client.core.servers.ServerFacts
import com.privategateway.client.core.servers.ServerGroup
import com.privategateway.client.core.servers.ServerItem
import com.privategateway.client.core.servers.ServerList
import com.privategateway.client.core.servers.ServerListEmpty
import com.privategateway.client.core.servers.ServerListQuery
import com.privategateway.client.core.servers.ServerListTab
import com.privategateway.client.core.servers.ServerSort
import com.privategateway.client.ui.MainUi
import com.privategateway.client.ui.home.CountryBadge
import com.privategateway.client.ui.theme.Overline
import com.privategateway.client.ui.theme.PgCard
import com.privategateway.client.ui.theme.PgChoiceRow
import com.privategateway.client.ui.theme.PgColor
import com.privategateway.client.ui.theme.PgIcon
import com.privategateway.client.ui.theme.PgIcons
import com.privategateway.client.ui.theme.PgLayout
import com.privategateway.client.ui.theme.PgRadius
import com.privategateway.client.ui.theme.PgSpace
import com.privategateway.client.ui.theme.PgToggleRow
import com.privategateway.client.ui.theme.PgType
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter

/** Что умеет список серверов: выбор, карточка, добавление и запрос списка (поиск, вкладка, фильтр, порядок). */
class ServersActions(
    val select: (String) -> Unit,
    val open: (String) -> Unit,
    val add: () -> Unit,
    val refresh: () -> Unit,
    val search: (String) -> Unit,
    val tab: (ServerListTab) -> Unit,
    val toggleProtocol: (ProxyProtocol) -> Unit,
    val sort: (ServerSort) -> Unit,
    val reset: () -> Unit,
)

/**
 * «Серверы» (доска 2 в одну колонку): поиск, фильтр протоколов и порядок, вкладки «Все» · «Избранные» · «Недавние»,
 * группы по подпискам и «Мои серверы». Нажатие на строку выбирает сервер (под защитой — сразу переподключение), «⋯» —
 * карточка. Сервер меняется только нажатием пользователя.
 */
@Composable
fun ServersScreen(ui: MainUi, query: ServerListQuery, actions: ServersActions) {
    var filterOpen by rememberSaveable { mutableStateOf(false) }
    val locale = LocalConfiguration.current.locales[0]
    val groups = remember(ui.servers, ui.subscriptions, query, locale) { ServerList.build(ui.servers, ui.subscriptions, query, locale) }
    val empty = ServerList.empty(ui.servers, groups, query)
    LazyColumn(
        Modifier.fillMaxSize().statusBarsPadding().testTag("servers"),
        contentPadding = PaddingValues(start = PgSpace.WindowSideMobile, end = PgSpace.WindowSideMobile, bottom = PgLayout.ScrollBottomPadding),
        verticalArrangement = Arrangement.spacedBy(PgSpace.S),
    ) {
        item {
            Row(Modifier.fillMaxWidth().padding(top = PgSpace.L, bottom = PgSpace.S), verticalAlignment = Alignment.CenterVertically) {
                Text(stringResource(R.string.nav_servers), style = PgType.H1, modifier = Modifier.weight(1f))
                if (ui.servers.isNotEmpty()) {
                    // Задержка через ядро — только без защиты; пока идет замер, кнопка гаснет (решение 430).
                    RoundButton(PgIcons.Refresh, stringResource(R.string.server_refresh), "servers.refresh", actions.refresh, enabled = !ui.measuring)
                }

                RoundButton(PgIcons.Plus, stringResource(R.string.header_add), "servers.add", actions.add)
            }
        }

        if (ui.servers.isNotEmpty()) {
            item {
                SearchRow(query.search, ServerList.filterCount(ui.servers, query), filterOpen, actions.search) { filterOpen = !filterOpen }
            }

            if (filterOpen) {
                item {
                    FilterPanel(ui.servers, query, actions, onDone = { filterOpen = false })
                }
            }

            item {
                Tabs(query.tab, ui.servers.size, actions.tab)
            }
        }

        if (empty != null) {
            item {
                PgCard(Modifier.fillMaxWidth().testTag("servers.empty")) {
                    Text(stringResource(emptyText(empty)), color = PgColor.Text.Secondary, style = PgType.Body)
                }
            }
        }

        for (group in groups) {
            item(key = "group:" + (group.subscription?.id ?: "manual")) {
                GroupHeader(group, query.tab)
            }

            items(group.servers, key = { it.id }) { server ->
                ServerRow(
                    server,
                    selected = server.id == ui.selectedId,
                    active = ui.active(server.id),
                    onSelect = { actions.select(server.id) },
                    onMore = { actions.open(server.id) },
                )
            }
        }

        item {
            Text(stringResource(R.string.servers_note), color = PgColor.Text.Muted, style = PgType.Caption, modifier = Modifier.padding(top = PgSpace.S))
        }
    }
}

private fun emptyText(empty: ServerListEmpty): Int = when (empty) {
    ServerListEmpty.NoServers -> R.string.servers_none
    ServerListEmpty.NoFavorites -> R.string.servers_empty_favorites
    ServerListEmpty.NoRecent -> R.string.servers_empty_recent
    ServerListEmpty.NothingFound -> R.string.servers_empty
}

@Composable
private fun RoundButton(icon: String, label: String, tag: String, onClick: () -> Unit, enabled: Boolean = true) {
    Box(
        Modifier
            .size(PgLayout.TouchTarget)
            .clip(CircleShape)
            .clickable(enabled = enabled, onClickLabel = label, role = Role.Button, onClick = onClick)
            .testTag(tag),
        contentAlignment = Alignment.Center,
    ) {
        PgIcon(icon, label, color = if (enabled) PgColor.Text.Primary else PgColor.Text.Muted, size = 24.dp, strokeWidth = 2f)
    }
}

/** Поиск (название, страна, протокол — адрес запечатан, решение 420) и кнопка фильтра с числом отличий от обычного. */
@Composable
private fun SearchRow(search: String, filterCount: Int, filterOpen: Boolean, onSearch: (String) -> Unit, onFilter: () -> Unit) {
    val shape = RoundedCornerShape(PgRadius.Input)
    val label = stringResource(R.string.servers_search_label)
    Row(verticalAlignment = Alignment.CenterVertically) {
        Row(
            Modifier
                .weight(1f)
                .heightIn(min = PgLayout.TouchTarget)
                .clip(shape)
                .background(PgColor.Surface.Solid)
                .border(1.dp, PgColor.Line.Border, shape)
                .padding(horizontal = PgSpace.M),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            PgIcon(PgIcons.Search, null, color = PgColor.Text.Muted, size = 18.dp)
            Spacer(Modifier.width(PgSpace.S))
            BasicTextField(
                value = search,
                onValueChange = onSearch,
                singleLine = true,
                textStyle = PgType.Body.copy(color = PgColor.Text.Primary),
                cursorBrush = SolidColor(PgColor.Accent.Base),
                modifier = Modifier.weight(1f).semantics { contentDescription = label }.testTag("servers.search"),
                decorationBox = { field ->
                    Box {
                        if (search.isEmpty()) {
                            Text(stringResource(R.string.android_servers_search_placeholder), color = PgColor.Text.Muted, style = PgType.Body, maxLines = 1)
                        }

                        field()
                    }
                },
            )
        }

        Spacer(Modifier.width(PgSpace.S))
        val filter = stringResource(R.string.servers_filter)
        Box(
            Modifier
                .size(PgLayout.TouchTarget)
                .clip(shape)
                .background(if (filterOpen || filterCount > 0) PgColor.Accent.Tint else PgColor.Surface.Solid)
                .border(1.dp, PgColor.Line.Border, shape)
                .semantics { selected = filterOpen }
                .clickable(onClickLabel = filter, role = Role.Button, onClick = onFilter)
                .testTag("servers.filter"),
            contentAlignment = Alignment.Center,
        ) {
            PgIcon(PgIcons.Filter, filter, color = if (filterOpen || filterCount > 0) PgColor.Accent.Text else PgColor.Text.Primary)
            if (filterCount > 0) {
                Text(
                    filterCount.toString(),
                    color = PgColor.Surface.Solid,
                    style = PgType.Caption,
                    modifier = Modifier
                        .align(Alignment.TopEnd)
                        .padding(2.dp)
                        .clip(CircleShape)
                        .background(PgColor.Accent.Base)
                        .padding(horizontal = 5.dp)
                        .testTag("servers.filter.count"),
                )
            }
        }
    }
}

/** Фильтр (на десктопе — всплывающая панель): протоколы, что есть в списке, порядок, «Сбросить» и «Готово». */
@Composable
private fun FilterPanel(servers: List<ServerItem>, query: ServerListQuery, actions: ServersActions, onDone: () -> Unit) {
    PgCard(Modifier.fillMaxWidth().testTag("servers.filter.panel")) {
        Overline(stringResource(R.string.servers_filter_protocols))
        for (protocol in ServerList.protocolsIn(servers)) {
            PgToggleRow(ServerFacts.protocolName(protocol), null, protocol !in query.hiddenProtocols, "filter.${protocol.name}") {
                actions.toggleProtocol(protocol)
            }
        }

        Spacer(Modifier.size(PgSpace.M))
        Overline(stringResource(R.string.servers_filter_sort))
        PgChoiceRow(stringResource(R.string.servers_sort_latency), null, query.sort == ServerSort.Latency, "sort.Latency") { actions.sort(ServerSort.Latency) }
        PgChoiceRow(stringResource(R.string.servers_sort_name), null, query.sort == ServerSort.Name, "sort.Name") { actions.sort(ServerSort.Name) }
        Spacer(Modifier.size(PgSpace.S))
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) {
            TextAction(stringResource(R.string.servers_filter_reset), "filter.reset", actions.reset)
            Spacer(Modifier.width(PgSpace.M))
            TextAction(stringResource(R.string.servers_filter_done), "filter.done", onDone)
        }
    }
}

@Composable
private fun TextAction(label: String, tag: String, onClick: () -> Unit) {
    Text(
        label,
        color = PgColor.Accent.Text,
        style = PgType.BodyStrong,
        modifier = Modifier
            .heightIn(min = PgLayout.TouchTarget)
            .clip(RoundedCornerShape(PgRadius.Button))
            .clickable(role = Role.Button, onClick = onClick)
            .padding(horizontal = PgSpace.S, vertical = PgSpace.M)
            .testTag(tag),
    )
}

/** Вкладки-сегменты: «Все · N», «Избранные», «Недавние». */
@Composable
private fun Tabs(current: ServerListTab, total: Int, onTab: (ServerListTab) -> Unit) {
    val shape = RoundedCornerShape(PgRadius.Button)
    val tabs = stringResource(R.string.servers_tabs)
    Row(
        Modifier
            .fillMaxWidth()
            .clip(shape)
            .background(PgColor.Control.SegmentTrack)
            .padding(3.dp)
            .semantics { contentDescription = tabs },
    ) {
        for (tab in ServerListTab.entries) {
            val selected = tab == current
            val label = when (tab) {
                ServerListTab.All -> stringResource(R.string.servers_tab_all, total.toString())
                ServerListTab.Favorites -> stringResource(R.string.servers_tab_favorites)
                ServerListTab.Recent -> stringResource(R.string.servers_tab_recent)
            }

            Box(
                Modifier
                    .weight(1f)
                    .heightIn(min = PgLayout.TouchTarget - 6.dp)
                    .clip(shape)
                    .background(if (selected) PgColor.Surface.Solid else Color.Transparent)
                    .semantics { this.selected = selected }
                    .clickable(role = Role.Tab) { onTab(tab) }
                    .testTag("tab.${tab.name}"),
                contentAlignment = Alignment.Center,
            ) {
                Text(label, color = if (selected) PgColor.Text.Primary else PgColor.Text.Secondary, style = if (selected) PgType.BodyStrong else PgType.Body, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
        }
    }
}

/** Заголовок группы: «Подписка · 7» и «обновлено 14:20», «Мои серверы · 2», «Избранные · 3», «Недавние · 4». */
@Composable
private fun GroupHeader(group: ServerGroup, tab: ServerListTab) {
    val count = group.servers.size.toString()
    val subscription = group.subscription
    val title = when {
        subscription != null -> stringResource(R.string.servers_group_subscription, subscription.name, count)
        tab == ServerListTab.Favorites -> stringResource(R.string.servers_group_favorites, count)
        tab == ServerListTab.Recent -> stringResource(R.string.servers_group_recent, count)
        else -> stringResource(R.string.servers_group_manual, count)
    }

    Row(Modifier.fillMaxWidth().padding(top = PgSpace.M).testTag("group.${subscription?.id ?: tab.name}"), verticalAlignment = Alignment.CenterVertically) {
        Overline(title, Modifier.weight(1f))
        if (subscription != null) {
            val updated = subscription.updatedAt?.let { stringResource(R.string.servers_updated, clock(it)) } ?: stringResource(R.string.servers_updated_never)
            Text(updated, color = PgColor.Text.Muted, style = PgType.Caption)
        }
    }
}

/** «14:20» — время по часам телефона. */
private fun clock(at: Long): String = DateTimeFormatter.ofPattern("HH:mm").withZone(ZoneId.systemDefault()).format(Instant.ofEpochMilli(at))

@Composable
private fun ServerRow(server: ServerItem, selected: Boolean, active: Boolean, onSelect: () -> Unit, onMore: () -> Unit) {
    val shape = RoundedCornerShape(PgRadius.Card)
    Row(
        Modifier
            .fillMaxWidth()
            .clip(shape)
            .background(if (selected) PgColor.Accent.Tint else PgColor.Surface.Card)
            .semantics { this.selected = selected }
            .clickable(role = Role.RadioButton, onClick = onSelect)
            .padding(start = PgSpace.M, top = PgSpace.S, bottom = PgSpace.S)
            .testTag("server.${server.id}"),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        CountryBadge(server.countryCode)
        Spacer(Modifier.width(PgSpace.L))
        Column(Modifier.weight(1f)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                if (server.favorite) {
                    FavoriteMark(14.dp)
                    Spacer(Modifier.width(PgSpace.Xs))
                }

                Text(server.displayName, style = PgType.BodyStrong, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f, fill = false))
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
            if (server.missingSince != null) {
                Text(stringResource(R.string.card_missing), color = PgColor.Status.Caution.Text, style = PgType.Caption)
            }
        }

        Spacer(Modifier.width(PgSpace.S))
        LatencyText(server)
        val more = stringResource(R.string.servers_more_name, server.displayName)
        Box(
            Modifier.size(PgLayout.TouchTarget).clip(CircleShape).clickable(onClickLabel = more, role = Role.Button, onClick = onMore).testTag("more.${server.id}"),
            contentAlignment = Alignment.Center,
        ) {
            PgIcon(PgIcons.More, more, color = PgColor.Text.Muted, size = 20.dp)
        }
    }
}

/** Задержка: быстрый — зеленым, медленный — серым, «нет» — оранжевым (сервер не ответил), «—» — еще не мерили. */
@Composable
fun LatencyText(server: ServerItem, modifier: Modifier = Modifier) {
    val kind = LatencyKind.of(server.latency)
    val ms = server.latency?.ms
    val (text, color) = when (kind) {
        LatencyKind.Good -> "$ms ${stringResource(R.string.unit_ms)}" to PgColor.Status.Success.Text
        LatencyKind.Slow -> "$ms ${stringResource(R.string.unit_ms)}" to PgColor.Text.Muted
        LatencyKind.NoAnswer -> stringResource(R.string.servers_latency_none) to PgColor.Status.Warning.Text
        LatencyKind.Never -> "—" to PgColor.Text.Muted
    }

    val hint = when (kind) {
        LatencyKind.NoAnswer -> stringResource(R.string.servers_latency_noneHint)
        LatencyKind.Never -> stringResource(R.string.android_servers_latency_never)
        else -> text
    }

    Text(text, color = color, style = PgType.Mono, maxLines = 1, modifier = modifier.semantics { contentDescription = hint }.testTag("latency.${server.id}"))
}

/** Звезда избранного: заливка и контур цветами favorite.* макета. */
@Composable
fun FavoriteMark(size: Dp) {
    Box {
        PgIcon(PgIcons.Star, null, color = PgColor.Favorite.Fill, size = size, filled = true)
        PgIcon(PgIcons.Star, null, color = PgColor.Favorite.Stroke, size = size)
    }
}

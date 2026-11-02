package com.privategateway.client.ui.servers

import androidx.activity.compose.BackHandler
import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
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
import androidx.compose.ui.draw.scale
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.selected
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
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
import com.privategateway.client.core.vpn.TunnelState
import com.privategateway.client.ui.MainUi
import com.privategateway.client.ui.theme.Overline
import com.privategateway.client.ui.theme.PgCheck
import com.privategateway.client.ui.theme.PgColor
import com.privategateway.client.ui.theme.PgIcon
import com.privategateway.client.ui.theme.PgIcons
import com.privategateway.client.ui.theme.PgLayout
import com.privategateway.client.ui.theme.PgPrimaryButton
import com.privategateway.client.ui.theme.PgRadius
import com.privategateway.client.ui.theme.PgSecondaryButton
import com.privategateway.client.ui.theme.PgSegments
import com.privategateway.client.ui.theme.PgShadow
import com.privategateway.client.ui.theme.PgSpace
import com.privategateway.client.ui.theme.PgType
import com.privategateway.client.ui.theme.pgShadow
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

/** Шапка страницы (.pg-header) и поле поиска (.sv-search): высоты макета — от них панель фильтра встает под поле. */
private val HeaderHeight = 60.dp
private val SearchHeight = 46.dp

/** Панель фильтра — на 6 ниже поля поиска (Margin 20,52 у десктопа: поле 46 и зазор 6). */
private val FilterGap = 6.dp

/**
 * «Серверы» (доска 2, ServersPage десктопа): шапка «Серверы» с «Обновить серверы» и «+», поиск с кнопкой фильтра и вкладки
 * «Все» · «Избранные» · «Недавние» стоят на месте, прокручивается только список — группы по подпискам и «Мои серверы».
 * Фильтр протоколов и порядок — панель поверх вкладок и списка; нажатие мимо нее и «Назад» ее закрывают. Нажатие на строку
 * выбирает сервер (под защитой — сразу переподключение), «⋯» — карточка. Сервер меняется только нажатием пользователя.
 */
@Composable
fun ServersScreen(ui: MainUi, query: ServerListQuery, actions: ServersActions) {
    var filterOpen by rememberSaveable { mutableStateOf(false) }
    val locale = LocalConfiguration.current.locales[0]
    val groups = remember(ui.servers, ui.subscriptions, query, locale) { ServerList.build(ui.servers, ui.subscriptions, query, locale) }
    val empty = ServerList.empty(ui.servers, groups, query)
    Box(Modifier.fillMaxSize().statusBarsPadding()) {
        Column(Modifier.fillMaxSize().testTag("servers")) {
            Header(ui.measuring, actions)
            Column(Modifier.padding(horizontal = PgSpace.WindowSideMobile), verticalArrangement = Arrangement.spacedBy(PgSpace.M)) {
                SearchField(query.search, ServerList.filterCount(ui.servers, query), filterOpen, actions.search) { filterOpen = !filterOpen }
                Tabs(query.tab, ui.servers.size, actions.tab)
            }

            LazyColumn(
                Modifier.fillMaxWidth().weight(1f).testTag("servers.list"),
                contentPadding = PaddingValues(
                    start = PgSpace.WindowSideMobile,
                    top = PgSpace.L,
                    end = PgSpace.WindowSideMobile,
                    bottom = PgLayout.ScrollBottomPadding,
                ),
                verticalArrangement = Arrangement.spacedBy(PgSpace.Xs),
            ) {
                groups.forEachIndexed { index, group ->
                    item(key = "group:" + (group.subscription?.id ?: "manual")) {
                        GroupHeader(group, query.tab, first = index == 0)
                    }

                    items(group.servers, key = { it.id }) { server ->
                        ServerRow(
                            server,
                            selected = server.id == ui.selectedId,
                            // «активен» — только когда защита идет через этот сервер; в подключении и переподключении — «выбран».
                            connected = ui.status.state == TunnelState.Connected && ui.status.serverId == server.id,
                            onSelect = { actions.select(server.id) },
                            onMore = { actions.open(server.id) },
                        )
                    }
                }

                if (empty != null) {
                    item {
                        Text(
                            stringResource(emptyText(empty)),
                            Modifier.fillMaxWidth().padding(horizontal = PgSpace.Xxs, vertical = 20.dp).testTag("servers.empty"),
                            color = PgColor.Text.Muted,
                            style = PgType.Body,
                            textAlign = TextAlign.Center,
                        )
                    }
                }

                item {
                    Text(
                        stringResource(R.string.servers_note),
                        Modifier.padding(start = PgSpace.Xxs, top = PgSpace.Xs, end = PgSpace.Xxs),
                        color = PgColor.Text.Muted,
                        style = PgType.Caption.copy(lineHeight = PgType.Body.lineHeight),
                    )
                }
            }
        }

        if (filterOpen) {
            BackHandler { filterOpen = false }
            // Нажатие мимо панели закрывает ее, как выпадающий список; само нажатие дальше не идет (FilterDismiss десктопа).
            Box(
                Modifier
                    .fillMaxSize()
                    .pointerInput(Unit) { detectTapGestures { filterOpen = false } }
                    .testTag("servers.filter.outside"),
            )
            FilterPanel(
                ui.servers,
                query,
                actions,
                onDone = { filterOpen = false },
                modifier = Modifier.padding(start = PgSpace.WindowSideMobile, top = HeaderHeight + SearchHeight + FilterGap, end = PgSpace.WindowSideMobile),
            )
        }
    }
}

private fun emptyText(empty: ServerListEmpty): Int = when (empty) {
    ServerListEmpty.NoServers -> R.string.servers_none
    ServerListEmpty.NoFavorites -> R.string.servers_empty_favorites
    ServerListEmpty.NoRecent -> R.string.servers_empty_recent
    ServerListEmpty.NothingFound -> R.string.servers_empty
}

/** Шапка: «Серверы», «Обновить серверы» (вид не меняется, во время замера крутится только стрелка) и «+». */
@Composable
private fun Header(measuring: Boolean, actions: ServersActions) {
    Row(
        Modifier.fillMaxWidth().height(HeaderHeight).padding(start = PgSpace.WindowSideMobile, end = PgSpace.L),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(stringResource(R.string.nav_servers), style = PgType.H1, modifier = Modifier.weight(1f))
        // Кнопка не гаснет и не сжимается: повтор во время замера модель не считает (MeasureResult.Running).
        HeaderButton(PgIcons.Refresh, stringResource(R.string.server_refresh), "servers.refresh", actions.refresh, 20.dp, 1.9f, steady = true, spinning = measuring)
        Spacer(Modifier.width(2.dp))
        HeaderButton(PgIcons.Plus, stringResource(R.string.header_add), "servers.add", actions.add, 22.dp, 2f)
    }
}

/**
 * Кнопка-значок шапки (.btn-icon, 44): при нажатии значок синеет и кнопка сжимается до 0,98; [steady] — не сжимается.
 * [spinning] — значок крутится, 0,9 с на оборот (Classes.spin десктопа).
 */
@Composable
private fun HeaderButton(
    icon: String,
    label: String,
    tag: String,
    onClick: () -> Unit,
    size: Dp,
    stroke: Float,
    steady: Boolean = false,
    spinning: Boolean = false,
) {
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    Box(
        Modifier
            .size(PgLayout.TouchTarget)
            .scale(if (pressed && !steady) 0.98f else 1f)
            .clip(RoundedCornerShape(PgRadius.Button))
            .clickable(interactionSource = interaction, indication = null, onClickLabel = label, role = Role.Button, onClick = onClick)
            .semantics { contentDescription = label }
            .testTag(tag),
        contentAlignment = Alignment.Center,
    ) {
        val color = if (pressed) PgColor.Accent.Base else PgColor.Text.Primary
        if (spinning) {
            val spin = rememberInfiniteTransition(label = "refresh")
            val angle by spin.animateFloat(0f, 360f, infiniteRepeatable(tween(900, easing = LinearEasing)), label = "angle")
            PgIcon(icon, null, Modifier.graphicsLayer { rotationZ = angle }.testTag("$tag.spin"), color = color, size = size, strokeWidth = stroke)
        } else {
            PgIcon(icon, null, color = color, size = size, strokeWidth = stroke)
        }
    }
}

/**
 * Поиск (.sv-search): поле 46 на фоне surface.card, внутри справа — кнопка фильтра 38 с числом отличий от обычного; пока
 * панель фильтра открыта — рамка поля accent.base. Ищется название, код страны и протокол — адрес запечатан (решение 420),
 * поэтому подсказка поля — своя, а не «Страна, город, IP» макета.
 */
@Composable
private fun SearchField(search: String, filterCount: Int, filterOpen: Boolean, onSearch: (String) -> Unit, onFilter: () -> Unit) {
    val shape = RoundedCornerShape(PgRadius.Button)
    val label = stringResource(R.string.servers_search_label)
    Row(
        Modifier
            .fillMaxWidth()
            .height(SearchHeight)
            .clip(shape)
            .background(PgColor.Surface.Card)
            .border(1.dp, if (filterOpen) PgColor.Accent.Base else PgColor.Line.Border, shape)
            .padding(start = 14.dp, end = PgSpace.Xxs)
            .testTag("servers.searchField"),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        PgIcon(PgIcons.Search, null, color = PgColor.Text.Muted, size = 17.dp, strokeWidth = 2f)
        BasicTextField(
            value = search,
            onValueChange = onSearch,
            singleLine = true,
            textStyle = PgType.Body.copy(color = PgColor.Text.Primary),
            cursorBrush = SolidColor(PgColor.Accent.Base),
            modifier = Modifier.weight(1f).padding(horizontal = PgSpace.S).semantics { contentDescription = label }.testTag("servers.search"),
            decorationBox = { field ->
                Box {
                    if (search.isEmpty()) {
                        Text(
                            stringResource(R.string.android_servers_search_placeholder),
                            color = PgColor.Text.Muted,
                            style = PgType.Body,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                        )
                    }

                    field()
                }
            },
        )
        FilterButton(filterCount, filterOpen, onFilter)
    }
}

/** Кнопка фильтра (Button.filter): 38, скругление radius.input; открыта панель или что-то изменено — accent.tint и число. */
@Composable
private fun FilterButton(filterCount: Int, filterOpen: Boolean, onClick: () -> Unit) {
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    val on = filterOpen || filterCount > 0
    val filter = stringResource(R.string.servers_filter)
    Box(
        Modifier
            .size(38.dp)
            .scale(if (pressed) 0.98f else 1f)
            .clip(RoundedCornerShape(PgRadius.Input))
            .background(if (on) PgColor.Accent.Tint else Color.Transparent)
            .semantics { selected = filterOpen }
            .clickable(interactionSource = interaction, indication = null, onClickLabel = filter, role = Role.Button, onClick = onClick)
            .testTag("servers.filter"),
        contentAlignment = Alignment.Center,
    ) {
        val color = when {
            on -> PgColor.Accent.Text
            pressed -> PgColor.Accent.Base
            else -> PgColor.Text.Secondary
        }
        PgIcon(PgIcons.Filter, filter, color = color, size = 19.dp, strokeWidth = 1.9f)
        if (filterCount > 0) {
            // Число — плашка 16 в правом верхнем углу кнопки (FilterCount десктопа).
            Box(
                Modifier
                    .align(Alignment.TopEnd)
                    .padding(top = 3.dp, end = 3.dp)
                    .widthIn(min = 16.dp)
                    .heightIn(min = 16.dp)
                    .clip(RoundedCornerShape(8.dp))
                    .background(PgColor.Accent.Base)
                    .padding(horizontal = PgSpace.Xxs),
                contentAlignment = Alignment.Center,
            ) {
                Text(
                    filterCount.toString(),
                    Modifier.testTag("servers.filter.count"),
                    color = PgColor.Surface.Solid,
                    style = FilterCountText,
                    textAlign = TextAlign.Center,
                )
            }
        }
    }
}

/** Число на кнопке фильтра: overline, жирным. */
private val FilterCountText = TextStyle(fontSize = PgType.Overline.fontSize, fontWeight = FontWeight.Bold)

/**
 * Фильтр и сортировка (.sv-pop, панель десктопа): протоколы, что есть в списке, — флажками в две колонки; порядок —
 * сегментами «По пингу | По названию»; слева «Сбросить», справа «Готово». Нажатия на саму панель ее не закрывают.
 */
@Composable
private fun FilterPanel(servers: List<ServerItem>, query: ServerListQuery, actions: ServersActions, onDone: () -> Unit, modifier: Modifier) {
    val shape = RoundedCornerShape(PgRadius.Card)
    Column(
        modifier
            .fillMaxWidth()
            .pgShadow(PgShadow.Popup, shape)
            .clip(shape)
            .background(PgColor.Surface.Solid)
            .border(1.dp, PgColor.Line.Border, shape)
            .pointerInput(Unit) { detectTapGestures { } }
            .padding(14.dp)
            .testTag("servers.filter.panel"),
        verticalArrangement = Arrangement.spacedBy(PgSpace.L),
    ) {
        val protocols = ServerList.protocolsIn(servers)
        // Без серверов протоколов нет — нет и заголовка над пустым местом.
        if (protocols.isNotEmpty()) {
            Overline(stringResource(R.string.servers_filter_protocols))
            Column(verticalArrangement = Arrangement.spacedBy(2.dp)) {
                for (pair in protocols.chunked(2)) {
                    Row(horizontalArrangement = Arrangement.spacedBy(PgSpace.S)) {
                        for (protocol in pair) {
                            PgCheck(
                                ServerFacts.protocolName(protocol),
                                protocol !in query.hiddenProtocols,
                                "filter.${protocol.name}",
                                { actions.toggleProtocol(protocol) },
                                Modifier.weight(1f),
                            )
                        }

                        if (pair.size == 1) {
                            Spacer(Modifier.weight(1f))
                        }
                    }
                }
            }
        }

        Overline(stringResource(R.string.servers_filter_sort))
        PgSegments(
            listOf(ServerSort.Latency, ServerSort.Name),
            query.sort,
            { sort ->
                when (sort) {
                    ServerSort.Latency -> stringResource(R.string.servers_sort_latency)
                    ServerSort.Name -> stringResource(R.string.servers_sort_name)
                }
            },
            "sort",
            actions.sort,
        )
        Row(verticalAlignment = Alignment.CenterVertically) {
            PgSecondaryButton(stringResource(R.string.servers_filter_reset), "filter.reset", actions.reset)
            Spacer(Modifier.weight(1f))
            PgPrimaryButton(stringResource(R.string.servers_filter_done), "filter.done", onDone)
        }
    }
}

/** Вкладки — сегменты (RadioButton.segment): «Все · N», «Избранные», «Недавние». */
@Composable
private fun Tabs(current: ServerListTab, total: Int, onTab: (ServerListTab) -> Unit) {
    val tabs = stringResource(R.string.servers_tabs)
    PgSegments(
        ServerListTab.entries,
        current,
        { tab ->
            when (tab) {
                ServerListTab.All -> stringResource(R.string.servers_tab_all, total.toString())
                ServerListTab.Favorites -> stringResource(R.string.servers_tab_favorites)
                ServerListTab.Recent -> stringResource(R.string.servers_tab_recent)
            }
        },
        "tab",
        onTab,
        Modifier.semantics { contentDescription = tabs },
    )
}

/**
 * Заголовок группы: «Подписка · 7» и «обновлено 14:20», «Мои серверы · 2», «Избранные · 3», «Недавние · 4». Между группами —
 * 12, как у десктопа: 6 промежутка списка и 6 над заголовком (у первой группы — нет).
 */
@Composable
private fun GroupHeader(group: ServerGroup, tab: ServerListTab, first: Boolean) {
    val count = group.servers.size.toString()
    val subscription = group.subscription
    val title = when {
        subscription != null -> stringResource(R.string.servers_group_subscription, subscription.name, count)
        tab == ServerListTab.Favorites -> stringResource(R.string.servers_group_favorites, count)
        tab == ServerListTab.Recent -> stringResource(R.string.servers_group_recent, count)
        else -> stringResource(R.string.servers_group_manual, count)
    }

    Row(
        Modifier
            .fillMaxWidth()
            .padding(start = PgSpace.Xxs, top = if (first) 0.dp else PgSpace.Xs, end = PgSpace.Xxs, bottom = 2.dp)
            .testTag("group.${subscription?.id ?: tab.name}"),
        verticalAlignment = Alignment.Bottom,
        horizontalArrangement = Arrangement.spacedBy(PgSpace.S),
    ) {
        Overline(title, Modifier.weight(1f))
        if (subscription != null) {
            val updated = subscription.updatedAt?.let { stringResource(R.string.servers_updated, clock(it)) } ?: stringResource(R.string.servers_updated_never)
            Text(updated, color = PgColor.Text.Muted, style = PgType.Caption)
        }
    }
}

/** «14:20» — время по часам телефона. */
private fun clock(at: Long): String = DateTimeFormatter.ofPattern("HH:mm").withZone(ZoneId.systemDefault()).format(Instant.ofEpochMilli(at))

/** Вторая строка сервера (.sv-addr): моноширинным размера overline. */
private val RowMeta = PgType.Mono.copy(fontSize = PgType.Overline.fontSize, fontWeight = FontWeight.Normal)

/** Пинг строки (TextBlock.latency): моноширинным размера caption, полужирным, по правому краю. */
private val RowLatency = PgType.Mono.copy(fontSize = PgType.Caption.fontSize, fontWeight = FontWeight.SemiBold, textAlign = TextAlign.End)

/** Метка строки (.sv-tag): overline, жирным. */
private val TagText = TextStyle(fontSize = PgType.Overline.fontSize, fontWeight = FontWeight.Bold)

/**
 * Строка сервера (Border.serverRow): рамка 1,5 line.border на surface.card; выбранная — белая, с рамкой status.success.base и
 * тенью selected. Значок страны 40 × 28, название и протокол второй строкой (адрес запечатан — решение 399), метка
 * «активен» (защита идет через сервер) или «выбран» — зеленая плашка перед пингом, «⋯» — карточка. Звезды и пометки
 * «пропал из подписки» в строке нет — они в карточке.
 */
@Composable
private fun ServerRow(server: ServerItem, selected: Boolean, connected: Boolean, onSelect: () -> Unit, onMore: () -> Unit) {
    val tag = when {
        connected -> R.string.servers_tag_active
        selected -> R.string.servers_tag_selected
        else -> null
    }
    val marked = tag != null
    val shape = RoundedCornerShape(PgRadius.Button)
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    Box(Modifier.fillMaxWidth()) {
        Row(
            Modifier
                .fillMaxWidth()
                .heightIn(min = 58.dp)
                .then(if (marked) Modifier.pgShadow(PgShadow.Selected, shape) else Modifier)
                .clip(shape)
                .background(if (marked) PgColor.Surface.Solid else PgColor.Surface.Card)
                .border(1.5.dp, if (marked) PgColor.Status.Success.Base else PgColor.Line.Border, shape)
                .semantics { this.selected = selected }
                .clickable(interactionSource = interaction, indication = null, role = Role.RadioButton, onClick = onSelect)
                .padding(start = PgSpace.L, top = PgSpace.S, end = 44.dp, bottom = PgSpace.S)
                .testTag("server.${server.id}"),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            CountryCode(server.countryCode, large = false)
            Column(Modifier.weight(1f).padding(start = PgSpace.L, end = PgSpace.S), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                Text(
                    server.displayName,
                    color = if (pressed) PgColor.Accent.Base else PgColor.Text.Primary,
                    style = PgType.BodyStrong,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
                Text(
                    server.summary,
                    Modifier.testTag("server.meta.${server.id}"),
                    color = PgColor.Text.Muted,
                    style = RowMeta,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
            }

            if (tag != null) {
                Text(
                    stringResource(tag),
                    Modifier
                        .padding(end = PgSpace.M)
                        .clip(RoundedCornerShape(PgRadius.Chip))
                        .background(PgColor.Status.Success.Tint)
                        .padding(horizontal = 7.dp, vertical = 3.dp)
                        .testTag("server.tag.${server.id}"),
                    color = PgColor.Status.Success.Badge,
                    style = TagText,
                    maxLines = 1,
                )
            }

            LatencyText(server, Modifier.widthIn(min = 52.dp), RowLatency)
        }

        MoreButton(server.displayName, Modifier.align(Alignment.CenterEnd).padding(end = PgSpace.Xs).testTag("more.${server.id}"), onMore)
    }
}

/** «⋯» строки: 34, скругление radius.input, серый значок; при нажатии — синий. */
@Composable
private fun MoreButton(name: String, modifier: Modifier, onClick: () -> Unit) {
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    val more = stringResource(R.string.servers_more_name, name)
    Box(
        modifier
            .size(34.dp)
            .clip(RoundedCornerShape(PgRadius.Input))
            .clickable(interactionSource = interaction, indication = null, onClickLabel = more, role = Role.Button, onClick = onClick),
        contentAlignment = Alignment.Center,
    ) {
        PgIcon(PgIcons.More, more, color = if (pressed) PgColor.Accent.Base else PgColor.Text.Muted, size = 18.dp, strokeWidth = 3f)
    }
}

/**
 * Значок страны: две буквы кода (цветные флаги шрифт рисует не везде) на accent.tint с рамкой accent.line. В строке —
 * 40 × 28 со скруглением radius.chip, в карточке ([large]) — 52 × 38 со скруглением radius.input. Кода нет — пустой.
 */
@Composable
internal fun CountryCode(code: String, large: Boolean) {
    val shape = RoundedCornerShape(if (large) PgRadius.Input else PgRadius.Chip)
    Box(
        Modifier
            .size(if (large) 52.dp else 40.dp, if (large) 38.dp else 28.dp)
            .clip(shape)
            .background(PgColor.Accent.Tint)
            .border(1.dp, PgColor.Accent.Line, shape),
        contentAlignment = Alignment.Center,
    ) {
        if (code.isNotEmpty()) {
            val style = if (large) PgType.Title else PgType.Caption
            Text(code, color = PgColor.Accent.Text, style = style.copy(fontWeight = FontWeight.Bold), maxLines = 1)
        }
    }
}

/**
 * Задержка: быстрый — зеленым, медленный — серым, «нет» — оранжевым (сервер не ответил), «—» — еще не мерили (на телефоне
 * задержка меряется только по кнопке и только без защиты — решение 430, поэтому «не мерили» здесь бывает часто).
 */
@Composable
fun LatencyText(server: ServerItem, modifier: Modifier = Modifier, style: TextStyle = PgType.Mono) {
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

    Text(text, color = color, style = style, maxLines = 1, modifier = modifier.semantics { contentDescription = hint }.testTag("latency.${server.id}"))
}

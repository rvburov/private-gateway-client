package com.privategateway.client.ui.check

import androidx.activity.compose.BackHandler
import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.ScrollState
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
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
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.scale
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.layout.onGloballyPositioned
import androidx.compose.ui.layout.positionInParent
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.unit.dp
import com.privategateway.client.R
import com.privategateway.client.core.settings.TunnelSettings
import com.privategateway.client.core.vpn.TunnelState
import com.privategateway.client.inspect.CheckUi
import com.privategateway.client.inspect.InspectorUi
import com.privategateway.client.ui.ChipLook
import com.privategateway.client.ui.MainUi
import com.privategateway.client.ui.Texts
import com.privategateway.client.ui.theme.Overline
import com.privategateway.client.ui.theme.PgCard
import com.privategateway.client.ui.theme.PgColor
import com.privategateway.client.ui.theme.PgIcon
import com.privategateway.client.ui.theme.PgIcons
import com.privategateway.client.ui.theme.PgLayout
import com.privategateway.client.ui.theme.PgLinkRow
import com.privategateway.client.ui.theme.PgRadius
import com.privategateway.client.ui.theme.PgShadow
import com.privategateway.client.ui.theme.PgSpace
import com.privategateway.client.ui.theme.PgType
import com.privategateway.client.ui.theme.pgShadow
import com.privategateway.client.ui.theme.pgTopDivider
import com.privategateway.client.vpn.JournalEntry
import com.privategateway.client.vpn.JournalLevel
import kotlin.math.roundToInt

/** Подстраницы «Проверки»: строки инспектора ведут на свои экраны (на телефоне — по одному на экран). */
enum class CheckPage {
    Main,
    Network,
    Probe,
}

/** Что делают кнопки «Проверки». */
class CheckActions(
    /** Экран открыт или сменилось состояние защиты: проверить сразу — внешний IP, что мешает, задержка. */
    val open: () -> Unit = {},
    /** «Проверить»: то же заново; во время проверки — еще раз сразу после нее. */
    val run: () -> Unit = {},
    /** «Экспорт отчета»: системное окно «Сохранить» и обезличенный отчет в выбранный файл. */
    val export: () -> Unit = {},
    /** «Обновить» у «Задержки». */
    val measureDelay: () -> Unit = {},
    /** Экран прокрутил к «Задержке» по плитке «Пинг». */
    val delayShown: () -> Unit = {},
)

/** Поля карточек «Проверки» (Border.card Padding="16,4"): строки сами без боковых полей, полосы — внутри полей карточки. */
private val CardPadding = PaddingValues(horizontal = PgSpace.Card, vertical = PgSpace.Xxs)

/** Строки-ссылки инспектора (.links-card): плитка 36 со значком 20, 10 сверху и снизу. */
private val LinkPadding = PaddingValues(vertical = 10.dp)

/** Чип строки (.check-chip): размер overline, полужирный, без заглавных и разрядки. */
private val ChipText = PgType.Caption.copy(fontSize = PgType.Overline.fontSize, fontWeight = FontWeight.SemiBold)

/** Надпись-кнопка (.btn-link): small, полужирный. */
private val LinkText = PgType.Small.copy(fontWeight = FontWeight.SemiBold)

/** Число «Задержки»: t-title моноширинным. */
private val DelayValue = PgType.Title.copy(fontFamily = PgType.Mono.fontFamily)

/** Строка журнала: моноширинный размера overline (11). */
private val JournalStyle = PgType.Mono.copy(fontSize = PgType.Overline.fontSize, fontWeight = FontWeight.Normal)

/** Сколько последних записей журнала на экране — как JournalShown десктопа. */
private const val JOURNAL_SHOWN = 50

/**
 * «Проверка» (доска 7, CheckPage десктопа): шапка стоит на месте, прокручивается содержимое. «Проверить» и «Экспорт отчета»,
 * пояснение, строки проверок (CheckRows), «Задержка», «Что мешает», инспектор строками-ссылками на «Сеть телефона» и
 * «Проверку узла» и журнал туннеля — новые записи сверху, секреты скрыты. Проверки только читают состояние: при открытии
 * экрана и при смене состояния защиты они идут сами ([CheckActions.open]). Ошибки отдельной карточкой нет: ее показывает
 * строка «Сетевое ядро» («Остановлено из-за ошибки» и что значит код) и запись ERR в журнале.
 */
@Composable
fun CheckScreen(
    ui: MainUi,
    core: String?,
    loadCore: () -> Unit,
    check: CheckUi = CheckUi(),
    actions: CheckActions = CheckActions(),
    inspector: InspectorUi = InspectorUi(),
    inspectorActions: InspectorActions = InspectorActions(),
    tunnel: TunnelSettings = TunnelSettings(),
) {
    LaunchedEffect(Unit) { loadCore() }
    val open by rememberUpdatedState(actions.open)
    LaunchedEffect(ui.status.state) { open() }
    var page by rememberSaveable { mutableStateOf(CheckPage.Main) }
    // Прокрутка «Проверки» переживает подстраницу: «Назад» возвращает на то же место.
    val scroll = rememberScrollState()
    when (page) {
        CheckPage.Network -> {
            BackHandler { page = CheckPage.Main }
            NetworkPage(inspector, inspectorActions) { page = CheckPage.Main }
        }

        CheckPage.Probe -> {
            BackHandler { page = CheckPage.Main }
            ProbePage(inspector.probe, ui.status.state == TunnelState.Connected, inspectorActions) { page = CheckPage.Main }
        }

        CheckPage.Main -> CheckMain(ui, core, check, actions, tunnel, scroll) { page = it }
    }
}

@Composable
private fun CheckMain(
    ui: MainUi,
    core: String?,
    check: CheckUi,
    actions: CheckActions,
    tunnel: TunnelSettings,
    scroll: ScrollState,
    onPage: (CheckPage) -> Unit,
) {
    var delayTop by remember { mutableIntStateOf(-1) }
    val delayShown by rememberUpdatedState(actions.delayShown)
    // Открыто плиткой «Пинг»: прокрутить к «Задержке», когда ее место известно.
    LaunchedEffect(check.focusDelay, delayTop) {
        if (check.focusDelay && delayTop >= 0) {
            scroll.animateScrollTo(delayTop)
            delayShown()
        }
    }

    Column(Modifier.fillMaxSize().statusBarsPadding().testTag("check")) {
        // Шапка 60 стоит на месте (Grid RowDefinitions="Auto,*" десктопа), прокручивается только содержимое.
        Box(Modifier.fillMaxWidth().height(60.dp).padding(horizontal = PgSpace.WindowSideMobile), contentAlignment = Alignment.CenterStart) {
            Text(stringResource(R.string.page_check_title), style = PgType.H1, modifier = Modifier.testTag("check.title"))
        }

        Column(
            Modifier
                .fillMaxWidth()
                .weight(1f)
                .verticalScroll(scroll)
                .padding(start = PgSpace.WindowSideMobile, end = PgSpace.WindowSideMobile, bottom = PgLayout.ScrollBottomPadding),
            verticalArrangement = Arrangement.spacedBy(PgSpace.L),
        ) {
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(PgSpace.S)) {
                RunButton(check.running, actions.run, Modifier.weight(1f))
                ExportButton(actions.export, Modifier.weight(1f))
            }

            Text(
                stringResource(R.string.diag_note),
                Modifier.padding(horizontal = PgSpace.Xs).testTag("check.note"),
                color = PgColor.Text.Muted,
                style = PgType.Caption,
            )
            Results(CheckRows.of(ui, core, check.hints, tunnel, check.running))
            Delay(ui, check, actions, Modifier.onGloballyPositioned { delayTop = it.positionInParent().y.roundToInt() })
            Hindrances(HinderRows.of(check.hints))
            InspectorLinks(onPage)
            Journal(check.journal)
        }
    }
}

/** «Проверить» (.btn-primary высотой 46 с тенью accent): стрелка крутится, пока идет проверка; нажатие и тогда — еще раз. */
@Composable
private fun RunButton(running: Boolean, onClick: () -> Unit, modifier: Modifier) {
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    val shape = RoundedCornerShape(PgRadius.Button)
    Row(
        modifier
            .scale(if (pressed) 0.98f else 1f)
            .height(46.dp)
            .pgShadow(PgShadow.Accent, shape)
            .clip(shape)
            .background(if (pressed) PgColor.Accent.Text else PgColor.Accent.Base)
            .clickable(interactionSource = interaction, indication = null, role = Role.Button, onClick = onClick)
            .padding(horizontal = PgSpace.M)
            .testTag("check.run"),
        horizontalArrangement = Arrangement.Center,
        verticalAlignment = Alignment.CenterVertically,
    ) {
        if (running) {
            val spin = rememberInfiniteTransition(label = "check")
            val angle by spin.animateFloat(0f, 360f, infiniteRepeatable(tween(900, easing = LinearEasing)), label = "angle")
            PgIcon(PgIcons.Refresh, null, Modifier.graphicsLayer { rotationZ = angle }.testTag("check.run.spin"), color = PgColor.Surface.Solid, size = 18.dp, strokeWidth = 2.2f)
        } else {
            PgIcon(PgIcons.Refresh, null, color = PgColor.Surface.Solid, size = 18.dp, strokeWidth = 2.2f)
        }

        Spacer(Modifier.width(PgSpace.S))
        Text(
            stringResource(if (running) R.string.diag_running else R.string.diag_run),
            color = PgColor.Surface.Solid,
            style = PgType.BodyStrong,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
    }
}

/** «Экспорт отчета» (.btn-secondary высотой 46, 500): белая с рамкой line.strong, при нажатии рамка и текст синеют. */
@Composable
private fun ExportButton(onClick: () -> Unit, modifier: Modifier) {
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    val shape = RoundedCornerShape(PgRadius.Button)
    Box(
        modifier
            .scale(if (pressed) 0.98f else 1f)
            .height(46.dp)
            .clip(shape)
            .background(PgColor.Surface.Solid)
            .border(1.dp, if (pressed) PgColor.Accent.Base else PgColor.Line.Strong, shape)
            .clickable(interactionSource = interaction, indication = null, role = Role.Button, onClick = onClick)
            .padding(horizontal = PgSpace.M)
            .testTag("check.export"),
        contentAlignment = Alignment.Center,
    ) {
        Text(
            stringResource(R.string.diag_export),
            color = if (pressed) PgColor.Accent.Base else PgColor.Text.Primary,
            style = PgType.Body.copy(fontWeight = FontWeight.Medium),
            textAlign = TextAlign.Center,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
    }
}

/** Рубрика над карточкой и сама карточка: рубрика на 6 ниже предыдущего блока и на 8 выше карточки (Margin="6,6,6,-4"). */
@Composable
private fun Section(modifier: Modifier = Modifier, header: @Composable () -> Unit, content: @Composable () -> Unit) {
    Column(modifier.fillMaxWidth().padding(top = PgSpace.Xs), verticalArrangement = Arrangement.spacedBy(PgSpace.S)) {
        Box(Modifier.fillMaxWidth().padding(horizontal = PgSpace.Xs)) {
            header()
        }

        content()
    }
}

@Composable
private fun Results(rows: List<CheckRow>) {
    PgCard(Modifier.fillMaxWidth().testTag("check.results"), contentPadding = CardPadding) {
        rows.forEachIndexed { index, row ->
            CheckLine(
                row.look,
                stringResource(row.title),
                row.detail.map { it.resolve() }.joinToString(" · "),
                row.chip.resolve(),
                "check.row.${row.id}",
                divider = index > 0,
                top = false,
            )
        }
    }
}

/**
 * Строка проверки (.check-row): кружок 22 со значком состояния, заголовок и пояснение столбцом, справа — чип на фоне tint
 * цветом состояния; между строками — полоса line.divider. [top] — кружок и чип по верху (длинные строки «Что мешает»).
 */
@Composable
private fun CheckLine(look: ChipLook, title: String, detail: String, chip: String, tag: String, divider: Boolean, top: Boolean) {
    Row(
        Modifier
            .fillMaxWidth()
            .pgTopDivider(divider)
            .padding(vertical = 11.dp)
            .testTag(tag),
        verticalAlignment = if (top) Alignment.Top else Alignment.CenterVertically,
    ) {
        Box(
            Modifier
                .padding(top = if (top) 1.dp else 0.dp)
                .size(22.dp)
                .clip(CircleShape)
                .background(look.base),
            contentAlignment = Alignment.Center,
        ) {
            PgIcon(look.mark, null, color = PgColor.Surface.Solid, size = 12.dp, strokeWidth = 3.2f)
        }

        Column(Modifier.weight(1f).padding(horizontal = PgSpace.L), verticalArrangement = Arrangement.spacedBy(2.dp)) {
            Text(
                title,
                Modifier.testTag("$tag.title"),
                color = PgColor.Text.Primary,
                style = PgType.BodyStrong,
                maxLines = if (top) Int.MAX_VALUE else 1,
                overflow = TextOverflow.Ellipsis,
            )
            if (detail.isNotEmpty()) {
                Text(detail, Modifier.testTag("$tag.detail"), color = PgColor.Text.Muted, style = PgType.Caption)
            }
        }

        Text(
            chip,
            Modifier
                .clip(RoundedCornerShape(10.dp))
                .background(look.tint)
                .padding(horizontal = 9.dp, vertical = 3.dp)
                .testTag("$tag.chip"),
            color = look.text,
            style = ChipText,
            maxLines = 1,
        )
    }
}

/**
 * «Задержка» (решение 509): рубрика с «Обновить», три строки — до сервера, через сервер, новое соединение — с числом
 * моноширинным, «Откуда … мс», «Обновлено …». Во время замера — «—» и «Меряем…», без защиты — «—» и «Подключитесь…».
 */
@Composable
private fun Delay(ui: MainUi, check: CheckUi, actions: CheckActions, modifier: Modifier = Modifier) {
    val language = Texts.language(LocalConfiguration.current.locales)
    Section(
        header = {
            Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                Overline(stringResource(R.string.diag_delay_title), Modifier.weight(1f))
                LinkButton(stringResource(R.string.diag_delay_refresh), "check.delay.refresh", !check.measuringDelay, actions.measureDelay)
            }
        },
        modifier = modifier.testTag("check.delay"),
    ) {
        PgCard(Modifier.fillMaxWidth(), contentPadding = CardPadding) {
            DelayRows.of(ui, check).forEachIndexed { index, row ->
                Row(
                    Modifier
                        .fillMaxWidth()
                        .pgTopDivider(index > 0)
                        .padding(vertical = 11.dp)
                        .testTag("check.delay.$index"),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Column(Modifier.weight(1f).padding(end = PgSpace.L), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                        Text(stringResource(row.title), color = PgColor.Text.Primary, style = PgType.BodyStrong)
                        Text(stringResource(row.text), color = PgColor.Text.Muted, style = PgType.Caption)
                    }

                    Text(
                        row.value?.let { stringResource(R.string.diag_delay_ms, it.toString()) } ?: "—",
                        Modifier.testTag("check.delay.$index.value"),
                        color = PgColor.Text.Primary,
                        style = DelayValue,
                    )
                }
            }

            val note = DelayRows.hint(ui, check)?.let { stringResource(it) } ?: DelayRows.why(ui, check, language)?.resolve()
            if (note != null) {
                Text(
                    note,
                    Modifier.fillMaxWidth().pgTopDivider(true).padding(top = 10.dp, bottom = PgSpace.Xxs).testTag("check.delay.note"),
                    color = PgColor.Text.Muted,
                    style = PgType.Caption,
                )
            }

            val shown = DelayRows.shown(ui, check)
            Text(
                shown?.let { stringResource(R.string.diag_delay_updated, DelayRows.updated(it.measuredAt)) } ?: "",
                Modifier
                    .fillMaxWidth()
                    .pgTopDivider(note == null)
                    .padding(top = PgSpace.Xxs, bottom = 10.dp)
                    .testTag("check.delay.updated"),
                color = PgColor.Text.Muted,
                style = PgType.Caption,
            )
        }
    }
}

/** Надпись-кнопка (.btn-link): accent.text, при нажатии сжимается до 0,98, без заливки; недоступная — серая. */
@Composable
private fun LinkButton(text: String, tag: String, enabled: Boolean, onClick: () -> Unit) {
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    Text(
        text,
        Modifier
            .scale(if (pressed && enabled) 0.98f else 1f)
            .alpha(if (enabled) 1f else 0.6f)
            .clip(RoundedCornerShape(PgRadius.Input))
            .clickable(interactionSource = interaction, indication = null, enabled = enabled, role = Role.Button, onClick = onClick)
            .padding(vertical = PgSpace.Xxs)
            .testTag(tag),
        color = if (enabled) PgColor.Accent.Text else PgColor.Text.Muted,
        style = LinkText,
    )
}

/** «Что мешает» (решение 508): то, что Android показывает приложению; ничего — «Ничего не найдено». */
@Composable
private fun Hindrances(rows: List<HinderRow>) {
    Section(header = { Overline(stringResource(R.string.diag_hinder_title)) }, modifier = Modifier.testTag("check.hinder")) {
        PgCard(Modifier.fillMaxWidth(), contentPadding = CardPadding) {
            rows.forEachIndexed { index, row ->
                CheckLine(
                    row.look,
                    row.title.resolve(),
                    row.detail.map { it.resolve() }.joinToString(" "),
                    row.chip.resolve(),
                    "check.hinder.${row.id}",
                    divider = index > 0,
                    top = true,
                )
            }
        }
    }
}

/**
 * Сетевой инспектор строками-ссылками: «Сеть телефона» и «Проверка узла» (замеры DNS, TCP и TLS: пинга и пути у телефона нет —
 * ICMP приложениям Android недоступен). «Соединений и трафика», «До и после VPN» и «Лаборатории» у телефона нет: соединений
 * других приложений Android не показывает, а пустых страниц экран не рисует.
 */
@Composable
private fun InspectorLinks(onPage: (CheckPage) -> Unit) {
    val network = listOf(R.string.network_ipv4, R.string.network_ipv6, R.string.network_gateway, R.string.network_dns).map { stringResource(it) }.joinToString(" · ")
    val probe = ProbeKinds.map { stringResource(probeKindLabel(it)) }.joinToString(" · ")
    PgCard(Modifier.fillMaxWidth().testTag("check.inspector"), contentPadding = CardPadding) {
        PgLinkRow(
            PgIcons.NetEthernet,
            stringResource(R.string.android_inspect_title),
            network,
            "check.network",
            { onPage(CheckPage.Network) },
            padding = LinkPadding,
            tile = 36.dp,
            iconSize = 20.dp,
        )
        PgLinkRow(
            PgIcons.Pulse,
            stringResource(R.string.diag_probe_title),
            probe,
            "check.probe",
            { onPage(CheckPage.Probe) },
            divider = true,
            padding = LinkPadding,
            tile = 36.dp,
            iconSize = 20.dp,
        )
    }
}

/**
 * Журнал туннеля: рубрика «Журнал» и справа «секреты скрыты»; в карточке — последние записи, новые сверху, каждая в одну
 * строку с многоточием: время, уровень цветом (INF — акцентом, WRN — оранжевым, ERR — красным, DBG — серым) и текст.
 */
@Composable
private fun Journal(entries: List<JournalEntry>) {
    Section(
        header = {
            Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.Bottom) {
                Overline(stringResource(R.string.diag_journal), Modifier.weight(1f))
                Text(stringResource(R.string.diag_journal_masked), color = PgColor.Text.Muted, style = PgType.Caption)
            }
        },
    ) {
        PgCard(Modifier.fillMaxWidth().testTag("check.journal"), contentPadding = PaddingValues(horizontal = 14.dp, vertical = PgSpace.L)) {
            val shown = entries.takeLast(JOURNAL_SHOWN).asReversed()
            if (shown.isEmpty()) {
                Text(stringResource(R.string.diag_journal_empty), Modifier.testTag("check.journal.empty"), color = PgColor.Text.Muted, style = PgType.Caption)
            }

            shown.forEachIndexed { index, entry -> JournalLine(entry, "check.journal.$index") }
        }
    }
}

@Composable
private fun JournalLine(entry: JournalEntry, tag: String) {
    val level: Color = when (entry.level) {
        JournalLevel.Debug -> PgColor.Text.Muted
        JournalLevel.Info -> PgColor.Accent.Text
        JournalLevel.Warning -> PgColor.Status.Warning.Text
        JournalLevel.Error -> PgColor.Status.Danger.Text
    }
    val line = buildAnnotatedString {
        withStyle(SpanStyle(color = PgColor.Text.Muted)) { append(JournalText.time(entry.at)) }
        append("  ")
        withStyle(SpanStyle(color = level, fontWeight = FontWeight.SemiBold)) { append(JournalText.level(entry.level)) }
        append("  ")
        append(entry.message)
    }
    Text(line, Modifier.fillMaxWidth().testTag(tag), color = PgColor.Text.Primary, style = JournalStyle, maxLines = 1, overflow = TextOverflow.Ellipsis)
}

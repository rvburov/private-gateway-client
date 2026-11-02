package com.privategateway.client.ui.servers

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
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.scale
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.selected
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import com.privategateway.client.R
import com.privategateway.client.core.links.ImportBatch
import com.privategateway.client.core.servers.ProxyProtocol
import com.privategateway.client.core.servers.ServerFacts
import com.privategateway.client.core.servers.ServerList
import com.privategateway.client.ui.ImportSource
import com.privategateway.client.ui.ImportUi
import com.privategateway.client.ui.Message
import com.privategateway.client.ui.Texts
import com.privategateway.client.ui.theme.Overline
import com.privategateway.client.ui.theme.PgCard
import com.privategateway.client.ui.theme.PgColor
import com.privategateway.client.ui.theme.PgIcon
import com.privategateway.client.ui.theme.PgIcons
import com.privategateway.client.ui.theme.PgInput
import com.privategateway.client.ui.theme.PgLayout
import com.privategateway.client.ui.theme.PgMenu
import com.privategateway.client.ui.theme.PgMenuAction
import com.privategateway.client.ui.theme.PgRadius
import com.privategateway.client.ui.theme.PgSecondaryButton
import com.privategateway.client.ui.theme.PgShadow
import com.privategateway.client.ui.theme.PgSpace
import com.privategateway.client.ui.theme.PgType
import com.privategateway.client.ui.theme.pgShadow

/** Сколько строк с ошибками показывать по отдельности (ErrorLinesShown десктопа); остальные — одной строкой «И еще…». */
private const val SHOWN_ERRORS = 3

/** Название профиля — не длиннее 64 знаков, как у десктопа (MaxLength поля). */
private const val NAME_LIMIT = 64

/**
 * Что умеет «Добавить профиль»: выбрать источник ([source]), взять текст из буфера ([clipboard]), ввести и проверить ссылку,
 * открыть файл и картинку с QR-кодом (системные окна), назвать профиль и добавить. [scan] — камера; ее нет — null, и
 * карточка QR сразу открывает выбор картинки.
 */
class ImportActions(
    val source: (ImportSource) -> Unit,
    val clipboard: () -> Unit,
    val text: (String) -> Unit,
    val check: (String) -> Unit,
    val name: (String) -> Unit,
    val add: () -> Unit,
    val close: () -> Unit,
    val pickImage: () -> Unit,
    val pickFile: () -> Unit,
    val scan: (() -> Unit)? = null,
)

/**
 * «Добавить профиль» (доска 3, ImportPage десктопа): шапка с «Назад» стоит, прокручиваются карточки источников —
 * «Из буфера обмена», «QR-код» (камерой или с картинки), «Ввести ссылку» (поле и «Проверить» — только у выбранной), «Открыть
 * файл» — и предпросмотр: формат и протоколы, плитки «найдено», «новых», «дубли», «ошибки», строки с ошибками, название
 * профиля. Внизу, вне прокрутки, — «Добавить N серверов» по числу новых. Без названия серверы идут в «Мои серверы», адрес
 * подписки не хранится. Добавив, окно показывает итог и ведет на «Серверы» (AppScreen).
 */
@Composable
fun ImportScreen(state: ImportUi, actions: ImportActions) {
    val language = Texts.language(LocalConfiguration.current.locales)
    val batch = state.batch?.takeIf { it.found > 0 }
    Column(
        Modifier
            .fillMaxSize()
            .statusBarsPadding()
            .navigationBarsPadding()
            .imePadding()
            .testTag("import"),
    ) {
        Header(actions.close)
        Column(
            Modifier
                .fillMaxWidth()
                .weight(1f)
                .verticalScroll(rememberScrollState())
                .padding(start = PgSpace.WindowSideMobile, end = PgSpace.WindowSideMobile, bottom = PgSpace.Card),
            verticalArrangement = Arrangement.spacedBy(PgSpace.S),
        ) {
            SourceCard(
                PgIcons.Clipboard,
                stringResource(R.string.import_clipboard_title),
                stringResource(R.string.import_clipboard_text),
                state.source == ImportSource.Clipboard,
                "import.clipboard",
                actions.clipboard,
            )
            QrSource(state.source == ImportSource.Qr, actions)
            SourceCard(
                PgIcons.Link,
                stringResource(R.string.import_link_title),
                stringResource(R.string.import_link_text),
                state.source == ImportSource.Link,
                "import.link",
            ) {
                // Повторное нажатие на раскрытую карточку не стирает проверенное.
                if (state.source != ImportSource.Link) {
                    actions.source(ImportSource.Link)
                }
            }

            if (state.source == ImportSource.Link) {
                LinkBox(state, actions)
            }

            SourceCard(
                PgIcons.File,
                stringResource(R.string.import_file_title),
                stringResource(R.string.import_file_text),
                state.source == ImportSource.File,
                "import.file",
            ) {
                actions.source(ImportSource.File)
                actions.pickFile()
            }

            Messages(state)
            if (batch != null) {
                Preview(state, batch, language, actions.name)
            }
        }

        if (batch != null) {
            val fresh = state.fresh
            val label = when {
                fresh > 0 -> stringResource(Texts.importAdd(fresh, language), fresh.toString())
                state.renewals > 0 -> stringResource(Texts.importRenew(state.renewals, language), state.renewals.toString())
                else -> stringResource(R.string.import_nothing)
            }
            val enabled = (fresh > 0 || state.renewals > 0) && !state.adding && state.added == null
            AddButton(label, enabled, actions.add, Modifier.padding(start = PgSpace.WindowSideMobile, top = PgSpace.S, end = PgSpace.WindowSideMobile, bottom = 20.dp))
        }
    }
}

/**
 * Подсказка после «Добавить» — как у десктопа: обновлена уже добавленная подписка («обновлена: новых N, удалено M»),
 * добавлено N серверов, у N обновлена защита, иначе «Новых серверов нет». Еще не добавляли — null.
 */
fun importDone(state: ImportUi, language: String): Message? {
    val added = state.added ?: return null
    val existing = state.existing
    return when {
        existing != null -> Message(R.string.subs_updated, existing, added.toString(), state.removed.toString())
        added > 0 -> Message(Texts.imported(added, language), added.toString())
        state.renewals > 0 -> Message(Texts.importRenewed(state.renewals, language), state.renewals.toString())
        else -> Message(R.string.import_nothing)
    }
}

/** Шапка шага (.im-head, 60): «Назад» и «Добавить профиль»; островка на этом шаге нет. */
@Composable
private fun Header(onClose: () -> Unit) {
    Row(
        Modifier.fillMaxWidth().height(60.dp).padding(start = PgSpace.S, end = PgSpace.WindowSideMobile),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        val back = stringResource(R.string.common_back)
        val interaction = remember { MutableInteractionSource() }
        val pressed by interaction.collectIsPressedAsState()
        Box(
            Modifier
                .size(PgLayout.TouchTarget)
                .scale(if (pressed) 0.98f else 1f)
                .clip(RoundedCornerShape(PgRadius.Button))
                .clickable(interactionSource = interaction, indication = null, onClickLabel = back, role = Role.Button, onClick = onClose)
                .semantics { contentDescription = back }
                .testTag("import.back"),
            contentAlignment = Alignment.Center,
        ) {
            PgIcon(PgIcons.Back, null, color = if (pressed) PgColor.Accent.Base else PgColor.Text.Primary, size = 22.dp, strokeWidth = 2.2f)
        }

        Spacer(Modifier.width(PgSpace.Xxs))
        Text(stringResource(R.string.page_add_title), style = PgType.H1)
    }
}

/**
 * Карточка источника (Button.source): значок 22 на плитке 44 accent.tint, заголовок полужирным и подпись caption. Выбранная —
 * белая, рамка accent.base и синяя тень (shadow.chosen); при нажатии рамка accent.line, текст синеет, карточка сжимается до 0,99.
 */
@Composable
private fun SourceCard(icon: String, title: String, caption: String, selected: Boolean, tag: String, onClick: () -> Unit) {
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    val shape = RoundedCornerShape(16.dp)
    val edge = when {
        selected -> PgColor.Accent.Base
        pressed -> PgColor.Accent.Line
        else -> PgColor.Line.Border
    }
    Row(
        Modifier
            .fillMaxWidth()
            .scale(if (pressed) 0.99f else 1f)
            .then(if (selected) Modifier.pgShadow(PgShadow.Chosen, shape) else Modifier)
            .clip(shape)
            .background(if (selected) PgColor.Surface.Solid else PgColor.Surface.Card)
            .border(1.5.dp, edge, shape)
            .semantics { this.selected = selected }
            .clickable(interactionSource = interaction, indication = null, role = Role.Button, onClick = onClick)
            .heightIn(min = 66.dp)
            .padding(horizontal = 14.dp, vertical = PgSpace.M)
            .testTag(tag),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(
            Modifier
                .size(PgLayout.TouchTarget)
                .clip(RoundedCornerShape(12.dp))
                .background(PgColor.Accent.Tint),
            contentAlignment = Alignment.Center,
        ) {
            PgIcon(icon, null, color = PgColor.Accent.Text, size = 22.dp, strokeWidth = 1.9f)
        }

        Spacer(Modifier.width(PgSpace.L))
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
            Text(title, color = if (pressed) PgColor.Accent.Base else PgColor.Text.Primary, style = PgType.BodyStrong)
            Text(caption, color = PgColor.Text.Muted, style = PgType.Caption)
        }
    }
}

/**
 * Карточка «QR-код»: на телефоне код снимают камерой или берут с картинки из галереи (системный выбор — без доступа ко всем
 * фото), поэтому заголовок и подпись — свои, а не «с экрана» десктопа. Нажатие — меню «Сканировать камерой» · «Выбрать
 * картинку»; камеры нет — сразу выбор картинки.
 */
@Composable
private fun QrSource(selected: Boolean, actions: ImportActions) {
    var open by remember { mutableStateOf(false) }
    Box(Modifier.fillMaxWidth()) {
        SourceCard(
            PgIcons.Qr,
            stringResource(R.string.android_import_qr_title),
            stringResource(R.string.android_import_qr_text),
            selected,
            "import.qr",
        ) {
            actions.source(ImportSource.Qr)
            if (actions.scan == null) {
                actions.pickImage()
            } else {
                open = true
            }
        }

        val scan = actions.scan
        PgMenu(open && scan != null, { open = false }, "import.qr.menu") {
            if (scan != null) {
                PgMenuAction(stringResource(R.string.android_import_qr_camera), "import.qr.camera", {
                    open = false
                    scan()
                })
            }

            PgMenuAction(stringResource(R.string.android_import_qr_image), "import.qr.image", {
                open = false
                actions.pickImage()
            })
        }
    }
}

/** «Ввести ссылку» раскрыта: поле в несколько строк моноширинным и «Проверить» — второстепенная кнопка справа. */
@Composable
private fun LinkBox(state: ImportUi, actions: ImportActions) {
    // Текст — в модели окна, а не в сохраненном состоянии: конфиг на сотни КБ в Bundle уронил бы процесс (и VPN с ним),
    // а ключи серверов попали бы в системный процесс.
    val text = state.text
    Column(Modifier.fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(PgSpace.S)) {
        PgInput(
            text,
            actions.text,
            stringResource(R.string.import_link_title),
            "import.text",
            Modifier.heightIn(min = 88.dp),
            placeholder = stringResource(R.string.import_link_placeholder),
            style = PgType.Mono.copy(fontSize = PgType.Caption.fontSize),
            singleLine = false,
        )
        PgSecondaryButton(
            stringResource(R.string.import_link_check),
            "import.check",
            { actions.check(text) },
            Modifier.align(Alignment.End),
            enabled = text.isNotBlank() && !state.downloading,
        )
    }
}

/**
 * Что сейчас с источником — под карточками, как Message десктопа: загрузка подписки, почему не вышло (подписка, буфер, файл,
 * картинка, камера), поиск кода на картинке, текст без серверов.
 */
@Composable
private fun Messages(state: ImportUi) {
    val note = Modifier.fillMaxWidth().padding(horizontal = PgSpace.Xs)
    if (state.downloading) {
        Text(stringResource(R.string.import_downloading), note.testTag("import.downloading"), color = PgColor.Text.Secondary, style = PgType.Small)
    }

    if (state.qrReading) {
        Text(stringResource(R.string.android_import_qr_reading), note.testTag("import.qr.reading"), color = PgColor.Text.Secondary, style = PgType.Small)
    }

    state.failure?.let { reason ->
        val (message, value) = Texts.failure(reason)
        val because = if (value == null) stringResource(message) else stringResource(message, value)
        Text(stringResource(R.string.import_download_failed, because), note.testTag("import.failed"), color = PgColor.Status.Warning.Text, style = PgType.Small)
    }

    state.qrProblem?.let {
        Text(stringResource(it), note.testTag("import.qr.problem"), color = PgColor.Status.Warning.Text, style = PgType.Small)
    }

    state.problem?.let {
        Text(stringResource(it.text, *it.args), note.testTag("import.problem"), color = PgColor.Status.Warning.Text, style = PgType.Small)
    }

    if (state.batch?.found == 0) {
        Text(stringResource(R.string.import_empty_text), note.testTag("import.empty"), color = PgColor.Status.Warning.Text, style = PgType.Small)
    }
}

/** Число плитки предпросмотра: моноширинным размера status, полужирным. */
private val TileNumber = PgType.Mono.copy(fontSize = PgType.Status.fontSize, fontWeight = FontWeight.SemiBold, textAlign = TextAlign.Center)

/**
 * Предпросмотр (доска 3): «Предпросмотр» и справа формат с протоколами, четыре плитки — найдено (все строки), новых (зеленая),
 * дубли, ошибки (оранжевая); строки с ошибками, http без шифрования и «эта подписка уже добавлена» — оранжевыми плашками;
 * название профиля, если будут новые серверы и подписка не та же.
 */
@Composable
private fun Preview(state: ImportUi, batch: ImportBatch, language: String, onName: (String) -> Unit) {
    PgCard(Modifier.fillMaxWidth().testTag("import.preview"), contentPadding = PaddingValues(horizontal = PgSpace.Card, vertical = 14.dp)) {
        Column(verticalArrangement = Arrangement.spacedBy(PgSpace.L)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Overline(stringResource(R.string.import_preview))
                Spacer(Modifier.width(PgSpace.S))
                Text(formatText(batch), Modifier.weight(1f).testTag("import.format"), color = PgColor.Text.Muted, style = PgType.Caption, textAlign = TextAlign.End)
            }

            Row(horizontalArrangement = Arrangement.spacedBy(PgSpace.S)) {
                Tile(batch.found, stringResource(R.string.import_found), TileTone.Plain, "import.count.found", Modifier.weight(1f))
                Tile(state.fresh, stringResource(R.string.import_new), TileTone.Good, "import.count.new", Modifier.weight(1f))
                Tile(state.duplicates, stringResource(R.string.import_duplicates), TileTone.Plain, "import.count.duplicates", Modifier.weight(1f))
                Tile(batch.failed, stringResource(Texts.importErrors(batch.failed, language)), TileTone.Bad, "import.count.failed", Modifier.weight(1f))
            }

            val failed = batch.lines.filter { !it.result.isOk }
            if (failed.isNotEmpty()) {
                Notice("import.errors") {
                    for (line in failed.take(SHOWN_ERRORS)) {
                        NoticeText(stringResource(R.string.import_line, line.number.toString(), stringResource(Texts.importError(line.result.error))))
                    }

                    if (failed.size > SHOWN_ERRORS) {
                        NoticeText(stringResource(R.string.import_more, (failed.size - SHOWN_ERRORS).toString()))
                    }
                }
            }

            if (state.insecure) {
                // Ключи серверов шли открытым текстом: их могли прочитать по пути (import.insecure, как у десктопа).
                Notice("import.insecure") { NoticeText(stringResource(R.string.import_insecure)) }
            }

            val existing = state.existing
            if (existing != null) {
                // Та же подписка уже добавлена: «Добавить» обновит ее, а не создаст вторую группу (решение 528) — поля названия нет.
                Notice("import.existing") { NoticeText(stringResource(R.string.import_subscription_exists, existing)) }
            } else if (state.fresh > 0) {
                NameField(state.name, onName)
            }
        }
    }
}

/** «base64 · VLESS / Hy2 / Trojan» — формат и протоколы разобранных серверов в порядке фильтра списка (FormatText десктопа). */
@Composable
private fun formatText(batch: ImportBatch): String {
    val format = stringResource(Texts.importFormat(batch.format))
    val protocols = ServerList.protocolOrder.filter { protocol -> batch.lines.any { it.result.server?.protocol == protocol } }
    return listOf(format, protocols.joinToString(" / ") { shortName(it) }).filter { it.isNotEmpty() }.joinToString(" · ")
}

/** Короткие имена протоколов для строки формата, как в макете: SS, Hy2, WG. */
private fun shortName(protocol: ProxyProtocol): String = when (protocol) {
    ProxyProtocol.Shadowsocks -> "SS"
    ProxyProtocol.Hysteria2 -> "Hy2"
    ProxyProtocol.WireGuard -> "WG"
    else -> ServerFacts.protocolName(protocol)
}

/** Тон плитки предпросмотра: подложка, число и подпись. */
private enum class TileTone(val background: Color, val number: Color, val caption: Color) {
    Plain(PgColor.Control.SegmentTrack, PgColor.Text.Primary, PgColor.Text.Muted),
    Good(PgColor.Status.Success.Tint, PgColor.Status.Success.Text, PgColor.Status.Success.Badge),
    Bad(PgColor.Status.Warning.Tint, PgColor.Status.Warning.Text, PgColor.Status.Warning.Badge),
}

/** Плитка предпросмотра (Border.tile): число и подпись под ним, скругление 12. */
@Composable
private fun Tile(count: Int, caption: String, tone: TileTone, tag: String, modifier: Modifier) {
    Column(
        modifier
            .clip(RoundedCornerShape(12.dp))
            .background(tone.background)
            .padding(horizontal = PgSpace.Xxs, vertical = PgSpace.M),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(2.dp),
    ) {
        Text(count.toString(), Modifier.testTag(tag), color = tone.number, style = TileNumber, maxLines = 1)
        Text(caption, color = tone.caption, style = PgType.Caption, textAlign = TextAlign.Center)
    }
}

/** Оранжевая плашка (Border.notice, .im-notice): warning.tint, рамка warning.line, скругление 12. */
@Composable
private fun Notice(tag: String, content: @Composable ColumnScope.() -> Unit) {
    val shape = RoundedCornerShape(12.dp)
    Column(
        Modifier
            .fillMaxWidth()
            .clip(shape)
            .background(PgColor.Status.Warning.Tint)
            .border(1.dp, PgColor.Status.Warning.Line, shape)
            .padding(horizontal = PgSpace.L, vertical = PgSpace.M)
            .testTag(tag),
        verticalArrangement = Arrangement.spacedBy(2.dp),
        content = content,
    )
}

@Composable
private fun NoticeText(text: String) {
    Text(text, color = PgColor.Status.Warning.Badge, style = PgType.Small)
}

/** Название профиля: без него серверы идут в «Мои серверы» (import.name.placeholder), адрес подписки не хранится. */
@Composable
private fun NameField(name: String, onName: (String) -> Unit) {
    val label = stringResource(R.string.import_name)
    Column(verticalArrangement = Arrangement.spacedBy(PgSpace.Xs)) {
        Text(label, color = PgColor.Text.Secondary, style = PgType.Small.copy(fontWeight = FontWeight.SemiBold))
        PgInput(name, { onName(it.take(NAME_LIMIT)) }, label, "import.name", placeholder = stringResource(R.string.import_name_placeholder))
    }
}

/**
 * «Добавить N серверов» (.im-add): основная кнопка высотой 54 со скруглением 16, текст размера title; закреплена внизу вне
 * прокрутки. При нажатии — accent.text и сжатие 0,98; недоступна — прозрачность 0,45.
 */
@Composable
private fun AddButton(label: String, enabled: Boolean, onClick: () -> Unit, modifier: Modifier) {
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    val shape = RoundedCornerShape(16.dp)
    Box(
        modifier
            .fillMaxWidth()
            .height(54.dp)
            .scale(if (pressed && enabled) 0.98f else 1f)
            .alpha(if (enabled) 1f else 0.45f)
            .clip(shape)
            .background(if (pressed && enabled) PgColor.Accent.Text else PgColor.Accent.Base)
            .clickable(interactionSource = interaction, indication = null, enabled = enabled, role = Role.Button, onClick = onClick)
            .testTag("import.add"),
        contentAlignment = Alignment.Center,
    ) {
        Text(label, color = PgColor.Surface.Solid, style = PgType.Title, textAlign = TextAlign.Center)
    }
}

package com.privategateway.client.ui.servers

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
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.unit.dp
import com.privategateway.client.R
import com.privategateway.client.ui.ImportUi
import com.privategateway.client.ui.Texts
import com.privategateway.client.ui.theme.Overline
import com.privategateway.client.ui.theme.PgButton
import com.privategateway.client.ui.theme.PgCard
import com.privategateway.client.ui.theme.PgColor
import com.privategateway.client.ui.theme.PgIcon
import com.privategateway.client.ui.theme.PgIcons
import com.privategateway.client.ui.theme.PgLayout
import com.privategateway.client.ui.theme.PgRadius
import com.privategateway.client.ui.theme.PgSpace
import com.privategateway.client.ui.theme.PgType

/** Сколько строк с ошибками показывать по отдельности; остальные — одной строкой «И еще…». */
private const val SHOWN_ERRORS = 5

/**
 * «Добавить» (доска 3, для телефона): текст из буфера обмена или вставленный — ссылки, base64, sing-box, Clash или адрес
 * подписки https://; предпросмотр с форматом, числом серверов и строками, которые пропущены, название профиля — и только
 * потом добавление. Без названия серверы идут в «Мои серверы», адрес подписки не хранится.
 */
@Composable
fun ImportScreen(
    state: ImportUi,
    clipboard: () -> String?,
    onText: (String) -> Unit,
    onPreview: (String) -> Unit,
    onName: (String) -> Unit,
    onAdd: () -> Unit,
    onClose: () -> Unit,
) {
    // Текст — в модели окна, а не в сохраненном состоянии: конфиг на сотни КБ в Bundle уронил бы процесс (и VPN с ним),
    // а ключи серверов попали бы в системный процесс.
    val text = state.text
    var emptyClipboard by rememberSaveable { mutableStateOf(false) }
    val language = Texts.language(LocalConfiguration.current.locales)
    Column(
        Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .statusBarsPadding()
            .navigationBarsPadding()
            .imePadding()
            .padding(horizontal = PgSpace.WindowSideMobile)
            .testTag("import"),
        verticalArrangement = Arrangement.spacedBy(PgSpace.L),
    ) {
        Row(Modifier.fillMaxWidth().padding(top = PgSpace.L), verticalAlignment = Alignment.CenterVertically) {
            val back = stringResource(R.string.common_back)
            Box(
                Modifier.size(PgLayout.TouchTarget).clip(CircleShape).clickable(onClickLabel = back, role = Role.Button, onClick = onClose),
                contentAlignment = Alignment.Center,
            ) {
                PgIcon(PgIcons.Back, back, color = PgColor.Text.Primary, size = 22.dp, strokeWidth = 2f)
            }

            Spacer(Modifier.width(PgSpace.S))
            Text(stringResource(R.string.header_add), style = PgType.H1)
        }

        PgCard(Modifier.fillMaxWidth()) {
            Row(
                Modifier
                    .fillMaxWidth()
                    .clickable(role = Role.Button) {
                        val pasted = clipboard()?.takeIf { it.isNotBlank() }
                        emptyClipboard = pasted == null
                        if (pasted != null) {
                            onText(pasted)
                            onPreview(pasted)
                        }
                    }
                    .testTag("import.clipboard"),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                PgIcon(PgIcons.Clipboard, null, color = PgColor.Accent.Base, size = 22.dp)
                Spacer(Modifier.width(PgSpace.L))
                Column(Modifier.weight(1f)) {
                    Text(stringResource(R.string.import_clipboard_title), style = PgType.BodyStrong)
                    Text(stringResource(R.string.import_clipboard_text), color = PgColor.Text.Secondary, style = PgType.Caption)
                }
            }

            if (emptyClipboard) {
                Text(stringResource(R.string.import_empty_clipboard), color = PgColor.Status.Warning.Text, style = PgType.Caption, modifier = Modifier.padding(top = PgSpace.S))
            }
        }

        PgCard(Modifier.fillMaxWidth()) {
            Overline(stringResource(R.string.import_link_title))
            Spacer(Modifier.size(PgSpace.S))
            val shape = RoundedCornerShape(PgRadius.Input)
            BasicTextField(
                value = text,
                onValueChange = onText,
                textStyle = PgType.Mono.copy(color = PgColor.Text.Primary),
                cursorBrush = SolidColor(PgColor.Accent.Base),
                modifier = Modifier
                    .fillMaxWidth()
                    .heightIn(min = 96.dp)
                    .clip(shape)
                    .background(PgColor.Surface.Inset)
                    .border(1.dp, PgColor.Line.Border, shape)
                    .padding(PgSpace.M)
                    .testTag("import.text"),
                decorationBox = { field ->
                    Box {
                        if (text.isEmpty()) {
                            Text(stringResource(R.string.import_link_placeholder), color = PgColor.Text.Muted, style = PgType.Mono)
                        }

                        field()
                    }
                },
            )
            Spacer(Modifier.size(PgSpace.S))
            Text(stringResource(R.string.android_import_hint), color = PgColor.Text.Muted, style = PgType.Caption)
            Spacer(Modifier.size(PgSpace.M))
            PgButton(stringResource(R.string.import_link_check), enabled = text.isNotBlank() && !state.downloading, tag = "import.check") { onPreview(text) }
            if (state.downloading) {
                Text(stringResource(R.string.import_downloading), color = PgColor.Text.Secondary, style = PgType.Small, modifier = Modifier.padding(top = PgSpace.S).testTag("import.downloading"))
            }

            state.failure?.let { reason ->
                val (message, value) = Texts.failure(reason)
                val because = if (value == null) stringResource(message) else stringResource(message, value)
                Text(
                    stringResource(R.string.import_download_failed, because),
                    color = PgColor.Status.Danger.Text,
                    style = PgType.Small,
                    modifier = Modifier.padding(top = PgSpace.S).testTag("import.failed"),
                )
            }
        }

        val batch = state.batch
        if (batch != null) {
            PgCard(Modifier.fillMaxWidth().testTag("import.preview")) {
                Overline(stringResource(R.string.import_preview) + " · " + stringResource(Texts.importFormat(batch.format)))
                Spacer(Modifier.size(PgSpace.S))
                val good = batch.found - batch.failed
                Text(stringResource(R.string.import_found) + ": " + good, style = PgType.BodyStrong)
                if (batch.failed > 0) {
                    Text(batch.failed.toString() + " " + stringResource(Texts.importErrors(batch.failed, language)), color = PgColor.Status.Warning.Text, style = PgType.Small)
                    val failed = batch.lines.filter { !it.result.isOk }
                    for (line in failed.take(SHOWN_ERRORS)) {
                        Text(
                            stringResource(R.string.import_line, line.number.toString(), stringResource(Texts.importError(line.result.error))),
                            color = PgColor.Text.Secondary,
                            style = PgType.Caption,
                        )
                    }

                    if (failed.size > SHOWN_ERRORS) {
                        Text(stringResource(R.string.import_more, (failed.size - SHOWN_ERRORS).toString()), color = PgColor.Text.Secondary, style = PgType.Caption)
                    }
                }

                if (state.insecure) {
                    // Ключи серверов шли открытым текстом: их могли прочитать по пути (import.insecure, как у десктопа).
                    Text(
                        stringResource(R.string.import_insecure),
                        color = PgColor.Status.Caution.Text,
                        style = PgType.Small,
                        modifier = Modifier
                            .padding(top = PgSpace.S)
                            .clip(RoundedCornerShape(PgRadius.Button))
                            .background(PgColor.Status.Caution.Tint)
                            .padding(PgSpace.M)
                            .testTag("import.insecure"),
                    )
                }

                val added = state.added
                if (added == null && good > 0) {
                    Spacer(Modifier.size(PgSpace.M))
                    NameField(state.name, onName)
                }

                Spacer(Modifier.size(PgSpace.M))
                when {
                    added != null && added > 0 -> Text(
                        stringResource(Texts.imported(added, language), added.toString()),
                        color = PgColor.Status.Success.Text,
                        style = PgType.BodyStrong,
                        modifier = Modifier.testTag("import.done"),
                    )

                    added != null || good == 0 -> Text(
                        stringResource(if (good == 0 && added == null) R.string.import_empty_text else R.string.import_nothing),
                        color = PgColor.Text.Secondary,
                        style = PgType.Body,
                        modifier = Modifier.testTag("import.done"),
                    )

                    else -> PgButton(stringResource(Texts.importAdd(good, language), good.toString()), enabled = !state.adding, tag = "import.add", onClick = onAdd)
                }
            }
        }

        Spacer(Modifier.size(PgSpace.L))
    }
}

/** Название профиля: без него серверы идут в «Мои серверы» (import.name.placeholder), адрес подписки не хранится. */
@Composable
private fun NameField(name: String, onName: (String) -> Unit) {
    Text(stringResource(R.string.import_name), color = PgColor.Text.Secondary, style = PgType.Caption)
    Spacer(Modifier.size(PgSpace.Xs))
    val shape = RoundedCornerShape(PgRadius.Input)
    BasicTextField(
        value = name,
        onValueChange = onName,
        singleLine = true,
        textStyle = PgType.Body.copy(color = PgColor.Text.Primary),
        cursorBrush = SolidColor(PgColor.Accent.Base),
        modifier = Modifier
            .fillMaxWidth()
            .clip(shape)
            .background(PgColor.Surface.Inset)
            .border(1.dp, PgColor.Line.Border, shape)
            .padding(PgSpace.M)
            .testTag("import.name"),
        decorationBox = { field ->
            Box {
                if (name.isEmpty()) {
                    Text(stringResource(R.string.import_name_placeholder), color = PgColor.Text.Muted, style = PgType.Body)
                }

                field()
            }
        },
    )
}

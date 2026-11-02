package com.privategateway.client.ui.settings

import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
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
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.privategateway.client.R
import com.privategateway.client.core.backup.MobileBackup
import com.privategateway.client.core.text.Plural
import com.privategateway.client.data.BackupParts
import com.privategateway.client.data.BackupRestored
import com.privategateway.client.ui.Texts
import com.privategateway.client.ui.theme.PgCard
import com.privategateway.client.ui.theme.PgCheck
import com.privategateway.client.ui.theme.PgColor
import com.privategateway.client.ui.theme.PgDialog
import com.privategateway.client.ui.theme.PgIcons
import com.privategateway.client.ui.theme.PgInput
import com.privategateway.client.ui.theme.PgLayout
import com.privategateway.client.ui.theme.PgPageHeader
import com.privategateway.client.ui.theme.PgPrimaryButton
import com.privategateway.client.ui.theme.PgSecondaryButton
import com.privategateway.client.ui.theme.PgSheet
import com.privategateway.client.ui.theme.PgSpace
import com.privategateway.client.ui.theme.PgTone
import com.privategateway.client.ui.theme.PgType
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.time.format.FormatStyle

/** Что умеет экран копии — действия модели и окна Android «Сохранить» и «Открыть». */
class BackupActions(
    val parts: (BackupParts) -> Unit = {},
    val password: (String) -> Unit = {},
    /** «Сохранить файл…»: модель согласна — окно Android «Сохранить». */
    val save: () -> Unit = {},
    /** «Выбрать файл .pgbackup» — окно Android «Открыть». */
    val choose: () -> Unit = {},
    val open: (String) -> Unit = {},
    val restore: () -> Unit = {},
    val restoreAuto: () -> Unit = {},
    val closeRestore: () -> Unit = {},
)

/**
 * «Резервная копия» (доска 14, решение 559): «Сохранить копию» — флажки, предупреждения о ключах и адресах подписок, пароль
 * звездочками, «Сохранить файл…»; «Восстановить» — выбор файла, лист с паролем и тем, что в копии; «Последняя автокопия» с
 * «Восстановить» и вопросом «Вернуть автокопию?». Файл .pgbackup — тот же формат, что у десктопа.
 */
@Composable
fun BackupScreen(ui: BackupUi, actions: BackupActions, onBack: () -> Unit) {
    var askingAuto by rememberSaveable { mutableStateOf(false) }
    Column(
        Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .statusBarsPadding()
            .padding(horizontal = PgSpace.WindowSideMobile)
            .padding(bottom = PgLayout.ScrollBottomPadding)
            .testTag("backup"),
    ) {
        PgPageHeader(stringResource(R.string.settings_adv_backup_title), onBack)
        Spacer(Modifier.size(PgSpace.L))
        SaveCard(ui, actions)
        Spacer(Modifier.size(18.dp))
        RestoreCard(ui, actions) { askingAuto = true }
    }

    ui.restore?.let { step ->
        BackHandler(onBack = actions.closeRestore)
        RestoreSheet(step, actions)
    }

    if (askingAuto) {
        PgDialog(
            stringResource(R.string.dialog_autocopy_title),
            stringResource(R.string.dialog_autocopy_text),
            PgIcons.AdvBackup,
            PgTone.Accent,
            stringResource(R.string.dialog_cancel),
            { askingAuto = false },
            stringResource(R.string.dialog_autocopy_restore),
            {
                askingAuto = false
                actions.restoreAuto()
            },
            { askingAuto = false },
            "dialog.autocopy",
        )
    }
}

@Composable
private fun SaveCard(ui: BackupUi, actions: BackupActions) {
    PgCard(Modifier.fillMaxWidth().testTag("backup.save")) {
        Text(stringResource(R.string.backup_save_title), color = PgColor.Text.Primary, style = PgType.Title)
        Spacer(Modifier.size(PgSpace.S))
        PgCheck(stringResource(R.string.backup_servers_title), ui.parts.servers, "backup.servers", { actions.parts(ui.parts.copy(servers = it)) })
        PgCheck(stringResource(R.string.backup_settings_title), ui.parts.settings, "backup.settings", { actions.parts(ui.parts.copy(settings = it)) })
        PgCheck(
            stringResource(R.string.backup_keys_title),
            ui.parts.keys,
            "backup.keys",
            { actions.parts(ui.parts.copy(keys = it)) },
            enabled = ui.parts.servers,
        )
        if (ui.parts.keys) {
            Warning(stringResource(R.string.backup_keys_warning), "backup.keys.warning")
        } else if (ui.parts.servers) {
            Warning(stringResource(R.string.backup_subscriptions_warning), "backup.subscriptions.warning")
        }

        Spacer(Modifier.size(PgSpace.L))
        Text(stringResource(R.string.backup_password), color = PgColor.Text.Primary, style = PgType.BodyStrong)
        Spacer(Modifier.size(PgSpace.Xs))
        val label = stringResource(R.string.backup_password)
        PgInput(
            ui.password,
            actions.password,
            label,
            "backup.password",
            keyboard = KeyboardOptions(keyboardType = KeyboardType.Password),
            visual = PasswordVisualTransformation('*'),
        )
        Spacer(Modifier.size(PgSpace.Xs))
        if (ui.tried && ui.passwordShort) {
            Text(
                stringResource(R.string.backup_password_short, BackupUi.MIN_PASSWORD.toString()),
                Modifier.testTag("backup.password.short"),
                color = PgColor.Status.Danger.Text,
                style = PgType.Caption,
            )
        } else {
            Text(stringResource(R.string.backup_password_note), color = PgColor.Text.Muted, style = PgType.Caption)
        }

        Spacer(Modifier.size(PgSpace.L))
        PgPrimaryButton(
            stringResource(R.string.backup_save),
            "backup.save.button",
            actions.save,
            Modifier.fillMaxWidth(),
            enabled = !ui.saving,
            height = 48.dp,
        )
    }
}

@Composable
private fun RestoreCard(ui: BackupUi, actions: BackupActions, askAuto: () -> Unit) {
    PgCard(Modifier.fillMaxWidth().testTag("backup.restore")) {
        Text(stringResource(R.string.backup_restore_title), color = PgColor.Text.Primary, style = PgType.Title)
        Spacer(Modifier.size(PgSpace.S))
        Text(stringResource(R.string.backup_restore_note_mobile), color = PgColor.Text.Secondary, style = PgType.Small)
        Spacer(Modifier.size(PgSpace.L))
        PgSecondaryButton(stringResource(R.string.backup_restore_choose), "backup.choose", actions.choose, Modifier.fillMaxWidth(), height = 48.dp)
        ui.restored?.let {
            Spacer(Modifier.size(PgSpace.M))
            Text(restoredText(it), Modifier.testTag("backup.restored"), color = PgColor.Status.Success.Text, style = PgType.Small)
        }

        Spacer(Modifier.size(PgSpace.L))
        Row(verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                Text(stringResource(R.string.backup_auto_title), color = PgColor.Text.Primary, style = PgType.BodyStrong)
                Text(
                    ui.lastAuto?.let { stringResource(R.string.backup_auto_meta, dateText(it)) } ?: stringResource(R.string.backup_auto_none),
                    Modifier.testTag("backup.auto.meta"),
                    color = PgColor.Text.Muted,
                    style = PgType.Caption,
                )
            }

            if (ui.lastAuto != null) {
                PgSecondaryButton(stringResource(R.string.backup_auto_restore), "backup.auto.restore", askAuto)
            }
        }
    }
}

/** Лист «Восстановить из копии»: пароль → что в копии → «Восстановить». */
@Composable
private fun RestoreSheet(step: RestoreStep, actions: BackupActions) {
    var password by rememberSaveable { mutableStateOf("") }
    PgSheet(actions.closeRestore, "backup.sheet") {
        SheetHeader(stringResource(R.string.backup_sheet_title), actions.closeRestore, "backup.sheet.close")
        Spacer(Modifier.size(PgSpace.L))
        when (step) {
            is RestoreStep.Password -> {
                val label = stringResource(R.string.backup_password)
                Text(label, color = PgColor.Text.Primary, style = PgType.BodyStrong)
                Spacer(Modifier.size(PgSpace.Xs))
                PgInput(
                    password,
                    { password = it },
                    label,
                    "backup.sheet.password",
                    keyboard = KeyboardOptions(keyboardType = KeyboardType.Password),
                    visual = PasswordVisualTransformation('*'),
                )
                step.error?.let {
                    Spacer(Modifier.size(PgSpace.Xs))
                    Text(stringResource(it), Modifier.testTag("backup.sheet.error"), color = PgColor.Status.Danger.Text, style = PgType.Caption)
                }

                Spacer(Modifier.size(PgSpace.L))
                Row(horizontalArrangement = Arrangement.spacedBy(PgSpace.S)) {
                    PgSecondaryButton(stringResource(R.string.dialog_cancel), "backup.sheet.cancel", actions.closeRestore, Modifier.weight(1f), height = 50.dp)
                    PgPrimaryButton(
                        stringResource(R.string.backup_sheet_open),
                        "backup.sheet.open",
                        { actions.open(password) },
                        Modifier.weight(1f),
                        enabled = !step.busy && password.isNotEmpty(),
                        height = 50.dp,
                    )
                }
            }

            is RestoreStep.Ready -> {
                Text(summary(step.copy), Modifier.testTag("backup.sheet.summary"), color = PgColor.Text.Primary, style = PgType.Body)
                Spacer(Modifier.size(PgSpace.S))
                Text(stringResource(R.string.backup_restore_note_mobile), color = PgColor.Text.Muted, style = PgType.Caption)
                Spacer(Modifier.size(PgSpace.L))
                PgPrimaryButton(
                    stringResource(R.string.backup_sheet_restore),
                    "backup.sheet.restore",
                    actions.restore,
                    Modifier.fillMaxWidth(),
                    enabled = !step.busy,
                    height = 50.dp,
                )
            }
        }
    }
}

@Composable
private fun Warning(text: String, tag: String) {
    Spacer(Modifier.size(PgSpace.S))
    Text(
        text,
        Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(10.dp))
            .background(PgColor.Status.Warning.Tint)
            .padding(horizontal = PgSpace.M, vertical = PgSpace.S)
            .testTag(tag),
        color = PgColor.Status.Warning.Text,
        style = PgType.Caption,
    )
}

/** «Копия от 2 нояб. 2026 г., 15:42: серверы и подписки (7, с ключами), настройки.» */
@Composable
private fun summary(copy: MobileBackup): String {
    val parts = ArrayList<String>()
    val count = copy.servers?.size ?: copy.subscriptions?.size
    if (count != null) {
        parts += stringResource(if (copy.keys) R.string.backup_part_serversKeys else R.string.backup_part_serversNoKeys, count.toString())
    }

    if (copy.settings != null) {
        parts += stringResource(R.string.backup_part_settings)
    }

    return stringResource(R.string.backup_summary, dateText(copy.createdAt), parts.joinToString(", "))
}

/** «Восстановлено: 3 сервера, 2 подписки, настройки». */
@Composable
private fun restoredText(done: BackupRestored): String {
    val language = Texts.language(LocalConfiguration.current.locales)
    val parts = ArrayList<String>()
    if (done.servers > 0) {
        parts += stringResource(plural(done.servers, language, R.string.backup_count_one, R.string.backup_count_few, R.string.backup_count_many), done.servers.toString())
    }

    if (done.subscriptions > 0) {
        parts += stringResource(plural(done.subscriptions, language, R.string.backup_subs_one, R.string.backup_subs_few, R.string.backup_subs_many), done.subscriptions.toString())
    }

    if (done.settings) {
        parts += stringResource(R.string.backup_part_settings)
    }

    return stringResource(R.string.backup_restored, parts.joinToString(", "))
}

private fun plural(count: Int, language: String, one: Int, few: Int, many: Int): Int = when (Plural.form(count, language)) {
    "one" -> one
    "few" -> few
    else -> many
}

@Composable
private fun dateText(at: java.time.Instant): String {
    val locale = LocalConfiguration.current.locales[0]
    return DateTimeFormatter.ofLocalizedDateTime(FormatStyle.MEDIUM, FormatStyle.SHORT).withLocale(locale).format(at.atZone(ZoneId.systemDefault()))
}

/** Страница копии с моделью и системными окнами «Сохранить» и «Открыть»: приложению — только выбранный файл. */
@Composable
fun BackupPage(model: BackupViewModel, onBack: () -> Unit) {
    val ui by model.ui.collectAsStateWithLifecycle()
    val resolver = LocalContext.current.contentResolver
    val create = rememberLauncherForActivityResult(ActivityResultContracts.CreateDocument("application/octet-stream")) { file ->
        file?.let { model.save(resolver, it) }
    }
    val pick = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { file ->
        file?.let { model.chosen(resolver, it) }
    }
    BackupScreen(
        ui,
        BackupActions(
            parts = model::parts,
            password = model::password,
            save = {
                if (model.canSave()) {
                    create.launch("private-gateway-" + java.time.LocalDate.now() + ".pgbackup")
                }
            },
            choose = { pick.launch(arrayOf("*/*")) },
            open = model::open,
            restore = model::restore,
            restoreAuto = model::restoreAuto,
            closeRestore = model::closeRestore,
        ),
        onBack,
    )
}

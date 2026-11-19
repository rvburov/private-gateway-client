package com.privategateway.client.ui.settings

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import com.privategateway.client.R
import com.privategateway.client.core.account.AccountError
import com.privategateway.client.core.account.LoginResult
import com.privategateway.client.core.settings.BlockPin
import com.privategateway.client.core.settings.PinCheck
import com.privategateway.client.ui.Texts
import com.privategateway.client.ui.theme.PgCard
import com.privategateway.client.ui.theme.PgColor
import com.privategateway.client.ui.theme.PgInput
import com.privategateway.client.ui.theme.PgPrimaryButton
import com.privategateway.client.ui.theme.PgSecondaryButton
import com.privategateway.client.ui.theme.PgSheet
import com.privategateway.client.ui.theme.PgSpace
import com.privategateway.client.ui.theme.PgType
import kotlinx.coroutines.launch

/**
 * PIN-код блока сайтов для «Маршрутизации» (решение 742): текущий код, запись, проверка с паузой ([PinCheck]) и сброс входом
 * в аккаунт. [reset] — null, когда входа нет (тесты экранов): тогда «Забыли PIN-код?» не показывается.
 */
class BlockPinControl(
    val pin: BlockPin?,
    val set: (BlockPin?) -> Unit,
    val check: (String) -> PinCheck,
    val reset: (suspend (email: String, password: String, code: String?) -> LoginResult)? = null,
    val email: String = "",
) {
    companion object {
        /** Без PIN-кода: ослабить блок можно сразу. */
        val None = BlockPinControl(null, {}, { PinCheck.Right })
    }
}

/** Что спросить кодом: заголовок и текст листа и действие после верного кода. */
class PinAsk(val title: String, val text: String, val allow: () -> Unit)

/**
 * Карточка «Блок сайтов → PIN-код»: «Задан: без него блок не снять» или «Не задан…» и «Изменить» / «Задать». Теги: подпись —
 * «block.pin.state», кнопка — «block.pin.change».
 */
@Composable
internal fun BlockPinCard(set: Boolean, onChange: () -> Unit) {
    PgCard(Modifier.fillMaxWidth(), contentPadding = PaddingValues(horizontal = PgSpace.Card, vertical = PgSpace.L)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f).padding(end = PgSpace.M), verticalArrangement = Arrangement.spacedBy(3.dp)) {
                Text(stringResource(R.string.block_pin_title), color = PgColor.Text.Primary, style = PgType.BodyStrong)
                Text(
                    stringResource(if (set) R.string.block_pin_on else R.string.block_pin_off),
                    Modifier.testTag("block.pin.state"),
                    color = PgColor.Text.Muted,
                    style = PgType.Caption,
                )
            }
            PgSecondaryButton(stringResource(if (set) R.string.block_pin_changeButton else R.string.block_pin_setButton), "block.pin.change", onChange)
        }
    }
}

/**
 * Лист «Снять блок с …?» (макет v2): текст, поле PIN-кода, «Забыли PIN-код?», «Оставить блок» и красная «Снять блок». Верный
 * код — [onAllowed]; пять неверных подряд — минута без попыток. Теги: лист — «block.pin.prompt», поле — «block.pin.field».
 */
@Composable
internal fun BlockPinPromptSheet(ask: PinAsk, control: BlockPinControl, onAllowed: () -> Unit, onForgot: () -> Unit, onClose: () -> Unit) {
    var pin by rememberSaveable { mutableStateOf("") }
    var error by rememberSaveable { mutableStateOf<Int?>(null) }
    val confirm: () -> Unit = {
        val check = control.check(pin)
        pin = ""
        when (check) {
            PinCheck.Right -> onAllowed()
            PinCheck.Wrong -> error = R.string.block_pin_wrong
            PinCheck.Paused -> error = R.string.block_pin_paused
        }
    }
    PgSheet(onClose, "block.pin.prompt", scroll = false) {
        SheetHeader(ask.title, onClose, "block.pin.close")
        Column(
            Modifier.fillMaxWidth().weight(1f).verticalScroll(rememberScrollState()).padding(vertical = PgSpace.Card),
            verticalArrangement = Arrangement.spacedBy(PgSpace.L),
        ) {
            Text(ask.text, color = PgColor.Text.Secondary, style = PgType.Small)
            PinField(pin, { pin = it }, stringResource(R.string.block_pin_title), "block.pin.field")
            error?.let { Text(stringResource(it), Modifier.testTag("block.pin.error"), color = PgColor.Status.Danger.Text, style = PgType.Caption) }
            if (control.reset != null) {
                Text(
                    stringResource(R.string.block_pin_forgot),
                    Modifier
                        .heightIn(min = 40.dp)
                        .clickable(role = Role.Button, onClick = onForgot)
                        .padding(vertical = PgSpace.S)
                        .testTag("block.pin.forgot"),
                    color = PgColor.Accent.Text,
                    style = PgType.BodyStrong,
                )
            }
        }

        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(PgSpace.S)) {
            PgPrimaryButton(stringResource(R.string.block_pin_keep), "block.pin.keep", onClose, Modifier.weight(1f), height = 50.dp)
            PgSecondaryButton(
                stringResource(R.string.block_pin_unblock),
                "block.pin.confirm",
                confirm,
                Modifier.weight(1f),
                enabled = pin.isNotEmpty(),
                danger = true,
                height = 50.dp,
            )
        }
    }
}

/**
 * Лист «Сбросить PIN-код» (макет v2): вход тем же аккаунтом, что в приложении; администратору — второй шаг с кодом 2FA. Вошли —
 * код снят ([onDone]), блоки остались. Теги: «block.reset.*».
 */
@Composable
internal fun BlockPinResetSheet(control: BlockPinControl, onDone: () -> Unit, onClose: () -> Unit) {
    val reset = control.reset ?: return
    var email by rememberSaveable { mutableStateOf(control.email) }
    var password by rememberSaveable { mutableStateOf("") }
    var code by rememberSaveable { mutableStateOf("") }
    var codeStep by rememberSaveable { mutableStateOf(false) }
    var busy by rememberSaveable { mutableStateOf(false) }
    var error by rememberSaveable { mutableStateOf<AccountError?>(null) }
    val scope = rememberCoroutineScope()
    val submit: () -> Unit = {
        busy = true
        error = null
        scope.launch {
            val result = reset(email, password, code.trim().takeIf { codeStep })
            busy = false
            code = ""
            when (result) {
                is LoginResult.Ok -> {
                    password = ""
                    control.set(null)
                    onDone()
                }

                LoginResult.CodeRequired -> codeStep = true
                is LoginResult.Failed -> error = result.error
            }
        }
    }
    PgSheet(onClose, "block.reset", scroll = false) {
        SheetHeader(stringResource(R.string.block_pin_reset_title), onClose, "block.reset.close")
        Column(
            Modifier.fillMaxWidth().weight(1f).verticalScroll(rememberScrollState()).padding(vertical = PgSpace.Card),
            verticalArrangement = Arrangement.spacedBy(PgSpace.L),
        ) {
            Text(stringResource(R.string.block_pin_reset_text), color = PgColor.Text.Secondary, style = PgType.Small)
            if (codeStep) {
                Column(verticalArrangement = Arrangement.spacedBy(PgSpace.Xs)) {
                    FieldLabel(stringResource(R.string.account_code))
                    PgInput(code, { code = it.take(64) }, stringResource(R.string.account_code), "block.reset.code", keyboard = KeyboardOptions(keyboardType = KeyboardType.Number))
                }
            } else {
                Column(verticalArrangement = Arrangement.spacedBy(PgSpace.Xs)) {
                    FieldLabel(stringResource(R.string.account_email))
                    PgInput(email, { email = it.take(320) }, stringResource(R.string.account_email), "block.reset.email", keyboard = KeyboardOptions(keyboardType = KeyboardType.Email))
                }
                Column(verticalArrangement = Arrangement.spacedBy(PgSpace.Xs)) {
                    FieldLabel(stringResource(R.string.account_password))
                    PgInput(
                        password,
                        { password = it.take(4096) },
                        stringResource(R.string.account_password),
                        "block.reset.password",
                        keyboard = KeyboardOptions(keyboardType = KeyboardType.Password),
                        visual = PasswordVisualTransformation(),
                    )
                }
            }
            error?.let {
                val text = if (it == AccountError.RateLimited) stringResource(Texts.account(it), "1") else stringResource(Texts.account(it))
                Text(text, Modifier.testTag("block.reset.error"), color = PgColor.Status.Danger.Text, style = PgType.Caption)
            }
        }

        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(PgSpace.S)) {
            PgSecondaryButton(stringResource(R.string.dialog_cancel), "block.reset.cancel", onClose, Modifier.weight(1f), height = 50.dp)
            PgPrimaryButton(
                stringResource(R.string.block_pin_reset_button),
                "block.reset.submit",
                submit,
                Modifier.weight(1f),
                enabled = !busy && email.isNotBlank() && password.isNotEmpty(),
                height = 50.dp,
            )
        }
    }
}

/**
 * Лист «PIN-код для снятия блока» (макет v2): что закрывает код, «PIN-код · 4–8 цифр», «Еще раз», «Задать PIN-код» и
 * «Отмена»; у заданного кода — «Убрать PIN-код»; после сброса — строка [notice]. Теги: «block.setup.*».
 */
@Composable
internal fun BlockPinSetupSheet(control: BlockPinControl, notice: Boolean, onClose: () -> Unit) {
    var pin by rememberSaveable { mutableStateOf("") }
    var repeat by rememberSaveable { mutableStateOf("") }
    var error by rememberSaveable { mutableStateOf<Int?>(null) }
    val save: () -> Unit = {
        when {
            !BlockPin.valid(pin) -> error = R.string.block_pin_invalid
            pin != repeat -> error = R.string.block_pin_mismatch
            else -> {
                control.set(BlockPin.create(pin))
                onClose()
            }
        }
    }
    PgSheet(onClose, "block.setup", scroll = false) {
        SheetHeader(stringResource(R.string.block_pin_setup_title), onClose, "block.setup.close")
        Column(
            Modifier.fillMaxWidth().weight(1f).verticalScroll(rememberScrollState()).padding(vertical = PgSpace.Card),
            verticalArrangement = Arrangement.spacedBy(PgSpace.L),
        ) {
            if (notice) {
                Text(stringResource(R.string.block_pin_reset_done), Modifier.testTag("block.setup.notice"), color = PgColor.Status.Success.Badge, style = PgType.Small)
            }
            Text(stringResource(R.string.block_pin_setup_text), color = PgColor.Text.Secondary, style = PgType.Small)
            Column(verticalArrangement = Arrangement.spacedBy(PgSpace.Xs)) {
                FieldLabel(stringResource(R.string.block_pin_field))
                PinField(pin, { pin = it }, stringResource(R.string.block_pin_field), "block.setup.pin")
            }
            Column(verticalArrangement = Arrangement.spacedBy(PgSpace.Xs)) {
                FieldLabel(stringResource(R.string.block_pin_repeat))
                PinField(repeat, { repeat = it }, stringResource(R.string.block_pin_repeat), "block.setup.repeat")
            }
            error?.let { Text(stringResource(it), Modifier.testTag("block.setup.error"), color = PgColor.Status.Danger.Text, style = PgType.Caption) }
            if (control.pin != null) {
                PgSecondaryButton(
                    stringResource(R.string.block_pin_remove),
                    "block.setup.remove",
                    {
                        control.set(null)
                        onClose()
                    },
                    Modifier.fillMaxWidth(),
                    danger = true,
                    height = 44.dp,
                )
            }
        }

        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(PgSpace.S)) {
            PgSecondaryButton(stringResource(R.string.dialog_cancel), "block.setup.cancel", onClose, Modifier.weight(1f), height = 50.dp)
            PgPrimaryButton(stringResource(R.string.block_pin_save), "block.setup.save", save, Modifier.weight(1f), height = 50.dp)
        }
    }
}

/** Поле PIN-кода: только цифры, не больше 8, точками. */
@Composable
private fun PinField(value: String, onChange: (String) -> Unit, label: String, tag: String) {
    PgInput(
        value,
        { text -> onChange(text.filter { it in '0'..'9' }.take(BlockPin.MAX_LENGTH)) },
        label,
        tag,
        style = MonoField,
        keyboard = KeyboardOptions(keyboardType = KeyboardType.NumberPassword),
        visual = PasswordVisualTransformation(),
    )
}

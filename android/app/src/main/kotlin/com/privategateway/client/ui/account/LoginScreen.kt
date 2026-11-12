package com.privategateway.client.ui.account

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.ime
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.runtime.snapshotFlow
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.input.VisualTransformation
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import com.privategateway.client.R
import com.privategateway.client.account.AccountNotice
import com.privategateway.client.core.account.AccountApi
import com.privategateway.client.core.account.AccountError
import com.privategateway.client.ui.Texts
import com.privategateway.client.ui.theme.PgColor
import com.privategateway.client.ui.theme.PgIcon
import com.privategateway.client.ui.theme.PgIcons
import com.privategateway.client.ui.theme.PgInput
import com.privategateway.client.ui.theme.PgLayout
import com.privategateway.client.ui.theme.PgPrimaryButton
import com.privategateway.client.ui.theme.PgRadius
import com.privategateway.client.ui.theme.PgSecondaryButton
import com.privategateway.client.ui.theme.PgSegments
import com.privategateway.client.ui.theme.PgSpace
import com.privategateway.client.ui.theme.PgType

/** Действия экрана входа: поля, переключатель кода, «Войти» и страница сайта ([site] — полный адрес https). */
class LoginActions(
    val email: (String) -> Unit,
    val password: (String) -> Unit,
    val code: (String) -> Unit,
    val toggleRecovery: () -> Unit,
    val submit: () -> Unit,
    val site: (String) -> Unit,
)

/**
 * Вход в аккаунт сайта (решение 621): до любого другого экрана, и до приветствия первого запуска; островка нет. Сверху — язык
 * RU/EN (Android 13+, как на приветствии), знак, заголовок и пояснение; почему вход нужен снова ([notice]: доступ отозван или
 * срок истек); почта, пароль с «глазом», код 2FA или резервный код и «Войти» («Вход…» и недоступна, пока идет запрос). Enter в
 * поле кода отправляет форму. Ошибка — текстом под формой; у 403 и 409 — кнопка на страницу сайта (решение 626).
 */
@Composable
fun LoginScreen(ui: LoginUi, notice: AccountNotice?, actions: LoginActions, language: ((String) -> Unit)?) {
    val scroll = rememberScrollState()
    // Окно во весь экран (edge-to-edge): клавиатуру форма учитывает сама, как ImportScreen. Отступы — до прокрутки: область
    // прокрутки уменьшается на высоту клавиатуры, и поле в фокусе прокручивается в видимую часть.
    var codeFocused by remember { mutableStateOf(false) }
    val keyboard = WindowInsets.ime.getBottom(LocalDensity.current) > 0
    LaunchedEffect(codeFocused, keyboard) {
        // Поле кода — последнее: с ним на экране должны быть и «Войти», и ошибка под формой, а не только само поле. Конец
        // формы сдвигается, пока выезжает клавиатура и появляется ошибка, — прокрутка идет за ним.
        if (codeFocused && keyboard) {
            snapshotFlow { scroll.maxValue }.collect { scroll.scrollTo(it) }
        }
    }

    Column(
        Modifier
            .fillMaxSize()
            .statusBarsPadding()
            .navigationBarsPadding()
            .imePadding()
            .verticalScroll(scroll)
            .padding(horizontal = PgSpace.WindowSideMobile)
            .padding(top = PgSpace.L, bottom = 24.dp)
            .testTag("login"),
    ) {
        if (language != null) {
            val current = if (Texts.language(LocalConfiguration.current.locales) == "ru") "ru" else "en"
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) {
                PgSegments(
                    listOf("ru", "en"),
                    current,
                    { stringResource(if (it == "ru") R.string.welcome_lang_ru else R.string.welcome_lang_en) },
                    "login.language",
                    { language(it) },
                    Modifier.width(112.dp),
                )
            }
        }

        Spacer(Modifier.size(32.dp))
        Box(Modifier.align(Alignment.CenterHorizontally).testTag("login.logo")) {
            PgIcon(PgIcons.Shield, null, color = PgColor.Accent.Base, size = 40.dp)
            PgIcon(PgIcons.MarkOk, null, color = PgColor.Accent.Base, size = 40.dp)
        }

        Spacer(Modifier.size(PgSpace.L))
        Text(
            stringResource(R.string.account_title),
            Modifier.fillMaxWidth(),
            color = PgColor.Text.Primary,
            style = PgType.H1,
            textAlign = TextAlign.Center,
        )
        Spacer(Modifier.size(PgSpace.S))
        Text(
            stringResource(R.string.account_text),
            Modifier.fillMaxWidth(),
            color = PgColor.Text.Secondary,
            style = PgType.Body,
            textAlign = TextAlign.Center,
        )

        if (notice != null) {
            Spacer(Modifier.size(20.dp))
            Notice(notice)
        }

        Spacer(Modifier.size(24.dp))
        val submit = { if (ui.ready && !ui.busy) actions.submit() }
        Field(stringResource(R.string.account_email))
        PgInput(
            ui.email,
            actions.email,
            stringResource(R.string.account_email),
            "login.email",
            keyboard = KeyboardOptions(keyboardType = KeyboardType.Email, autoCorrectEnabled = false, imeAction = ImeAction.Next),
        )

        Spacer(Modifier.size(PgSpace.L))
        Field(stringResource(R.string.account_password))
        Password(ui.password, actions.password)

        Spacer(Modifier.size(PgSpace.L))
        val codeLabel = stringResource(if (ui.recovery) R.string.account_recovery else R.string.account_code)
        Field(codeLabel)
        PgInput(
            ui.code,
            actions.code,
            codeLabel,
            "login.code",
            Modifier.onFocusChanged { codeFocused = it.isFocused },
            keyboard = KeyboardOptions(
                keyboardType = if (ui.recovery) KeyboardType.Text else KeyboardType.NumberPassword,
                autoCorrectEnabled = false,
                imeAction = ImeAction.Done,
            ),
            actions = KeyboardActions(onDone = { submit() }),
        )
        Spacer(Modifier.size(PgSpace.Xs))
        Text(
            stringResource(if (ui.recovery) R.string.account_recovery_hint else R.string.account_code_hint),
            Modifier.testTag("login.code.hint"),
            color = PgColor.Text.Muted,
            style = PgType.Caption,
        )
        Text(
            stringResource(if (ui.recovery) R.string.account_code_use else R.string.account_recovery_use),
            Modifier
                .heightIn(min = PgLayout.TouchTarget)
                .clickable(role = Role.Button, onClick = actions.toggleRecovery)
                .padding(vertical = PgSpace.M)
                .testTag("login.recovery.toggle"),
            color = PgColor.Accent.Base,
            style = PgType.Small,
        )

        val error = ui.error
        if (error != null) {
            Spacer(Modifier.size(PgSpace.S))
            Text(
                if (error == AccountError.RateLimited) {
                    stringResource(Texts.account(error), ui.retryMinutes.coerceAtLeast(1).toString())
                } else {
                    stringResource(Texts.account(error))
                },
                Modifier.fillMaxWidth().testTag("login.error"),
                color = PgColor.Status.Danger.Text,
                style = PgType.Small,
            )
        }

        Spacer(Modifier.size(20.dp))
        PgPrimaryButton(
            stringResource(if (ui.busy) R.string.account_signingIn else R.string.account_signIn),
            "login.submit",
            submit,
            Modifier.fillMaxWidth(),
            enabled = ui.ready && !ui.busy,
            height = 50.dp,
        )

        // 403 — 2FA настраивается на сайте; 409 — лишнее устройство удаляется на странице устройств сайта.
        val page = when (error) {
            AccountError.SetupRequired -> R.string.account_site to AccountApi.LOGIN_PAGE
            AccountError.DeviceLimit -> R.string.account_devices to AccountApi.DEVICES_PAGE
            else -> null
        }
        if (page != null) {
            Spacer(Modifier.size(PgSpace.S))
            PgSecondaryButton(
                stringResource(page.first),
                "login.site",
                { actions.site(AccountApi.page(page.second).toString()) },
                Modifier.fillMaxWidth(),
                height = 46.dp,
            )
        }
    }
}

/** Подпись поля над ним. */
@Composable
private fun Field(text: String) {
    Text(text, Modifier.padding(bottom = PgSpace.Xs), color = PgColor.Text.Primary, style = PgType.BodyStrong)
}

/** Пароль и «глаз» рядом с полем: показать или скрыть введенное. */
@Composable
private fun Password(value: String, onChange: (String) -> Unit) {
    var shown by rememberSaveable { mutableStateOf(false) }
    Row(verticalAlignment = Alignment.CenterVertically) {
        PgInput(
            value,
            onChange,
            stringResource(R.string.account_password),
            "login.password",
            Modifier.weight(1f),
            keyboard = KeyboardOptions(keyboardType = KeyboardType.Password, autoCorrectEnabled = false, imeAction = ImeAction.Next),
            visual = if (shown) VisualTransformation.None else PasswordVisualTransformation(),
        )
        Spacer(Modifier.width(PgSpace.S))
        val label = stringResource(R.string.account_password_show)
        Box(
            Modifier
                .size(PgLayout.TouchTarget)
                .clip(RoundedCornerShape(PgRadius.Button))
                .clickable(onClickLabel = label, role = Role.Switch) { shown = !shown }
                .semantics { contentDescription = label }
                .testTag("login.password.show"),
            contentAlignment = Alignment.Center,
        ) {
            PgIcon(if (shown) PgIcons.EyeOff else PgIcons.Eye, null, color = PgColor.Text.Secondary, size = 20.dp, strokeWidth = 2f)
        }
    }
}

/** Почему вход нужен снова: «Доступ отозван…» или «Срок входа истек…» — плашкой предупреждения над формой. */
@Composable
private fun Notice(notice: AccountNotice) {
    Row(
        Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(PgRadius.Input))
            .background(PgColor.Status.Warning.Tint)
            .padding(PgSpace.L)
            .testTag("login.notice"),
        verticalAlignment = Alignment.Top,
    ) {
        PgIcon(PgIcons.Warning, null, color = PgColor.Status.Warning.Text, size = 18.dp, strokeWidth = 2f)
        Spacer(Modifier.width(PgSpace.M))
        Text(
            stringResource(if (notice == AccountNotice.Revoked) R.string.account_revoked else R.string.account_expired),
            color = PgColor.Status.Warning.Text,
            style = PgType.Small,
        )
    }
}

package com.privategateway.client.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
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
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import com.privategateway.client.R
import com.privategateway.client.ui.theme.PgCard
import com.privategateway.client.ui.theme.PgColor
import com.privategateway.client.ui.theme.PgIcon
import com.privategateway.client.ui.theme.PgIcons
import com.privategateway.client.ui.theme.PgPrimaryButton
import com.privategateway.client.ui.theme.PgSecondaryButton
import com.privategateway.client.ui.theme.PgSegments
import com.privategateway.client.ui.theme.PgSpace
import com.privategateway.client.ui.theme.PgType

/** Шаги первого запуска на телефоне: шага «Установка службы» нет — службы у телефона нет, разрешение VPN спрашивает Android. */
enum class WelcomeStep {
    Greet,
    Add,
}

/**
 * Первый запуск (доска 10, решение 555), без островка: приветствие — язык RU/EN (Android 13+; раньше — язык системы, сегментов
 * нет), знак, слоган, три обещания и «Начать»; затем «Добавьте первый сервер» — «Добавить сервер» открывает «Добавить профиль»,
 * «Позже» ведет на главную. [onDone] — первый запуск пройден (больше не показывать).
 */
@Composable
fun WelcomeScreen(onAdd: () -> Unit, onDone: () -> Unit, language: ((String) -> Unit)?) {
    var step by rememberSaveable { mutableStateOf(WelcomeStep.Greet) }
    Column(
        Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .statusBarsPadding()
            .navigationBarsPadding()
            .padding(horizontal = PgSpace.WindowSideMobile)
            .padding(top = PgSpace.L, bottom = 24.dp)
            .testTag("welcome"),
    ) {
        when (step) {
            WelcomeStep.Greet -> Greet(language) { step = WelcomeStep.Add }
            WelcomeStep.Add -> AddFirst(onAdd, onDone)
        }
    }
}

@Composable
private fun ColumnScope.Greet(language: ((String) -> Unit)?, onStart: () -> Unit) {
    if (language != null) {
        val current = if (Texts.language(LocalConfiguration.current.locales) == "ru") "ru" else "en"
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) {
            PgSegments(
                listOf("ru", "en"),
                current,
                { stringResource(if (it == "ru") R.string.welcome_lang_ru else R.string.welcome_lang_en) },
                "welcome.language",
                { language(it) },
                Modifier.width(112.dp),
            )
        }
    }

    Spacer(Modifier.size(40.dp))
    Box(Modifier.align(Alignment.CenterHorizontally).testTag("welcome.logo")) {
        PgIcon(PgIcons.Shield, null, color = PgColor.Accent.Base, size = 40.dp)
        PgIcon(PgIcons.MarkOk, null, color = PgColor.Accent.Base, size = 40.dp)
    }

    Spacer(Modifier.size(PgSpace.L))
    Text(
        stringResource(R.string.app_title),
        Modifier.fillMaxWidth(),
        color = PgColor.Text.Primary,
        style = PgType.H1,
        textAlign = TextAlign.Center,
    )
    Spacer(Modifier.size(PgSpace.S))
    Text(
        stringResource(R.string.welcome_tagline),
        Modifier.fillMaxWidth(),
        color = PgColor.Text.Secondary,
        style = PgType.Body,
        textAlign = TextAlign.Center,
    )

    Spacer(Modifier.size(28.dp))
    Column(verticalArrangement = Arrangement.spacedBy(PgSpace.L)) {
        Promise(PgIcons.NavHome, stringResource(R.string.welcome_all_title, "Android"), stringResource(R.string.welcome_all_text_mobile))
        Promise(PgIcons.Shield, stringResource(R.string.welcome_ks_title), stringResource(R.string.welcome_ks_text))
        Promise(PgIcons.NavServers, stringResource(R.string.welcome_choice_title), stringResource(R.string.welcome_choice_text))
    }

    Spacer(Modifier.size(32.dp))
    PgPrimaryButton(stringResource(R.string.welcome_start), "welcome.start", onStart, Modifier.fillMaxWidth(), height = 50.dp)
}

/** Обещание приветствия: плитка 36 со значком и две строки. */
@Composable
private fun Promise(icon: String, title: String, text: String) {
    Row(verticalAlignment = Alignment.CenterVertically) {
        Box(
            Modifier.size(36.dp).clip(RoundedCornerShape(10.dp)).background(PgColor.Accent.Tint),
            contentAlignment = Alignment.Center,
        ) {
            PgIcon(icon, null, color = PgColor.Accent.Base, size = 20.dp)
        }

        Spacer(Modifier.width(PgSpace.M))
        Column(verticalArrangement = Arrangement.spacedBy(2.dp)) {
            Text(title, color = PgColor.Text.Primary, style = PgType.BodyStrong)
            Text(text, color = PgColor.Text.Muted, style = PgType.Caption)
        }
    }
}

@Composable
private fun ColumnScope.AddFirst(onAdd: () -> Unit, onDone: () -> Unit) {
    Row(verticalAlignment = Alignment.CenterVertically) {
        Box {
            PgIcon(PgIcons.Shield, null, color = PgColor.Accent.Base, size = 28.dp)
            PgIcon(PgIcons.MarkOk, null, color = PgColor.Accent.Base, size = 28.dp)
        }

        Spacer(Modifier.width(PgSpace.M))
        Text(stringResource(R.string.app_title), color = PgColor.Text.Primary, style = PgType.Status)
    }

    Spacer(Modifier.size(28.dp))
    PgCard(Modifier.fillMaxWidth().testTag("welcome.add")) {
        Box(
            Modifier.size(56.dp).clip(RoundedCornerShape(16.dp)).background(PgColor.Accent.Tint),
            contentAlignment = Alignment.Center,
        ) {
            PgIcon(PgIcons.ServerAdd, null, color = PgColor.Accent.Base, size = 32.dp)
        }

        Spacer(Modifier.size(PgSpace.L))
        Text(stringResource(R.string.welcome_add_title), color = PgColor.Text.Primary, style = PgType.Status)
        Spacer(Modifier.size(PgSpace.S))
        Text(stringResource(R.string.welcome_add_text), color = PgColor.Text.Secondary, style = PgType.Small)
        Spacer(Modifier.size(20.dp))
        PgPrimaryButton(stringResource(R.string.welcome_add_button), "welcome.add.button", onAdd, Modifier.fillMaxWidth(), height = 50.dp)
        Spacer(Modifier.size(PgSpace.S))
        PgSecondaryButton(stringResource(R.string.welcome_add_later), "welcome.add.later", onDone, Modifier.fillMaxWidth(), height = 46.dp)
    }
}

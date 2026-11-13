package com.privategateway.client.ui.settings

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalUriHandler
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import com.privategateway.client.R
import com.privategateway.client.core.text.TimeText
import com.privategateway.client.ui.Texts
import com.privategateway.client.ui.subscriptions.day
import com.privategateway.client.ui.theme.PgCard
import com.privategateway.client.ui.theme.PgColor
import com.privategateway.client.ui.theme.PgPrimaryButton
import com.privategateway.client.ui.theme.PgSecondaryButton
import com.privategateway.client.ui.theme.PgSpace
import com.privategateway.client.ui.theme.PgType
import com.privategateway.client.updates.InstallStep
import com.privategateway.client.updates.UpdateStatus
import com.privategateway.client.updates.UpdatesView
import java.time.Clock
import java.util.Locale

/**
 * Карточка «Обновления» в «О программе» Android (доска 15): «Установлена последняя версия · проверено 15:42» или «Доступно
 * обновление 0.3.0» (рамка акцента) с главным из заметок и «Скачать и установить» — загрузка с полосой и мегабайтами,
 * сверка с подписанным манифестом и окно установки Android (рамка зеленая). Нет разрешения на установку — подсказка
 * (настройку открывает окно), файл не прошел проверку или Android не поставил — так и сказано, рядом «Скачать на сайте»
 * (страница скачивания). Проверяет приложение и само при показе окна, не чаще раза в 12 часов; «Проверить» — сразу. Без
 * входа ленты нет — «Войдите в аккаунт».
 */
@Composable
internal fun UpdatesCard(view: UpdatesView, onCheck: () -> Unit, onInstall: () -> Unit = {}) {
    val links = LocalUriHandler.current
    val offer = view.version?.takeIf { view.status == UpdateStatus.Available && !view.checking }
    val busy = view.checking || view.step == InstallStep.Downloading
    // Рамка как у .up-card макета: есть предложение — accent.base, передано Android — status.success.line.
    val edge = when {
        offer == null -> PgColor.Line.Border
        view.step == InstallStep.Installing -> PgColor.Status.Success.Line
        else -> PgColor.Accent.Base
    }
    PgCard(Modifier.fillMaxWidth().testTag("about.updates"), edge = edge) {
        Column(verticalArrangement = Arrangement.spacedBy(PgSpace.S)) {
            Text(stringResource(R.string.about_updates_title), style = PgType.Title)
            Text(caption(view), color = PgColor.Text.Secondary, style = PgType.Caption, modifier = Modifier.testTag("about.updates.state"))
            if (offer != null && view.highlights.isNotEmpty()) {
                Highlights(view.highlights)
            }

            if (offer != null && (view.step == InstallStep.Downloading || view.step == InstallStep.Installing)) {
                Progress(view)
            }

            Row(horizontalArrangement = Arrangement.spacedBy(PgSpace.S)) {
                if (offer != null) {
                    val label = if (view.step == InstallStep.Downloading) R.string.about_update_loading_button else R.string.android_update_install
                    PgPrimaryButton(stringResource(label), "about.updates.install", onInstall, enabled = !busy)
                }

                if (offer != null && (view.step == InstallStep.BadFile || view.step == InstallStep.Failed)) {
                    // Запасной путь: APK с сайта ставится вручную, как в первый раз.
                    val site = view.notes ?: DOWNLOADS_URL
                    PgSecondaryButton(stringResource(R.string.about_update_open), "about.updates.site", { runCatching { links.openUri(site) } })
                } else {
                    PgSecondaryButton(stringResource(R.string.about_updates_check), "about.updates.check", onCheck, enabled = !busy)
                }
            }
        }
    }
}

@Composable
private fun caption(view: UpdatesView): String {
    if (view.checking) {
        return stringResource(R.string.about_updates_checking)
    }

    val version = view.version.orEmpty()
    return when (view.status) {
        // Еще не проверяли: проверка при показе окна вот-вот начнется.
        null -> stringResource(R.string.about_updates_checking)
        UpdateStatus.NotSigned -> stringResource(R.string.android_updates_unsigned)
        UpdateStatus.SignedOut -> stringResource(R.string.about_updates_signIn)
        UpdateStatus.UpToDate -> {
            val checked = view.checkedAt?.let { day(TimeText.day(it, Clock.systemDefaultZone())) }
            stringResource(R.string.about_updates_upToDate, checked ?: "—")
        }
        UpdateStatus.Failed -> stringResource(R.string.about_updates_failed)
        UpdateStatus.Available -> when (view.step) {
            InstallStep.None -> stringResource(R.string.about_update_available, version)
            InstallStep.Downloading -> stringResource(R.string.android_update_downloading, version)
            InstallStep.Installing -> stringResource(R.string.about_update_ready)
            InstallStep.Allow -> stringResource(R.string.android_update_allow)
            InstallStep.BadFile -> stringResource(R.string.android_update_badFile)
            InstallStep.Failed -> stringResource(R.string.android_update_failed)
        }
    }
}

/** Главное из заметок выпуска — пунктами с точкой, как на карточке десктопа. */
@Composable
private fun Highlights(items: List<String>) {
    Column(Modifier.testTag("about.updates.highlights"), verticalArrangement = Arrangement.spacedBy(PgSpace.Xxs)) {
        items.forEach { item ->
            Row(verticalAlignment = Alignment.Top) {
                Box(Modifier.padding(start = 2.dp, top = 8.dp, end = PgSpace.S).size(4.dp).clip(CircleShape).background(PgColor.Text.Secondary))
                Text(item, color = PgColor.Text.Primary, style = PgType.Small)
            }
        }
    }
}

/** Полоса загрузки (accent; передано Android — полная, зеленая) и «18,4 из 28,7 МБ · VPN продолжает работать». */
@Composable
private fun Progress(view: UpdatesView) {
    val installing = view.step == InstallStep.Installing
    val fraction = when {
        installing -> 1f
        view.total > 0 -> (view.received.toFloat() / view.total).coerceIn(0f, 1f)
        else -> 0f
    }
    val shape = RoundedCornerShape(3.dp)
    Box(Modifier.fillMaxWidth().height(6.dp).clip(shape).background(PgColor.Accent.Tint).testTag("about.updates.progress")) {
        Box(Modifier.fillMaxHeight().fillMaxWidth(fraction).clip(shape).background(if (installing) PgColor.Status.Success.Base else PgColor.Accent.Base))
    }

    if (!installing && view.total > 0) {
        val locale = Locale.forLanguageTag(Texts.language(LocalConfiguration.current.locales))
        fun mb(bytes: Long) = String.format(locale, "%.1f", bytes / BYTES_IN_MB)
        Text(
            stringResource(R.string.about_update_progress, mb(view.received), stringResource(R.string.about_update_mb, mb(view.total))),
            color = PgColor.Text.Secondary,
            style = PgType.Caption,
            modifier = Modifier.testTag("about.updates.bytes"),
        )
    }
}

/** МБ на карточке — как у десктопа: двоичные, с одной цифрой после запятой. */
private const val BYTES_IN_MB = 1_048_576.0

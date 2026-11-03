package com.privategateway.client.ui.settings

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalUriHandler
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import com.privategateway.client.R
import com.privategateway.client.core.text.TimeText
import com.privategateway.client.core.updates.UpdateResult
import com.privategateway.client.ui.subscriptions.day
import com.privategateway.client.ui.theme.PgCard
import com.privategateway.client.ui.theme.PgColor
import com.privategateway.client.ui.theme.PgPrimaryButton
import com.privategateway.client.ui.theme.PgSecondaryButton
import com.privategateway.client.ui.theme.PgSpace
import com.privategateway.client.ui.theme.PgType
import java.time.Clock

/** «Обновления» в «О программе» Android: идет ли проверка, итог последней и когда он получен (мс эпохи). */
data class UpdatesView(val checking: Boolean = false, val result: UpdateResult? = null, val checkedAt: Long? = null)

/**
 * Карточка «Обновления» (доска 15, DECISIONS.md, п. 8) — только кнопка «Проверить», фоновой проверки и уведомления нет:
 * «Установлена последняя версия · проверено 15:42» или «Доступна версия 0.2.0» и «Открыть выпуск» — APK ставят со страницы
 * выпуска на GitHub. Подписанных выпусков еще нет (ключей в приложении нет) — так и сказано; не вышло — «Проверить
 * обновления не удалось», причина в журнале «Проверки».
 */
@Composable
internal fun UpdatesCard(view: UpdatesView, onCheck: () -> Unit) {
    val links = LocalUriHandler.current
    val available = (view.result as? UpdateResult.Available)?.takeIf { !view.checking }
    PgCard(Modifier.fillMaxWidth().testTag("about.updates")) {
        Column(verticalArrangement = Arrangement.spacedBy(PgSpace.S)) {
            Text(stringResource(R.string.about_updates_title), style = PgType.Title)
            Text(caption(view), color = PgColor.Text.Secondary, style = PgType.Caption, modifier = Modifier.testTag("about.updates.state"))
            Row(horizontalArrangement = Arrangement.spacedBy(PgSpace.S)) {
                if (available != null) {
                    PgPrimaryButton(stringResource(R.string.about_update_open), "about.updates.open", { runCatching { links.openUri(available.page) } })
                }

                PgSecondaryButton(stringResource(R.string.about_updates_check), "about.updates.check", onCheck, enabled = !view.checking)
            }
        }
    }
}

@Composable
private fun caption(view: UpdatesView): String {
    if (view.checking) {
        return stringResource(R.string.about_updates_checking)
    }

    return when (val result = view.result) {
        null -> stringResource(R.string.about_updates_manual)
        UpdateResult.NotSigned -> stringResource(R.string.android_updates_unsigned)
        UpdateResult.UpToDate -> stringResource(R.string.about_updates_upToDate, view.checkedAt?.let { day(TimeText.day(it, Clock.systemDefaultZone())) } ?: "—")
        is UpdateResult.Available -> stringResource(R.string.about_updates_later, result.version)
        UpdateResult.Failed -> stringResource(R.string.about_updates_failed)
    }
}

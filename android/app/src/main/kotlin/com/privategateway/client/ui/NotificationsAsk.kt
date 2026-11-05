package com.privategateway.client.ui

import androidx.compose.runtime.Composable
import androidx.compose.ui.res.stringResource
import com.privategateway.client.R
import com.privategateway.client.ui.theme.PgDialog
import com.privategateway.client.ui.theme.PgIcons
import com.privategateway.client.ui.theme.PgTone

/**
 * Вопрос о разрешении на уведомления — после первого удачного подключения, а не перед запросом VPN (решение пользователя
 * 2026-11-03, DECISIONS.md, п. 7; доска 26): тогда понятно, зачем они, — сообщить об обрыве. «Разрешить» — системный запрос
 * Android, «Не сейчас» и затемнение — больше не спрашивать. Тег — «dialog.notifications».
 */
@Composable
fun NotificationsAsk(onAllow: () -> Unit, onLater: () -> Unit) {
    PgDialog(
        stringResource(R.string.dialog_notifications_title),
        stringResource(R.string.dialog_notifications_text),
        PgIcons.Shield,
        PgTone.Accent,
        stringResource(R.string.dialog_notifications_allow),
        onAllow,
        stringResource(R.string.dialog_notifications_later),
        onLater,
        onLater,
        "dialog.notifications",
        detail = PgIcons.MarkAlert,
    )
}

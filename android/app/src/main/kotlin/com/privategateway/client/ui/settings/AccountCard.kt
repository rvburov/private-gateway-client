package com.privategateway.client.ui.settings

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.privategateway.client.R
import com.privategateway.client.account.AccountInfo
import com.privategateway.client.ui.theme.PgCard
import com.privategateway.client.ui.theme.PgColor
import com.privategateway.client.ui.theme.PgDialog
import com.privategateway.client.ui.theme.PgIcons
import com.privategateway.client.ui.theme.PgSecondaryButton
import com.privategateway.client.ui.theme.PgSpace
import com.privategateway.client.ui.theme.PgTone
import com.privategateway.client.ui.theme.PgType

/**
 * Карточка «Аккаунт» в «Настройках»: имя и почта участника сайта, «Это устройство: …» и «Выйти» с подтверждением — выход
 * выключает защиту и удаляет устройство из аккаунта, поэтому сначала вопрос (как пауза на главной: безопасное — основной
 * кнопкой).
 */
@Composable
internal fun AccountCard(account: AccountInfo, onSignOut: () -> Unit) {
    var asking by rememberSaveable { mutableStateOf(false) }
    GroupTitle(stringResource(R.string.settings_account_title), top = 0.dp)
    PgCard(Modifier.fillMaxWidth().testTag("settings.account")) {
        Column(verticalArrangement = Arrangement.spacedBy(2.dp)) {
            val name = account.displayName.ifBlank { account.email }
            Text(name, Modifier.testTag("settings.account.name"), color = PgColor.Text.Primary, style = PgType.BodyStrong, maxLines = 1, overflow = TextOverflow.Ellipsis)
            if (name != account.email && account.email.isNotEmpty()) {
                Text(account.email, Modifier.testTag("settings.account.email"), color = PgColor.Text.Secondary, style = PgType.Small, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }

            if (account.deviceName.isNotEmpty()) {
                Text(
                    stringResource(R.string.settings_account_device, account.deviceName),
                    Modifier.testTag("settings.account.device"),
                    color = PgColor.Text.Muted,
                    style = PgType.Caption,
                )
            }
        }

        Spacer(Modifier.size(PgSpace.L))
        PgSecondaryButton(
            stringResource(R.string.settings_account_signOut),
            "settings.account.signOut",
            { asking = true },
            Modifier.fillMaxWidth().padding(top = PgSpace.Xxs),
            danger = true,
        )
    }

    if (asking) {
        PgDialog(
            stringResource(R.string.dialog_signOut_title),
            stringResource(R.string.dialog_signOut_text),
            PgIcons.Warning,
            PgTone.Danger,
            stringResource(R.string.dialog_signOut_keep),
            { asking = false },
            stringResource(R.string.dialog_signOut_confirm),
            {
                asking = false
                onSignOut()
            },
            { asking = false },
            "dialog.signOut",
            secondaryDanger = true,
        )
    }
}

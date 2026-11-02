package com.privategateway.client.ui

import android.content.Context
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.assertIsNotEnabled
import androidx.compose.ui.test.assertTextContains
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performScrollTo
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.privategateway.client.R
import com.privategateway.client.core.backup.BackupServer
import com.privategateway.client.core.backup.MobileBackup
import com.privategateway.client.data.BackupParts
import com.privategateway.client.data.BackupRestored
import com.privategateway.client.ui.settings.BackupActions
import com.privategateway.client.ui.settings.BackupScreen
import com.privategateway.client.ui.settings.BackupUi
import com.privategateway.client.ui.settings.RestoreStep
import com.privategateway.client.ui.theme.LocalPgToast
import com.privategateway.client.ui.theme.PgTheme
import com.privategateway.client.ui.theme.PgToastState
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.annotation.Config
import java.time.Instant
import kotlin.test.assertEquals

/** «Резервная копия» телефона (доска 14, решение 559) без устройства: флажки, пароль, лист восстановления, итог. */
@RunWith(AndroidJUnit4::class)
@Config(qualifiers = "w384dp-h832dp")
class BackupScreenTest {
    @get:Rule
    val compose = createComposeRule()

    private val context = ApplicationProvider.getApplicationContext<Context>()

    @Test
    fun `keys go only with servers, short password is said, restore sheet shows what is inside`() {
        val ui = mutableStateOf(BackupUi())
        var saves = 0
        var restores = 0
        compose.setContent {
            PgTheme {
                CompositionLocalProvider(LocalPgToast provides PgToastState()) {
                    BackupScreen(
                        ui.value,
                        BackupActions(
                            parts = { ui.value = ui.value.copy(parts = if (it.servers) it else it.copy(keys = false)) },
                            save = { saves++ },
                            restore = { restores++ },
                        ),
                    ) {}
                }
            }
        }

        compose.onNodeWithTag("backup.subscriptions.warning").assertIsDisplayed()
        compose.onNodeWithTag("backup.keys").performClick()
        assertEquals(BackupParts(servers = true, settings = true, keys = true), ui.value.parts)
        compose.onNodeWithTag("backup.keys.warning").assertIsDisplayed()
        compose.onNodeWithTag("backup.servers").performClick()
        assertEquals(BackupParts(servers = false, settings = true, keys = false), ui.value.parts, "без серверов ключей нет")
        compose.onNodeWithTag("backup.keys").assertIsNotEnabled()

        ui.value = ui.value.copy(password = "short", tried = true)
        compose.onNodeWithTag("backup.password.short").performScrollTo().assertIsDisplayed()
        compose.onNodeWithTag("backup.save.button").performScrollTo().performClick()
        assertEquals(1, saves)

        val copy = MobileBackup(Instant.parse("2026-11-02T15:42:00Z"), servers = listOf(BackupServer("vless://example", null)), settings = emptyMap())
        ui.value = ui.value.copy(restore = RestoreStep.Ready(copy))
        compose.onNodeWithTag("backup.sheet.summary").assertTextContains(context.getString(R.string.backup_part_settings), substring = true)
        compose.onNodeWithTag("backup.sheet.restore").performClick()
        assertEquals(1, restores)

        ui.value = ui.value.copy(restore = null, restored = BackupRestored(servers = 1, subscriptions = 0, settings = true))
        compose.onNodeWithTag("backup.restored").performScrollTo().assertTextContains(context.getString(R.string.backup_part_settings), substring = true)
    }
}

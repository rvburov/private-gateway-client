package com.privategateway.client.ui

import android.content.Context
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.test.assertIsNotEnabled
import androidx.compose.ui.test.assertTextEquals
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.performClick
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.privategateway.client.R
import com.privategateway.client.core.updates.UpdateResult
import com.privategateway.client.ui.settings.AboutScreen
import com.privategateway.client.ui.settings.UpdatesView
import com.privategateway.client.ui.theme.PgTheme
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.annotation.Config
import kotlin.test.assertEquals

/** «Обновления» в «О программе» Android (DECISIONS.md, п. 8): только кнопка «Проверить», итог словами. */
@RunWith(AndroidJUnit4::class)
@Config(qualifiers = "w384dp-h832dp")
class UpdatesCardTest {
    @get:Rule
    val compose = createComposeRule()

    private val context = ApplicationProvider.getApplicationContext<Context>()

    @Test
    fun `check button asks and every result is said in words`() {
        var view by mutableStateOf(UpdatesView())
        var checks = 0
        compose.setContent { PgTheme { AboutScreen("26.10.21", {}, {}, {}, updates = view, onCheckUpdates = { checks++ }) {} } }

        val state = compose.onNodeWithTag("about.updates.state")
        state.assertTextEquals(context.getString(R.string.about_updates_manual))
        compose.onNodeWithTag("about.updates.check").performClick()
        assertEquals(1, checks)

        view = UpdatesView(checking = true)
        state.assertTextEquals(context.getString(R.string.about_updates_checking))
        compose.onNodeWithTag("about.updates.check").assertIsNotEnabled()

        view = UpdatesView(result = UpdateResult.NotSigned, checkedAt = 0)
        state.assertTextEquals(context.getString(R.string.android_updates_unsigned))

        view = UpdatesView(result = UpdateResult.Failed, checkedAt = 0)
        state.assertTextEquals(context.getString(R.string.about_updates_failed))

        val now = System.currentTimeMillis()
        view = UpdatesView(result = UpdateResult.UpToDate, checkedAt = now)
        val at = java.time.format.DateTimeFormatter.ofPattern("HH:mm").format(java.time.Instant.ofEpochMilli(now).atZone(java.time.ZoneId.systemDefault()))
        state.assertTextEquals(context.getString(R.string.about_updates_upToDate, context.getString(R.string.when_at, at)))
        compose.onNodeWithTag("about.updates.open").assertDoesNotExist()

        view = UpdatesView(result = UpdateResult.Available("0.2.0", "https://github.com/rvburov/private-gateway-client/releases/tag/v0.2.0"), checkedAt = 0)
        state.assertTextEquals(context.getString(R.string.about_updates_later, "0.2.0"))
        compose.onNodeWithTag("about.updates.open").assertTextEquals(context.getString(R.string.about_update_open))
    }
}

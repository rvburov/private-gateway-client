package com.privategateway.client.ui

import android.content.Context
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.Text
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.assertIsNotSelected
import androidx.compose.ui.test.assertIsOff
import androidx.compose.ui.test.assertIsOn
import androidx.compose.ui.test.assertIsSelected
import androidx.compose.ui.test.assertTextEquals
import androidx.compose.ui.test.click
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performTouchInput
import androidx.compose.ui.unit.dp
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.privategateway.client.R
import com.privategateway.client.core.notify.NotificationLevel
import com.privategateway.client.ui.theme.LocalPgToast
import com.privategateway.client.ui.theme.PgCard
import com.privategateway.client.ui.theme.PgCheck
import com.privategateway.client.ui.theme.PgDialog
import com.privategateway.client.ui.theme.PgIcons
import com.privategateway.client.ui.theme.PgLinkRow
import com.privategateway.client.ui.theme.PgPrimaryButton
import com.privategateway.client.ui.theme.PgSegments
import com.privategateway.client.ui.theme.PgSelect
import com.privategateway.client.ui.theme.PgSettingSwitch
import com.privategateway.client.ui.theme.PgSheet
import com.privategateway.client.ui.theme.PgTheme
import com.privategateway.client.ui.theme.PgToast
import com.privategateway.client.ui.theme.PgToastState
import com.privategateway.client.ui.theme.PgTone
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.annotation.Config
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/** Блоки дизайн-системы телефона без устройства (Robolectric): что выбирают нажатия, вопросы перед выбором, слои. Доска 24. */
@RunWith(AndroidJUnit4::class)
@Config(qualifiers = "w384dp-h832dp")
class ControlsTest {
    @get:Rule
    val compose = createComposeRule()

    private val context = ApplicationProvider.getApplicationContext<Context>()

    private fun text(id: Int) = context.getString(id)

    private fun formatted(id: Int, vararg args: Any) = context.getString(id, *args)

    private enum class Mode(val label: Int) {
        Vpn(R.string.settings_mode_vpn),
        Proxy(R.string.settings_mode_proxy),
    }

    private enum class Dns(val label: Int) {
        Profile(R.string.prot_dns_profile),
        Cloudflare(R.string.prot_dns_cloudflare),
        Quad9(R.string.prot_dns_quad9),
    }

    private fun notifyLabel(level: NotificationLevel) = when (level) {
        NotificationLevel.All -> R.string.settings_notifications_all
        NotificationLevel.ImportantOnly -> R.string.settings_notifications_important
        NotificationLevel.Off -> R.string.settings_notifications_off
    }

    @Test
    fun `segment changes the choice by tap`() {
        var level by mutableStateOf(NotificationLevel.ImportantOnly)
        var calls = 0
        compose.setContent {
            PgTheme {
                PgSegments(NotificationLevel.entries, level, { stringResource(notifyLabel(it)) }, "notify", {
                    calls++
                    level = it
                })
            }
        }

        compose.onNodeWithText(text(R.string.settings_notifications_all)).assertIsDisplayed()
        compose.onNodeWithTag("notify.ImportantOnly").assertIsSelected()
        // Нажатие на выбранный сегмент ничего не меняет.
        compose.onNodeWithTag("notify.ImportantOnly").performClick()
        assertEquals(0, calls)

        compose.onNodeWithTag("notify.Off").performClick()
        assertEquals(NotificationLevel.Off, level)
        assertEquals(1, calls)
        compose.onNodeWithTag("notify.Off").assertIsSelected()
        compose.onNodeWithTag("notify.ImportantOnly").assertIsNotSelected()
    }

    @Test
    fun `segment with a question changes only after yes`() {
        var mode by mutableStateOf(Mode.Vpn)
        compose.setContent {
            PgTheme {
                // Как «Только прокси» (решение 529): на прокси — через диалог, обратно на VPN — сразу.
                var pending by remember { mutableStateOf<(() -> Unit)?>(null) }
                PgSegments(Mode.entries, mode, { stringResource(it.label) }, "mode", { mode = it }, confirm = { value, apply ->
                    if (value == Mode.Proxy) {
                        pending = apply
                    } else {
                        apply()
                    }
                })
                pending?.let { apply ->
                    PgDialog(
                        stringResource(R.string.dialog_proxyMode_title),
                        stringResource(R.string.dialog_proxyMode_text),
                        PgIcons.Shield,
                        PgTone.Warning,
                        stringResource(R.string.dialog_proxyMode_keep),
                        { pending = null },
                        stringResource(R.string.dialog_proxyMode_switch),
                        {
                            pending = null
                            apply()
                        },
                        { pending = null },
                        "dialog.proxy",
                        detail = PgIcons.MarkAlert,
                        secondaryDanger = true,
                    )
                }
            }
        }

        compose.onNodeWithTag("mode.Proxy").performClick()
        compose.onNodeWithTag("dialog.proxy").assertIsDisplayed()
        compose.onNodeWithText(text(R.string.dialog_proxyMode_title)).assertIsDisplayed()
        compose.onNodeWithTag("mode.Vpn").assertIsSelected()
        assertEquals(Mode.Vpn, mode, "до ответа режим прежний")

        // «Оставить VPN» — режим не меняется, диалог закрыт.
        compose.onNodeWithTag("dialog.proxy.primary").assertTextEquals(text(R.string.dialog_proxyMode_keep)).performClick()
        compose.onNodeWithTag("dialog.proxy").assertDoesNotExist()
        compose.onNodeWithTag("mode.Vpn").assertIsSelected()
        assertEquals(Mode.Vpn, mode)

        compose.onNodeWithTag("mode.Proxy").performClick()
        compose.onNodeWithTag("dialog.proxy.secondary").assertTextEquals(text(R.string.dialog_proxyMode_switch)).performClick()
        compose.onNodeWithTag("dialog.proxy").assertDoesNotExist()
        compose.onNodeWithTag("mode.Proxy").assertIsSelected()
        assertEquals(Mode.Proxy, mode)

        compose.onNodeWithTag("mode.Vpn").performClick()
        compose.onNodeWithTag("dialog.proxy").assertDoesNotExist()
        assertEquals(Mode.Vpn, mode)
    }

    @Test
    fun `menu option is chosen and the menu closes`() {
        var dns by mutableStateOf(Dns.Profile)
        compose.setContent { PgTheme { PgSelect(Dns.entries, dns, { stringResource(it.label) }, "dns", { dns = it }) } }

        compose.onNodeWithTag("dns").assertTextEquals(text(R.string.prot_dns_profile))
        compose.onNodeWithTag("dns.menu").assertDoesNotExist()
        compose.onNodeWithTag("dns").performClick()
        // Закрытое меню не составляется вовсе (PgMenu), поэтому «есть» — значит открыто; видимость окна Popup под Robolectric
        // не проверяем.
        compose.onNodeWithTag("dns.menu").assertExists()
        compose.onNodeWithTag("dns.Profile").assertIsSelected()
        compose.onNodeWithTag("dns.Quad9").assertIsNotSelected().performClick()

        assertEquals(Dns.Quad9, dns)
        compose.onNodeWithTag("dns.menu").assertDoesNotExist()
        compose.onNodeWithTag("dns").assertTextEquals(text(R.string.prot_dns_quad9))

        // Кнопка-владелец сама переключает меню: второе нажатие закрывает его без выбора.
        compose.onNodeWithTag("dns").performClick()
        compose.onNodeWithTag("dns.Quad9").assertIsSelected()
        compose.onNodeWithTag("dns").performClick()
        compose.onNodeWithTag("dns.menu").assertDoesNotExist()
        assertEquals(Dns.Quad9, dns)
    }

    @Test
    fun `check toggles by tap on the whole row`() {
        var on by mutableStateOf(false)
        compose.setContent { PgTheme { PgCheck(text(R.string.backup_servers_title), on, "backup.servers", { on = it }) } }

        compose.onNodeWithTag("backup.servers").assertIsOff().performClick()
        assertTrue(on)
        compose.onNodeWithTag("backup.servers").assertIsOn()
        compose.onNodeWithText(text(R.string.backup_servers_title)).performClick()
        assertFalse(on)
        compose.onNodeWithTag("backup.servers").assertIsOff()
    }

    @Test
    fun `switch row asks before turning off and stays on when kept`() {
        var on by mutableStateOf(true)
        compose.setContent {
            PgTheme {
                var pending by remember { mutableStateOf<(() -> Unit)?>(null) }
                PgSettingSwitch(text(R.string.settings_ks_title), text(R.string.settings_ks_text), on, "ks", { on = it }, confirm = { value, apply ->
                    if (value) {
                        apply()
                    } else {
                        pending = apply
                    }
                })
                pending?.let { apply ->
                    PgDialog(
                        text(R.string.dialog_ksOff_title),
                        text(R.string.dialog_ksOff_text),
                        PgIcons.Shield,
                        PgTone.Warning,
                        text(R.string.dialog_ksOff_keep),
                        { pending = null },
                        text(R.string.dialog_ksOff_drop),
                        {
                            pending = null
                            apply()
                        },
                        { pending = null },
                        "dialog.ks",
                        detail = PgIcons.MarkAlert,
                        secondaryDanger = true,
                    )
                }
            }
        }

        compose.onNodeWithTag("ks").assertIsOn().performClick()
        compose.onNodeWithTag("dialog.ks").assertIsDisplayed()
        compose.onNodeWithTag("ks").assertIsOn()
        compose.onNodeWithTag("dialog.ks.primary").performClick()
        compose.onNodeWithTag("dialog.ks").assertDoesNotExist()
        compose.onNodeWithTag("ks").assertIsOn()
        assertTrue(on, "«Оставить включенным» возвращает тумблер")

        compose.onNodeWithTag("ks").performClick()
        compose.onNodeWithTag("dialog.ks.secondary").performClick()
        compose.onNodeWithTag("ks").assertIsOff()
        assertFalse(on)

        // Включить — без вопроса.
        compose.onNodeWithTag("ks").performClick()
        compose.onNodeWithTag("dialog.ks").assertDoesNotExist()
        assertTrue(on)
    }

    @Test
    fun `dialog calls the tapped button and closes by the scrim, not by its own card`() {
        val calls = mutableListOf<String>()
        compose.setContent {
            PgTheme {
                PgDialog(
                    formatted(R.string.dialog_deleteServer_title, "Нидерланды 01"),
                    text(R.string.dialog_deleteServer_textInUse),
                    PgIcons.Trash,
                    PgTone.Danger,
                    text(R.string.dialog_deleteServer_delete),
                    { calls += "delete" },
                    text(R.string.dialog_cancel),
                    { calls += "cancel" },
                    { calls += "dismiss" },
                    "dialog.delete",
                    primaryDanger = true,
                )
            }
        }

        compose.onNodeWithText(formatted(R.string.dialog_deleteServer_title, "Нидерланды 01")).assertIsDisplayed()
        compose.onNodeWithText(text(R.string.dialog_deleteServer_textInUse)).assertIsDisplayed()
        compose.onNodeWithTag("dialog.delete.primary").performClick()
        compose.onNodeWithTag("dialog.delete.secondary").performClick()
        assertEquals(listOf("delete", "cancel"), calls)

        // Нажатие на саму карточку (у значка, мимо кнопок) диалог не закрывает, на затемнение над ней — закрывает.
        compose.onNodeWithTag("dialog.delete").performTouchInput { click(Offset(width - 4f, 4f)) }
        assertEquals(listOf("delete", "cancel"), calls)
        compose.onNodeWithTag("dialog.delete.scrim").performTouchInput { click(Offset(width / 2f, 4f)) }
        assertEquals(listOf("delete", "cancel", "dismiss"), calls)
    }

    @Test
    fun `sheet shows its content and closes by the scrim above it`() {
        var shown by mutableStateOf(true)
        compose.setContent {
            PgTheme {
                if (shown) {
                    PgSheet({ shown = false }, "sheet.dns") {
                        Text(text(R.string.prot_dnsCustom_title))
                    }
                }
            }
        }

        compose.onNodeWithText(text(R.string.prot_dnsCustom_title)).assertIsDisplayed()
        // Нажатие на сам лист (у ручки) его не закрывает, на затемнение над листом — закрывает.
        compose.onNodeWithTag("sheet.dns").performTouchInput { click(Offset(width / 2f, 4f)) }
        assertTrue(shown)
        compose.onNodeWithTag("sheet.dns.scrim").performTouchInput { click(Offset(width / 2f, 4f)) }
        assertFalse(shown)
        compose.onNodeWithTag("sheet.dns").assertDoesNotExist()
    }

    @Test
    fun `toast shows from any screen and goes away by itself after 1,8 s`() {
        val toast = PgToastState()
        compose.setContent {
            PgTheme {
                CompositionLocalProvider(LocalPgToast provides toast) {
                    Box(Modifier.fillMaxSize()) {
                        // Экран не знает хоста: подсказку берет из LocalPgToast.
                        val shown = LocalPgToast.current
                        PgPrimaryButton(text(R.string.ip_copied), "copy", { shown.show(text(R.string.ip_copied)) }, Modifier.align(Alignment.Center))
                        PgToast(toast, Modifier.align(Alignment.TopCenter))
                    }
                }
            }
        }

        compose.onNodeWithTag("toast").assertDoesNotExist()
        compose.onNodeWithTag("copy").performClick()
        compose.onNodeWithTag("toast").assertIsDisplayed().assertTextEquals(text(R.string.ip_copied))

        compose.mainClock.advanceTimeBy(1_000)
        compose.onNodeWithTag("toast").assertIsDisplayed()
        // Новая подсказка — отсчет заново. Нажатие только меняет состояние; отсчет новой подсказки начнется, когда окно ее
        // покажет, — без ожидания здесь он ушел бы за advanceTimeBy и проверка ниже зависела бы от того, как clickable
        // зовет onClick.
        compose.onNodeWithTag("copy").performClick()
        compose.waitForIdle()
        compose.mainClock.advanceTimeBy(1_000)
        compose.onNodeWithTag("toast").assertIsDisplayed()

        compose.mainClock.advanceTimeBy(1_000)
        compose.onNodeWithTag("toast").assertDoesNotExist()
    }

    @Test
    fun `long toast stays for 6 s`() {
        val toast = PgToastState()
        compose.setContent { PgTheme { PgToast(toast) } }

        compose.onNodeWithTag("toast").assertDoesNotExist()
        toast.show(text(R.string.server_refreshed), long = true)
        compose.onNodeWithTag("toast").assertTextEquals(text(R.string.server_refreshed))
        compose.mainClock.advanceTimeBy(5_000)
        compose.onNodeWithTag("toast").assertIsDisplayed()
        compose.mainClock.advanceTimeBy(1_500)
        compose.onNodeWithTag("toast").assertDoesNotExist()
    }

    @Test
    fun `link row opens its page, a disabled one does not`() {
        val opened = mutableListOf<String>()
        compose.setContent {
            PgTheme {
                PgCard(contentPadding = PaddingValues(vertical = 4.dp)) {
                    PgLinkRow(
                        PgIcons.AdvProtection,
                        text(R.string.settings_adv_protection_title),
                        text(R.string.settings_adv_protection_text),
                        "settings.protection",
                        { opened += "protection" },
                    )
                    PgLinkRow(
                        PgIcons.AdvProfiles,
                        text(R.string.settings_adv_profiles_title),
                        text(R.string.settings_adv_profiles_text),
                        "settings.profiles",
                        { opened += "profiles" },
                        enabled = false,
                        divider = true,
                    )
                }
            }
        }

        compose.onNodeWithText(text(R.string.settings_adv_protection_text)).assertIsDisplayed()
        compose.onNodeWithTag("settings.protection").performClick()
        compose.onNodeWithTag("settings.profiles").performClick()
        assertEquals(listOf("protection"), opened)
    }
}

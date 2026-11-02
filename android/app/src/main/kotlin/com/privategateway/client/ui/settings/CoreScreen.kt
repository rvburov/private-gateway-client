package com.privategateway.client.ui.settings

import androidx.annotation.StringRes
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
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
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import com.privategateway.client.R
import com.privategateway.client.core.settings.ConnectionSettings
import com.privategateway.client.core.settings.CoreLogLevel
import com.privategateway.client.core.settings.TlsFingerprint
import com.privategateway.client.core.settings.TunnelSettings
import com.privategateway.client.core.vpn.ReconnectPolicy
import com.privategateway.client.ui.theme.PgCard
import com.privategateway.client.ui.theme.PgColor
import com.privategateway.client.ui.theme.PgInput
import com.privategateway.client.ui.theme.PgLayout
import com.privategateway.client.ui.theme.PgMenu
import com.privategateway.client.ui.theme.PgMenuOption
import com.privategateway.client.ui.theme.PgPageHeader
import com.privategateway.client.ui.theme.PgPrimaryButton
import com.privategateway.client.ui.theme.PgSecondaryButton
import com.privategateway.client.ui.theme.PgSelect
import com.privategateway.client.ui.theme.PgSelectButton
import com.privategateway.client.ui.theme.PgSetting
import com.privategateway.client.ui.theme.PgSettingSwitch
import com.privategateway.client.ui.theme.PgSheet
import com.privategateway.client.ui.theme.PgSpace
import com.privategateway.client.ui.theme.PgType

/**
 * «Подключение и ядро» телефона (доска 6): шапка с «Назад» стоит, прокручивается список. Группы — «Сетевое ядро» (Xray-core с
 * версией, Hysteria2 и TUIC — у телефона их ядра нет, журнал ядра), «TUN (режим VPN)» (MTU с листом «Свой MTU», строгие
 * маршруты, определение домена) и «Соединение» (отпечаток TLS, Mux, фрагментация TLS, таймаут подключения, переподключение).
 * Только то, что ядро и туннель телефона выполняют: «Стека TUN» нет — вход TUN Xray выбора стека не дает; группы «Локальный
 * прокси» нет — на телефоне только VPN (решение 527). Под защитой изменения применяет служба туннеля — переподключением без
 * окна утечки; таймаут и переподключение действуют со следующей пробы связи.
 */
@Composable
fun CoreScreen(
    core: String?,
    loadCore: () -> Unit,
    tunnel: TunnelSettings,
    onTunnel: (TunnelSettings) -> Unit,
    behavior: Behavior,
    onBehavior: (Behavior) -> Unit,
    onBack: () -> Unit,
) {
    LaunchedEffect(Unit) { loadCore() }
    var editingMtu by rememberSaveable { mutableStateOf(false) }
    val connection = tunnel.connection
    val change: (ConnectionSettings) -> Unit = { onTunnel(tunnel.copy(connection = it)) }
    Column(Modifier.fillMaxSize().statusBarsPadding().testTag("core")) {
        Box(Modifier.padding(horizontal = PgSpace.WindowSideMobile)) {
            PgPageHeader(stringResource(R.string.settings_adv_core_title), onBack)
        }

        Column(
            Modifier
                .fillMaxWidth()
                .weight(1f)
                .verticalScroll(rememberScrollState())
                .padding(start = PgSpace.WindowSideMobile, top = PgSpace.L, end = PgSpace.WindowSideMobile, bottom = PgLayout.ScrollBottomPadding),
        ) {
            GroupTitle(stringResource(R.string.core_group_core), top = 0.dp)
            PgCard(Modifier.fillMaxWidth(), contentPadding = CoreCard) {
                // Версию сообщает само ядро в приложении; пока она не прочитана — «—», как в подписи «Настроек».
                PgSetting(stringResource(R.string.core_xray_title), stringResource(R.string.core_xray_text), padding = CoreRows) {
                    ValueBox(core ?: "—", "core.xray.version")
                }
                // Hysteria2 и TUIC у десктопа ведет sing-box; у телефона второго ядра нет (решение 423): они не подключаются —
                // подпись та же, что в карточке такого сервера, с заглавной буквы (как у iOS).
                val locale = LocalConfiguration.current.locales[0]
                val second = stringResource(R.string.android_card_core_none).replaceFirstChar { it.titlecase(locale) }
                PgSetting(stringResource(R.string.core_second_title), second, divider = true, padding = CoreRows) {
                    ValueBox(stringResource(R.string.core_version_none), "core.second.version")
                }
                PgSetting(stringResource(R.string.core_log_title), stringResource(R.string.core_log_text), divider = true, padding = CoreRows) {
                    PgSelect(
                        CoreLogLevel.entries,
                        connection.coreLog,
                        { stringResource(logLabel(it)) },
                        "core.log",
                        { change(connection.copy(coreLog = it)) },
                        Modifier.widthIn(max = CoreSelectWidth),
                    )
                }
            }

            GroupTitle(stringResource(R.string.core_group_tun), top = PgSpace.Card)
            PgCard(Modifier.fillMaxWidth(), contentPadding = CoreCard) {
                PgSetting(stringResource(R.string.core_mtu_title), stringResource(R.string.core_mtu_text), padding = CoreRows) {
                    MtuSelect(connection.mtu, { change(connection.copy(mtu = it)) }) { editingMtu = true }
                }
                PgSettingSwitch(
                    stringResource(R.string.core_strict_title),
                    stringResource(R.string.core_strict_text),
                    connection.strictRoutes,
                    "core.strict",
                    { change(connection.copy(strictRoutes = it)) },
                    divider = true,
                    padding = CoreRows,
                )
                PgSettingSwitch(
                    stringResource(R.string.core_sniff_title),
                    stringResource(R.string.core_sniff_text),
                    connection.sniffing,
                    "core.sniff",
                    { change(connection.copy(sniffing = it)) },
                    divider = true,
                    padding = CoreRows,
                )
            }

            GroupTitle(stringResource(R.string.core_group_conn), top = PgSpace.Card)
            PgCard(Modifier.fillMaxWidth(), contentPadding = CoreCard) {
                PgSetting(stringResource(R.string.core_fp_title), stringResource(R.string.core_fp_text), padding = CoreRows) {
                    PgSelect(
                        TlsFingerprint.entries,
                        connection.fingerprint,
                        { it.xray ?: stringResource(R.string.core_fp_profile) },
                        "core.fp",
                        { change(connection.copy(fingerprint = it)) },
                        Modifier.widthIn(max = CoreSelectWidth),
                    )
                }
                PgSettingSwitch(
                    stringResource(R.string.core_mux_title),
                    stringResource(R.string.core_mux_text),
                    connection.mux,
                    "core.mux",
                    { change(connection.copy(mux = it)) },
                    divider = true,
                    padding = CoreRows,
                )
                PgSettingSwitch(
                    stringResource(R.string.core_frag_title),
                    stringResource(R.string.core_frag_text),
                    connection.tlsFragment,
                    "core.frag",
                    { change(connection.copy(tlsFragment = it)) },
                    divider = true,
                    padding = CoreRows,
                )
                PgSetting(stringResource(R.string.core_timeout_title), stringResource(R.string.core_timeout_text), divider = true, padding = CoreRows) {
                    PgSelect(
                        ConnectionSettings.TIMEOUT_CHOICES,
                        connection.connectTimeoutSeconds,
                        { stringResource(R.string.core_timeout_value, it.toString()) },
                        "core.timeout",
                        { change(connection.copy(connectTimeoutSeconds = it)) },
                        Modifier.widthIn(max = CoreSelectWidth),
                    )
                }
                // Переподключение — только к выбранному серверу; хранится отдельно от туннеля (Behavior): ядро оно не меняет.
                PgSetting(stringResource(R.string.core_reconnect_title), stringResource(R.string.core_reconnect_text), divider = true, padding = CoreRows) {
                    PgSelect(
                        ReconnectPolicy.entries,
                        behavior.reconnect,
                        { stringResource(reconnectLabel(it)) },
                        "reconnect",
                        { onBehavior(behavior.copy(reconnect = it)) },
                        Modifier.widthIn(max = CoreSelectWidth),
                    )
                }
            }
        }
    }

    if (editingMtu) {
        MtuSheet(
            initial = connection.mtu,
            onSave = { mtu ->
                editingMtu = false
                change(connection.copy(mtu = mtu))
            },
            onClose = { editingMtu = false },
        )
    }
}

/** Поля карточек доски 6 (Border.card Padding="16,2"): строки без боковых полей, полосы — внутри полей карточки. */
private val CoreCard = PaddingValues(horizontal = PgSpace.Card, vertical = 2.dp)

/** Строка карточки доски 6: 12 сверху и снизу. */
private val CoreRows = PaddingValues(vertical = PgSpace.L)

/** Кнопка-список в строке не шире 180 (MaxWidth десктопа): длинное значение — с многоточием. */
private val CoreSelectWidth = 180.dp

/**
 * MTU: кнопка-список с выбранным («Авто» или число) и меню «Авто · 1500 · 1420 · 1280 · Свое…», выбранный пункт — синим
 * полужирным. «Свое…» открывает лист [onCustom] и выбранным становится, только когда свое число сохранено (как у десктопа).
 * Теги: кнопка — «core.mtu», меню — «core.mtu.menu», пункты — «core.mtu.auto», «core.mtu.1500»…, «core.mtu.custom».
 */
@Composable
private fun MtuSelect(mtu: Int?, onChoose: (Int?) -> Unit, onCustom: () -> Unit) {
    var open by remember { mutableStateOf(false) }
    val custom = mtu != null && mtu !in ConnectionSettings.MTU_CHOICES
    Box(Modifier.widthIn(max = CoreSelectWidth)) {
        PgSelectButton(mtuText(mtu), "core.mtu", { open = !open }, open = open)
        PgMenu(open, { open = false }, "core.mtu.menu") {
            for (choice in ConnectionSettings.MTU_CHOICES) {
                PgMenuOption(mtuText(choice), choice == mtu, "core.mtu.${choice ?: "auto"}", {
                    open = false
                    if (choice != mtu) {
                        onChoose(choice)
                    }
                })
            }

            PgMenuOption(stringResource(R.string.core_mtu_custom), custom, "core.mtu.custom", {
                open = false
                onCustom()
            })
        }
    }
}

@Composable
private fun mtuText(mtu: Int?): String = mtu?.toString() ?: stringResource(R.string.core_mtu_auto)

/**
 * Лист «Свой MTU» (MtuView десктопа): заголовок и крестик, поле «MTU» моноширинным с цифровой клавиатурой (не больше 4
 * знаков), подпись о границах; внизу «Отмена» и «Сохранить». Число вне 1280–1500 не сохраняется — лист говорит, какое нужно.
 */
@Composable
private fun MtuSheet(initial: Int?, onSave: (Int) -> Unit, onClose: () -> Unit) {
    var text by rememberSaveable { mutableStateOf(initial?.toString().orEmpty()) }
    var tried by rememberSaveable { mutableStateOf(false) }
    val value = ConnectionSettings.parseMtu(text)
    PgSheet(onClose, "core.mtu.sheet", scroll = false) {
        SheetHeader(stringResource(R.string.core_mtuCustom_title), onClose, "core.mtu.close")
        Column(
            Modifier
                .fillMaxWidth()
                .weight(1f)
                .verticalScroll(rememberScrollState())
                .padding(vertical = PgSpace.Card),
            verticalArrangement = Arrangement.spacedBy(PgSpace.Xs),
        ) {
            val label = stringResource(R.string.core_mtu_title)
            FieldLabel(label)
            PgInput(
                text,
                {
                    if (it.length <= MTU_DIGITS) {
                        text = it
                        tried = false
                    }
                },
                label,
                "core.mtu.text",
                placeholder = stringResource(R.string.core_mtu_placeholder),
                style = MonoField,
                keyboard = KeyboardOptions(keyboardType = KeyboardType.Number),
            )
            if (tried && value == null) {
                Text(stringResource(R.string.core_mtuCustom_bad), Modifier.testTag("core.mtu.bad"), color = PgColor.Status.Danger.Text, style = PgType.Caption)
            } else {
                Text(stringResource(R.string.core_mtuCustom_note), color = PgColor.Text.Muted, style = PgType.Caption)
            }
        }

        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(PgSpace.S)) {
            PgSecondaryButton(stringResource(R.string.dialog_cancel), "core.mtu.cancel", onClose, Modifier.weight(1f), height = 50.dp)
            PgPrimaryButton(
                stringResource(R.string.subs_edit_save),
                "core.mtu.save",
                {
                    tried = true
                    value?.let(onSave)
                },
                Modifier.weight(1f),
                height = 50.dp,
            )
        }
    }
}

/** MTU — не больше четырех знаков (maxlength поля доски 6). */
private const val MTU_DIGITS = 4

@StringRes
private fun logLabel(level: CoreLogLevel): Int = when (level) {
    CoreLogLevel.Warning -> R.string.core_log_warning
    CoreLogLevel.Info -> R.string.core_log_info
    CoreLogLevel.Debug -> R.string.core_log_debug
}

@StringRes
private fun reconnectLabel(policy: ReconnectPolicy): Int = when (policy) {
    ReconnectPolicy.Backoff60Seconds -> R.string.core_reconnect_backoff
    ReconnectPolicy.UpTo5Minutes -> R.string.core_reconnect_5min
    ReconnectPolicy.Never -> R.string.core_reconnect_never
}

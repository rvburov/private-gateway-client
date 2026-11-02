package com.privategateway.client.ui.settings

import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.scale
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import com.privategateway.client.R
import com.privategateway.client.core.settings.DnsChoice
import com.privategateway.client.core.settings.TunnelSettings
import com.privategateway.client.core.vpn.SystemVpn
import com.privategateway.client.core.vpn.SystemVpnSource
import com.privategateway.client.core.vpn.SystemVpnView
import com.privategateway.client.ui.theme.PgCard
import com.privategateway.client.ui.theme.PgColor
import com.privategateway.client.ui.theme.PgIcon
import com.privategateway.client.ui.theme.PgIcons
import com.privategateway.client.ui.theme.PgInput
import com.privategateway.client.ui.theme.PgLayout
import com.privategateway.client.ui.theme.PgMenu
import com.privategateway.client.ui.theme.PgMenuOption
import com.privategateway.client.ui.theme.PgPageHeader
import com.privategateway.client.ui.theme.PgPrimaryButton
import com.privategateway.client.ui.theme.PgRadius
import com.privategateway.client.ui.theme.PgSecondaryButton
import com.privategateway.client.ui.theme.PgSelect
import com.privategateway.client.ui.theme.PgSelectButton
import com.privategateway.client.ui.theme.PgSetting
import com.privategateway.client.ui.theme.PgSettingSwitch
import com.privategateway.client.ui.theme.PgSheet
import com.privategateway.client.ui.theme.PgSpace
import com.privategateway.client.ui.theme.PgType
import com.privategateway.client.ui.theme.pgTopDivider

/**
 * «Защита и утечки» для телефона (доска 5): шапка с «Назад» стоит, прокручивается список. Группы — «Блок при обрыве» (что
 * сообщил Android о «Постоянной VPN» и блоке без VPN и переход в его настройки: включает их только пользователь), DNS в
 * туннеле (меню, «Свой адрес…» — нижний лист), IPv6 и локальная сеть. В «Блоке при обрыве» — «Вход в Wi-Fi отеля или кафе»
 * («Открыть» — вопрос о паузе на 5 минут, решение 557). Строк, которых телефон не умеет, нет: строгий режим приложения, блок DNS
 * провайдера, FakeDNS, автопроверка утечек, обнаружение устройств и работа рядом с другим VPN (на телефоне VPN один). Под защитой изменения применяет служба туннеля — переподключением без окна утечки.
 */
@Composable
fun ProtectionScreen(
    tunnel: TunnelSettings,
    onChange: (TunnelSettings) -> Unit,
    onBack: () -> Unit,
    system: SystemVpnView = SystemVpnView(SystemVpn(), SystemVpnSource.Unknown),
    onCaptive: () -> Unit = {},
    openVpnSettings: () -> Unit = {},
) {
    var editingDns by rememberSaveable { mutableStateOf(false) }
    Column(Modifier.fillMaxSize().statusBarsPadding().testTag("protection")) {
        Box(Modifier.padding(horizontal = PgSpace.WindowSideMobile)) {
            PgPageHeader(stringResource(R.string.settings_adv_protection_title), onBack)
        }

        Column(
            Modifier
                .fillMaxWidth()
                .weight(1f)
                .verticalScroll(rememberScrollState())
                .padding(start = PgSpace.WindowSideMobile, top = PgSpace.L, end = PgSpace.WindowSideMobile, bottom = PgLayout.ScrollBottomPadding),
        ) {
            GroupTitle(stringResource(R.string.prot_group_ks), top = 0.dp)
            PgCard(Modifier.fillMaxWidth(), contentPadding = GroupPadding) {
                KillSwitch(system, openVpnSettings)
                PgSetting(stringResource(R.string.prot_captive_title), stringResource(R.string.prot_captive_text), divider = true, padding = Rows) {
                    PgSecondaryButton(stringResource(R.string.prot_captive_open), "prot.captive", onCaptive)
                }
            }

            GroupTitle(stringResource(R.string.prot_group_dns), top = PgSpace.Card)
            PgCard(Modifier.fillMaxWidth(), contentPadding = GroupPadding) {
                PgSetting(stringResource(R.string.prot_dns_title), stringResource(R.string.prot_dns_text), padding = Rows) {
                    DnsSelect(tunnel, { onChange(tunnel.copy(dns = it)) }) { editingDns = true }
                }
            }

            GroupTitle(stringResource(R.string.prot_group_ipv6), top = PgSpace.Card)
            PgCard(Modifier.fillMaxWidth(), contentPadding = GroupPadding) {
                // Мимо туннеля IPv6 не уходит никогда: без «IPv6 через сервер» у туннеля нет ни адреса, ни маршрута IPv6, и
                // Android сам закрывает IPv6 всем программам (решение 397), с ним — IPv6 идет в туннель. Выключать нечего:
                // переключатель включен и недоступен.
                PgSettingSwitch(
                    stringResource(R.string.prot_ipv6_title),
                    stringResource(R.string.prot_ipv6_text),
                    true,
                    "ipv6.block",
                    {},
                    enabled = false,
                    padding = Rows,
                )
                PgSetting(stringResource(R.string.prot_addr_title), stringResource(R.string.prot_addr_text), divider = true, padding = Rows) {
                    PgSelect(
                        AddressChoice.entries,
                        AddressChoice.of(tunnel.ipv6),
                        { stringResource(it.label) },
                        "addr",
                        { onChange(tunnel.copy(ipv6 = it.ipv6)) },
                        Modifier.widthIn(max = SelectWidth),
                    )
                }
            }

            GroupTitle(stringResource(R.string.prot_group_lan), top = PgSpace.Card)
            PgCard(Modifier.fillMaxWidth(), contentPadding = GroupPadding) {
                PgSettingSwitch(
                    stringResource(R.string.prot_lan_title),
                    stringResource(R.string.prot_lan_text),
                    tunnel.allowLan,
                    "lan",
                    { onChange(tunnel.copy(allowLan = it)) },
                    padding = Rows,
                )
            }
        }
    }

    if (editingDns) {
        CustomDnsSheet(
            initial = tunnel.customDns.orEmpty(),
            onSave = { address ->
                editingDns = false
                onChange(tunnel.copy(dns = DnsChoice.Custom, customDns = address))
            },
            onClose = { editingDns = false },
        )
    }
}

/** Поля карточек доски 5 (Border.card Padding="16,2"): строки без боковых полей, полосы — внутри полей карточки. */
private val GroupPadding = PaddingValues(horizontal = PgSpace.Card, vertical = 2.dp)

/** Строка карточки доски 5: 12 сверху и снизу. */
private val Rows = PaddingValues(vertical = PgSpace.L)

/** Кнопка-список в строке не шире 180 (MaxWidth десктопа): длинное значение — с многоточием. */
private val SelectWidth = 180.dp

/** Адрес DNS в поле листа — моноширинным размера caption. */
private val AddressStyle = PgType.Mono.copy(fontSize = PgType.Caption.fontSize, fontWeight = FontWeight.Normal)

/**
 * «Приоритет адресов» телефона в порядке макета: IPv6 через сервер, IPv4 первым, — или только IPv4 (по умолчанию, решение
 * 397). «Предпочитать IPv6» нет: адрес для соединения выбирает сама программа телефона, а у туннеля адрес IPv6 частный
 * (fd66:…), и Android при нем ставит IPv4 первым — пункт ничего бы не менял.
 */
private enum class AddressChoice(val ipv6: Boolean, val label: Int) {
    PreferIpv4(true, R.string.prot_addr_ipv4),
    Ipv4Only(false, R.string.prot_addr_only4),
    ;

    companion object {
        fun of(ipv6: Boolean): AddressChoice = if (ipv6) PreferIpv4 else Ipv4Only
    }
}

/**
 * «Блок при обрыве» на Android: Kill Switch — «Блокировать подключения без VPN», на месте «Защиты при загрузке» десктопа —
 * «Постоянная VPN» (поднимает защиту после перезагрузки, решение 537). Значения — как сообщил Android, откуда они известны, —
 * строкой под ними; нажатие на строку и кнопка открывают настройки VPN телефона. Строгого режима отдельно нет: блок без VPN
 * Android держит и после ручного отключения.
 */
@Composable
private fun KillSwitch(system: SystemVpnView, openVpnSettings: () -> Unit) {
    SystemVpnRow(
        stringResource(R.string.prot_ks_title),
        stringResource(R.string.android_check_ks_text),
        system.flags.lockdown,
        "prot.ks.value",
        "prot.ks",
        openVpnSettings,
        Rows,
    )
    SystemVpnRow(
        stringResource(R.string.android_settings_vpn_alwaysOn),
        null,
        system.flags.alwaysOn,
        "prot.alwaysOn.value",
        "prot.alwaysOn",
        openVpnSettings,
        Rows,
        divider = true,
    )
    Column(
        Modifier.fillMaxWidth().pgTopDivider(true).padding(Rows),
        verticalArrangement = Arrangement.spacedBy(PgSpace.M),
    ) {
        SystemVpnSourceText(system.source, "prot.ks.source")
        OpenVpnSettingsButton(openVpnSettings, "prot.ks.open")
    }
}

/**
 * «DNS в туннеле»: кнопка-список с выбранным (свой адрес — сам адрес) и меню «Из профиля · DoH: Cloudflare · DoH: Quad9 ·
 * Свой адрес…», выбранный пункт — синим полужирным. «Свой адрес…» открывает лист [onCustom] и сам выбор не меняет: им станет
 * адрес после «Сохранить» (как ChooseDns десктопа). Теги: кнопка — «dns», меню — «dns.menu», пункты — «dns.<выбор>».
 */
@Composable
private fun DnsSelect(tunnel: TunnelSettings, onChoose: (DnsChoice) -> Unit, onCustom: () -> Unit) {
    var open by remember { mutableStateOf(false) }
    val custom = tunnel.customDns?.trim()?.takeIf { tunnel.dns == DnsChoice.Custom && TunnelSettings.validCustomDns(it) }
    Box(Modifier.widthIn(max = SelectWidth)) {
        PgSelectButton(custom ?: dnsText(tunnel.dns), "dns", { open = !open }, open = open)
        PgMenu(open, { open = false }, "dns.menu") {
            for (choice in DnsChoice.entries) {
                PgMenuOption(dnsText(choice), tunnel.dns == choice, "dns.$choice", {
                    open = false
                    if (choice == DnsChoice.Custom) {
                        onCustom()
                    } else if (choice != tunnel.dns) {
                        onChoose(choice)
                    }
                })
            }
        }
    }
}

@Composable
private fun dnsText(choice: DnsChoice): String = stringResource(
    when (choice) {
        DnsChoice.Profile -> R.string.prot_dns_profile
        DnsChoice.CloudflareDoh -> R.string.prot_dns_cloudflare
        DnsChoice.Quad9Doh -> R.string.prot_dns_quad9
        DnsChoice.Custom -> R.string.prot_dns_custom
    },
)

/**
 * Лист «Свой адрес DNS» (DnsAddressView десктопа): заголовок и крестик, поле «Адрес» моноширинным, подсказка и подпись, внизу
 * «Отмена» и «Сохранить». Годится IP или DoH по IP (https://IP/…): DoH по имени ядру пришлось бы искать мимо туннеля, поэтому
 * подсказка и подпись — телефона. Неверный адрес не сохраняется — ядру молча ушел бы DNS по умолчанию.
 */
@Composable
private fun CustomDnsSheet(initial: String, onSave: (String) -> Unit, onClose: () -> Unit) {
    var text by rememberSaveable { mutableStateOf(initial) }
    var tried by rememberSaveable { mutableStateOf(false) }
    val valid = TunnelSettings.validCustomDns(text)
    PgSheet(onClose, "dns.sheet", scroll = false) {
        Row(Modifier.fillMaxWidth().padding(top = 2.dp), verticalAlignment = Alignment.CenterVertically) {
            Text(stringResource(R.string.prot_dnsCustom_title), Modifier.weight(1f), color = PgColor.Text.Primary, style = PgType.Status)
            CloseButton(onClose)
        }

        Column(
            Modifier
                .fillMaxWidth()
                .weight(1f)
                .verticalScroll(rememberScrollState())
                .padding(vertical = PgSpace.Card),
            verticalArrangement = Arrangement.spacedBy(PgSpace.Xs),
        ) {
            val label = stringResource(R.string.prot_dnsCustom_label)
            Text(label, color = PgColor.Text.Primary, style = PgType.Small.copy(fontWeight = FontWeight.SemiBold))
            PgInput(
                text,
                {
                    text = it
                    tried = false
                },
                label,
                "dns.custom.text",
                placeholder = stringResource(R.string.android_dnsCustom_placeholder),
                style = AddressStyle,
            )
            if (tried && !valid) {
                Text(stringResource(R.string.prot_dnsCustom_bad), Modifier.testTag("dns.custom.bad"), color = PgColor.Status.Danger.Text, style = PgType.Caption)
            } else {
                Text(stringResource(R.string.android_dnsCustom_note), color = PgColor.Text.Muted, style = PgType.Caption)
            }
        }

        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(PgSpace.S)) {
            PgSecondaryButton(stringResource(R.string.dialog_cancel), "dns.custom.cancel", onClose, Modifier.weight(1f), height = 50.dp)
            PgPrimaryButton(
                stringResource(R.string.card_rename_save),
                "dns.custom.save",
                {
                    tried = true
                    if (valid) {
                        onSave(text.trim())
                    }
                },
                Modifier.weight(1f),
                height = 50.dp,
            )
        }
    }
}

/** Крестик листа (.btn-icon 40): серый значок, при нажатии — синий и сжатие 0,98 без заливки (решение 485). */
@Composable
private fun CloseButton(onClose: () -> Unit) {
    val label = stringResource(R.string.card_close)
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    Box(
        Modifier
            .size(40.dp)
            .scale(if (pressed) 0.98f else 1f)
            .clip(RoundedCornerShape(PgRadius.Button))
            .clickable(interactionSource = interaction, indication = null, onClickLabel = label, role = Role.Button, onClick = onClose)
            .semantics { contentDescription = label }
            .testTag("dns.custom.close"),
        contentAlignment = Alignment.Center,
    ) {
        PgIcon(PgIcons.Close, null, color = if (pressed) PgColor.Accent.Base else PgColor.Text.Secondary, size = 20.dp, strokeWidth = 2.2f)
    }
}

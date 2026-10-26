package com.privategateway.client.ui.settings

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import com.privategateway.client.R
import com.privategateway.client.core.settings.DnsChoice
import com.privategateway.client.core.settings.TunnelSettings
import com.privategateway.client.ui.theme.Overline
import com.privategateway.client.ui.theme.PgCard
import com.privategateway.client.ui.theme.PgChoiceRow
import com.privategateway.client.ui.theme.PgColor
import com.privategateway.client.ui.theme.PgLayout
import com.privategateway.client.ui.theme.PgPageHeader
import com.privategateway.client.ui.theme.PgRadius
import com.privategateway.client.ui.theme.PgSpace
import com.privategateway.client.ui.theme.PgToggleRow
import com.privategateway.client.ui.theme.PgType

/**
 * «Защита и утечки» для телефона (доска 5 в одну колонку): DNS в туннеле, IPv6, локальная сеть. Блок при обрыве на
 * Android — системный («Постоянная VPN» в настройках Android), FakeDNS и блок DNS провайдера телефону не нужны: весь
 * трафик и так идет в туннель. Под защитой изменения применяет служба — переподключением без окна утечки.
 */
@Composable
fun ProtectionScreen(tunnel: TunnelSettings, onChange: (TunnelSettings) -> Unit, onBack: () -> Unit) {
    var editingDns by rememberSaveable { mutableStateOf(false) }
    Column(
        Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .statusBarsPadding()
            .padding(horizontal = PgSpace.WindowSideMobile)
            .padding(bottom = PgLayout.ScrollBottomPadding)
            .testTag("protection"),
        verticalArrangement = Arrangement.spacedBy(PgSpace.L),
    ) {
        PgPageHeader(stringResource(R.string.settings_adv_protection_title), onBack)

        PgCard(Modifier.fillMaxWidth()) {
            Overline(stringResource(R.string.prot_group_dns))
            Spacer(Modifier.size(PgSpace.S))
            Text(stringResource(R.string.prot_dns_title), style = PgType.Title)
            Text(stringResource(R.string.prot_dns_text), color = PgColor.Text.Secondary, style = PgType.Small)
            Spacer(Modifier.size(PgSpace.S))
            val choices = listOf(
                DnsChoice.Profile to R.string.prot_dns_profile,
                DnsChoice.CloudflareDoh to R.string.prot_dns_cloudflare,
                DnsChoice.Quad9Doh to R.string.prot_dns_quad9,
            )
            for ((choice, label) in choices) {
                PgChoiceRow(stringResource(label), null, tunnel.dns == choice, "dns.${choice.name}") { onChange(tunnel.copy(dns = choice)) }
            }

            // Свой адрес: выбор открывает ввод; без верного адреса выбор не сохраняется.
            val custom = tunnel.customDns?.takeIf { TunnelSettings.validCustomDns(it) }
            PgChoiceRow(stringResource(R.string.prot_dns_custom), custom, tunnel.dns == DnsChoice.Custom, "dns.Custom") { editingDns = true }
            if (editingDns) {
                CustomDnsEditor(
                    initial = tunnel.customDns.orEmpty(),
                    onSave = { address ->
                        editingDns = false
                        onChange(tunnel.copy(dns = DnsChoice.Custom, customDns = address))
                    },
                    onCancel = { editingDns = false },
                )
            }
        }

        PgCard(Modifier.fillMaxWidth()) {
            Overline(stringResource(R.string.prot_group_ipv6))
            Spacer(Modifier.size(PgSpace.S))
            Text(stringResource(R.string.prot_addr_title), style = PgType.Title)
            Text(stringResource(R.string.prot_addr_text), color = PgColor.Text.Secondary, style = PgType.Small)
            Spacer(Modifier.size(PgSpace.S))
            // Без IPv6 через сервер Android сам закрывает IPv6 всем программам (решение 397) — мимо туннеля он не уйдет.
            PgChoiceRow(stringResource(R.string.prot_addr_only4), null, !tunnel.ipv6, "ipv6.off") { onChange(tunnel.copy(ipv6 = false)) }
            PgChoiceRow(stringResource(R.string.prot_addr_ipv4), null, tunnel.ipv6, "ipv6.on") { onChange(tunnel.copy(ipv6 = true)) }
        }

        PgCard(Modifier.fillMaxWidth()) {
            Overline(stringResource(R.string.prot_group_lan))
            Spacer(Modifier.size(PgSpace.S))
            PgToggleRow(stringResource(R.string.prot_lan_title), stringResource(R.string.prot_lan_text), tunnel.allowLan, "lan") {
                onChange(tunnel.copy(allowLan = it))
            }
        }
    }

}

/** Свой адрес DNS: IP или DoH по IP (https://IP/…); неверный не сохраняется — ядру ушел бы DNS по умолчанию молча. */
@Composable
private fun CustomDnsEditor(initial: String, onSave: (String) -> Unit, onCancel: () -> Unit) {
    var text by remember { mutableStateOf(initial) }
    var tried by remember { mutableStateOf(false) }
    val valid = TunnelSettings.validCustomDns(text)
    Column(Modifier.fillMaxWidth().padding(top = PgSpace.S), verticalArrangement = Arrangement.spacedBy(PgSpace.S)) {
        Text(stringResource(R.string.prot_dnsCustom_label), color = PgColor.Text.Secondary, style = PgType.Caption)
        val shape = RoundedCornerShape(PgRadius.Input)
        BasicTextField(
            value = text,
            onValueChange = { text = it },
            singleLine = true,
            textStyle = PgType.Mono.copy(color = PgColor.Text.Primary),
            cursorBrush = SolidColor(PgColor.Accent.Base),
            modifier = Modifier
                .fillMaxWidth()
                .clip(shape)
                .background(PgColor.Surface.Inset)
                .border(1.dp, if (tried && !valid) PgColor.Status.Danger.Base else PgColor.Line.Border, shape)
                .padding(PgSpace.M)
                .testTag("dns.custom.text"),
            decorationBox = { field ->
                Box {
                    if (text.isEmpty()) {
                        Text(stringResource(R.string.android_dnsCustom_placeholder), color = PgColor.Text.Muted, style = PgType.Mono)
                    }

                    field()
                }
            },
        )
        if (tried && !valid) {
            Text(stringResource(R.string.prot_dnsCustom_bad), color = PgColor.Status.Danger.Text, style = PgType.Caption)
        } else {
            Text(stringResource(R.string.android_dnsCustom_note), color = PgColor.Text.Muted, style = PgType.Caption)
        }

        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) {
            TextButton(onClick = onCancel) {
                Text(stringResource(R.string.dialog_cancel))
            }

            TextButton(
                onClick = {
                    tried = true
                    if (valid) {
                        onSave(text.trim())
                    }
                },
                modifier = Modifier.testTag("dns.custom.save"),
            ) {
                Text(stringResource(R.string.card_rename_save))
            }
        }
    }
}

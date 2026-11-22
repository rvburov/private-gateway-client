package com.privategateway.client.ui.check

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import com.privategateway.client.R
import com.privategateway.client.core.inspect.PhoneNetwork
import com.privategateway.client.core.inspect.ProbeKind
import com.privategateway.client.core.settings.RuleAction
import com.privategateway.client.core.settings.TunnelSettings
import com.privategateway.client.core.vpn.TunnelState
import com.privategateway.client.inspect.CheckUi
import com.privategateway.client.inspect.InspectorUi
import com.privategateway.client.ui.theme.PgCard
import com.privategateway.client.ui.theme.PgColor
import com.privategateway.client.ui.theme.PgInput
import com.privategateway.client.ui.theme.PgLayout
import com.privategateway.client.ui.theme.PgPageHeader
import com.privategateway.client.ui.theme.PgPrimaryButton
import com.privategateway.client.ui.theme.PgSpace
import com.privategateway.client.ui.theme.PgType
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter

/** Лаборатория использует результаты инспектора и реальные события туннеля, без выдуманных промежуточных узлов. */
@Composable
fun LabScreen(page: CheckPage, inspector: InspectorUi, check: CheckUi, tunnel: TunnelSettings, actions: InspectorActions, onBack: () -> Unit) {
    val title = labTitle(page)
    Column(Modifier.fillMaxSize().statusBarsPadding().testTag("lab.${page.name}")) {
        Column(Modifier.padding(horizontal = PgSpace.WindowSideMobile)) { PgPageHeader(stringResource(title), onBack) }
        Column(
            Modifier.fillMaxWidth().weight(1f).verticalScroll(rememberScrollState())
                .padding(start = PgSpace.WindowSideMobile, end = PgSpace.WindowSideMobile, bottom = PgLayout.ScrollBottomPadding),
            verticalArrangement = Arrangement.spacedBy(PgSpace.L),
        ) {
            Note(stringResource(R.string.android_lab_note))
            if (page == CheckPage.LabEvents) {
                if (check.journal.isEmpty()) Note(stringResource(R.string.events_empty))
                for (entry in check.journal.takeLast(50).asReversed()) {
                    PgCard(Modifier.fillMaxWidth()) {
                        Text(clock(entry.at), color = PgColor.Text.Muted, style = PgType.Caption)
                        Text(entry.message, color = PgColor.Text.Primary, style = PgType.Small)
                    }
                }
            } else if (page == CheckPage.LabCompare) {
                Note(stringResource(R.string.android_lab_capture_note))
                PgPrimaryButton(stringResource(R.string.android_lab_capture), "lab.capture", actions.captureLab, Modifier.fillMaxWidth())
                inspector.lab.snapshots.forEachIndexed { index, snapshot ->
                    PgCard(Modifier.fillMaxWidth()) {
                        Text(stringResource(R.string.android_lab_snapshot, (index + 1).toString(), clock(snapshot.at)), style = PgType.BodyStrong, color = PgColor.Text.Primary)
                        Text(stringResource(if (snapshot.localOnly) R.string.block_local_title else if (snapshot.state == TunnelState.Connected) R.string.status_connected_title else R.string.status_off_title), style = PgType.Small, color = PgColor.Text.Secondary)
                        Text(snapshot.address ?: stringResource(R.string.lab_notMeasured), style = PgType.Mono, color = PgColor.Text.Secondary)
                        NetworkFacts(snapshot.networks)
                    }
                }
            } else {
                if (page == CheckPage.LabLayers) Note(stringResource(R.string.android_lab_layers_note))
                PgCard(Modifier.fillMaxWidth()) {
                    Text(stringResource(R.string.android_lab_network), style = PgType.BodyStrong, color = PgColor.Text.Primary)
                    NetworkFacts(inspector.networks)
                }
                PgInput(inspector.lab.target, actions.labTarget, stringResource(R.string.path_site), "lab.target", Modifier.fillMaxWidth())
                if (inspector.lab.invalid) Note(stringResource(R.string.probe_error_target))
                PgPrimaryButton(
                    stringResource(if (inspector.lab.running) R.string.path_checking else R.string.path_check),
                    "lab.run", actions.runLab, Modifier.fillMaxWidth(), enabled = !inspector.lab.running,
                )
                for (result in inspector.lab.results) {
                    val summary = result.summary()
                    PgCard(Modifier.fillMaxWidth().testTag("lab.stage.${result.kind.name}")) {
                        Text(stringResource(probeKindLabel(result.kind)), style = PgType.BodyStrong, color = PgColor.Text.Primary)
                        Text(stringResource(ProbeTexts.summary(summary.key), *summary.args.toTypedArray()), style = PgType.Small, color = PgColor.Text.Secondary)
                        result.steps.mapNotNull { it.address }.distinct().forEach {
                            Text(it, style = PgType.Mono, color = PgColor.Text.Muted)
                        }
                        if (result.kind == ProbeKind.Tls) {
                            result.steps.firstOrNull()?.tls?.let {
                                Text(listOfNotNull(it.protocol, it.alpn, it.cipher).joinToString(" · "), style = PgType.Caption, color = PgColor.Text.Muted)
                            }
                        }
                    }
                }
                if (page == CheckPage.LabLayers) OsiLayers(inspector)
                if (page == CheckPage.LabTrouble) {
                    PgCard(Modifier.fillMaxWidth()) {
                        Text(stringResource(R.string.android_lab_rules), style = PgType.BodyStrong, color = PgColor.Text.Primary)
                        Note(stringResource(R.string.android_lab_rules_note))
                        for (rule in tunnel.routing.coreRules) {
                            val action = when (rule.action) {
                                RuleAction.Block -> R.string.routing_action_block
                                RuleAction.Direct -> R.string.routing_action_direct
                                RuleAction.Proxy -> R.string.routing_action_proxy
                            }
                            Text("${rule.pattern} · ${stringResource(action)}", style = PgType.Small, color = PgColor.Text.Secondary)
                        }
                        Text(stringResource(if (tunnel.routing.blockOthers) R.string.routing_others_block else R.string.routing_others_open), style = PgType.Small, color = PgColor.Text.Secondary)
                    }
                }
            }
            if (inspector.lab.changed) Note(stringResource(R.string.android_lab_sessionChanged))
            if (inspector.lab.failed) Note(stringResource(R.string.probe_summary_failed))
        }
    }
}

@Composable
private fun OsiLayers(inspector: InspectorUi) {
    val unknown = stringResource(R.string.lab_notMeasured)
    val names = listOf(R.string.layer_7_name, R.string.layer_6_name, R.string.layer_5_name, R.string.layer_4_name, R.string.layer_3_name, R.string.layer_2_name, R.string.layer_1_name)
    val texts = listOf(R.string.layer_7_text, R.string.layer_6_text, R.string.layer_5_text, R.string.layer_4_text, R.string.layer_3_text, R.string.layer_2_text, R.string.layer_1_text)
    val network = inspector.networks.firstOrNull { it.isDefault }
    val dns = inspector.lab.results.firstOrNull { it.kind == ProbeKind.Dns }
    val tcp = inspector.lab.results.firstOrNull { it.kind == ProbeKind.Tcp }
    val tls = inspector.lab.results.firstOrNull { it.kind == ProbeKind.Tls }?.steps?.firstOrNull()?.tls
    val tcpSummary = tcp?.summary()
    val values = listOf(
        dns?.steps?.mapNotNull { it.address }?.joinToString(", ")?.ifEmpty { unknown } ?: unknown,
        tls?.let { listOfNotNull(it.protocol, it.alpn).joinToString(" · ") } ?: unknown,
        unknown,
        tcpSummary?.let { stringResource(ProbeTexts.summary(it.key), *it.args.toTypedArray()) } ?: unknown,
        network?.addresses?.joinToString(" · ")?.ifEmpty { unknown } ?: unknown,
        network?.mtu?.let { stringResource(R.string.network_mtu, it.toString()) } ?: unknown,
        inspector.networks.firstOrNull { it.kind != com.privategateway.client.core.inspect.PhoneNetworkKind.Vpn }?.let {
            stringResource(when (it.kind) {
                com.privategateway.client.core.inspect.PhoneNetworkKind.Wifi -> R.string.network_kind_wifi
                com.privategateway.client.core.inspect.PhoneNetworkKind.Cellular -> R.string.network_kind_cellular
                com.privategateway.client.core.inspect.PhoneNetworkKind.Ethernet -> R.string.network_kind_ethernet
                else -> R.string.network_kind_other
            })
        } ?: unknown,
    )
    Text(stringResource(R.string.layers_osi), style = PgType.BodyStrong, color = PgColor.Text.Primary)
    names.forEachIndexed { index, name ->
        PgCard(Modifier.fillMaxWidth()) {
            Text("${7 - index} · ${stringResource(name)}", style = PgType.BodyStrong, color = PgColor.Text.Primary)
            Note(stringResource(texts[index]))
            Text(values[index], style = PgType.Small, color = PgColor.Text.Secondary)
        }
    }
}

@Composable
private fun NetworkFacts(networks: List<PhoneNetwork>) {
    if (networks.isEmpty()) Note(stringResource(R.string.network_none))
    for (network in networks) {
        Text(network.name ?: stringResource(R.string.lab_notMeasured), style = PgType.BodyStrong, color = PgColor.Text.Primary)
        Text(network.addresses.joinToString(" · "), style = PgType.Small, color = PgColor.Text.Secondary)
        Text("${stringResource(R.string.network_gateway)}: ${network.gateways.joinToString(", ").ifEmpty { stringResource(R.string.lab_notMeasured) }}", style = PgType.Small, color = PgColor.Text.Secondary)
        Text("${stringResource(R.string.network_dns)}: ${network.dns.joinToString(", ").ifEmpty { stringResource(R.string.lab_notMeasured) }}", style = PgType.Small, color = PgColor.Text.Secondary)
        network.mtu?.let { Text(stringResource(R.string.network_mtu, it.toString()), style = PgType.Caption, color = PgColor.Text.Muted) }
    }
}

@Composable
private fun Note(text: String) = Text(text, style = PgType.Caption, color = PgColor.Text.Muted)

internal fun labTitle(page: CheckPage): Int = when (page) {
    CheckPage.LabPath -> R.string.diag_path_title
    CheckPage.LabLayers -> R.string.diag_layers_title
    CheckPage.LabEvents -> R.string.diag_events_title
    CheckPage.LabTrouble -> R.string.diag_trouble_title
    else -> R.string.diag_vpnpath_title
}

private fun clock(at: Long): String = DateTimeFormatter.ofPattern("HH:mm:ss").format(Instant.ofEpochMilli(at).atZone(ZoneId.systemDefault()))

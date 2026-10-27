package com.privategateway.client.ui.check

import androidx.annotation.StringRes
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import com.privategateway.client.R
import com.privategateway.client.core.inspect.AddressFailure
import com.privategateway.client.core.inspect.AddressInfo
import com.privategateway.client.core.inspect.PhoneNetwork
import com.privategateway.client.core.inspect.PhoneNetworkKind
import com.privategateway.client.core.inspect.ProbeKind
import com.privategateway.client.core.inspect.ProbeOutcome
import com.privategateway.client.core.inspect.ProbeResult
import com.privategateway.client.core.inspect.TlsFacts
import com.privategateway.client.core.inspect.TlsProblem
import com.privategateway.client.inspect.InspectorUi
import com.privategateway.client.inspect.ProbeUi
import com.privategateway.client.inspect.PublicIpUi
import com.privategateway.client.ui.theme.Overline
import com.privategateway.client.ui.theme.PgButton
import com.privategateway.client.ui.theme.PgCard
import com.privategateway.client.ui.theme.PgChoiceRow
import com.privategateway.client.ui.theme.PgColor
import com.privategateway.client.ui.theme.PgRadius
import com.privategateway.client.ui.theme.PgSpace
import com.privategateway.client.ui.theme.PgType
import java.time.Duration
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.time.format.FormatStyle
import java.util.Locale

/** Что делают кнопки и поля инспектора. */
data class InspectorActions(
    val measureIp: () -> Unit = {},
    val describe: () -> Unit = {},
    val kind: (ProbeKind) -> Unit = {},
    val target: (String) -> Unit = {},
    val port: (String) -> Unit = {},
    val run: () -> Unit = {},
)

/**
 * Инспектор сети на «Проверке» (пункт 9.5, решение 448): сети телефона, внешний IP и его провайдер, замеры DNS, TCP и
 * TLS — подписи и итоги те же, что у десктопа. [protected] — идет ли сейчас защита: замер и внешний IP идут через нее.
 */
@Composable
fun InspectorSection(ui: InspectorUi, protected: Boolean, actions: InspectorActions) {
    NetworksCard(ui.networks)
    PublicIpCard(ui.publicIp, actions)
    ProbeCard(ui.probe, protected, actions)
}

@Composable
private fun NetworksCard(networks: List<PhoneNetwork>) {
    PgCard(Modifier.fillMaxWidth().testTag("inspect.networks")) {
        Overline(stringResource(R.string.android_inspect_title))
        Spacer(Modifier.size(PgSpace.Xs))
        Text(stringResource(R.string.android_inspect_note), color = PgColor.Text.Secondary, style = PgType.Small)
        if (networks.isEmpty()) {
            Spacer(Modifier.size(PgSpace.S))
            Text(stringResource(R.string.android_inspect_none), color = PgColor.Text.Muted, style = PgType.Body)
        }

        for (network in networks) {
            Spacer(Modifier.size(PgSpace.M))
            Network(network)
        }
    }
}

@Composable
private fun Network(network: PhoneNetwork) {
    val kind = stringResource(
        when (network.kind) {
            PhoneNetworkKind.Wifi -> R.string.network_kind_wifi
            PhoneNetworkKind.Cellular -> R.string.network_kind_cellular
            PhoneNetworkKind.Ethernet -> R.string.network_kind_ethernet
            PhoneNetworkKind.Vpn -> R.string.network_kind_tunnel
            PhoneNetworkKind.Other -> R.string.network_kind_other
        },
    )
    val marks = listOfNotNull(
        stringResource(R.string.android_inspect_default).takeIf { network.isDefault },
        stringResource(R.string.network_metered).takeIf { network.metered },
    )
    Column(Modifier.testTag("inspect.network.${network.name ?: network.kind.name}")) {
        Text(listOfNotNull(kind, network.name).joinToString(" · "), style = PgType.BodyStrong)
        if (marks.isNotEmpty()) {
            Text(marks.joinToString(" · "), color = PgColor.Accent.Text, style = PgType.Caption)
        }

        Fact(R.string.network_ipv4, network.addresses.filter { !it.contains(':') })
        Fact(R.string.network_ipv6, network.addresses.filter { it.contains(':') })
        Fact(R.string.network_gateway, network.gateways)
        Fact(R.string.network_dns, network.dns)
        network.privateDns?.let { name ->
            Fact(R.string.android_inspect_privateDns, listOf(name.ifEmpty { stringResource(R.string.android_inspect_privateDns_auto) }))
        }

        network.mtu?.let { Text(stringResource(R.string.network_mtu, it.toString()), color = PgColor.Text.Secondary, style = PgType.Small) }
        if (network.validated) {
            Text(stringResource(R.string.android_inspect_validated), color = PgColor.Status.Success.Text, style = PgType.Small)
        }

        if (network.captive) {
            Text(stringResource(R.string.android_inspect_captive), color = PgColor.Status.Warning.Text, style = PgType.Small)
        }
    }
}

/** Строка «подпись — значения»; значений нет — строки нет. */
@Composable
private fun Fact(@StringRes title: Int, values: List<String>) {
    if (values.isEmpty()) {
        return
    }

    Row(Modifier.fillMaxWidth().padding(top = PgSpace.Xxs)) {
        Text(stringResource(title), color = PgColor.Text.Secondary, style = PgType.Small, modifier = Modifier.width(96.dp))
        Text(values.joinToString("\n"), style = PgType.Mono, modifier = Modifier.weight(1f))
    }
}

@Composable
private fun PublicIpCard(ip: PublicIpUi, actions: InspectorActions) {
    PgCard(Modifier.fillMaxWidth().testTag("inspect.ip")) {
        Overline(stringResource(R.string.network_public))
        Spacer(Modifier.size(PgSpace.S))
        when {
            ip.address != null -> {
                Text(ip.address, style = PgType.Mono, modifier = Modifier.testTag("inspect.ip.address"))
                Text(stringResource(if (ip.viaServer) R.string.network_public_exit else R.string.network_public_direct), color = PgColor.Text.Secondary, style = PgType.Small)
            }

            ip.failed -> Text(stringResource(R.string.network_public_unreachable), color = PgColor.Status.Warning.Text, style = PgType.Small)
            !ip.measuring -> Text(stringResource(R.string.network_public_none), color = PgColor.Text.Muted, style = PgType.Small)
        }

        Spacer(Modifier.size(PgSpace.M))
        PgButton(stringResource(if (ip.measuring) R.string.network_public_measuring else R.string.network_public_measure), !ip.measuring, "inspect.ip.measure", actions.measureIp)
        if (ip.address != null) {
            Spacer(Modifier.size(PgSpace.S))
            PgButton(stringResource(if (ip.describing) R.string.network_public_asking else R.string.network_public_describe), !ip.describing, "inspect.ip.describe", actions.describe)
        }

        ip.info?.let { Provider(it) }
    }
}

@Composable
private fun Provider(info: AddressInfo) {
    Spacer(Modifier.size(PgSpace.S))
    when (info.failure) {
        AddressFailure.Private -> Text(stringResource(R.string.network_public_private), color = PgColor.Text.Secondary, style = PgType.Small)
        AddressFailure.Unreachable -> Text(stringResource(R.string.network_public_unreachable), color = PgColor.Status.Warning.Text, style = PgType.Small)
        AddressFailure.NotFound -> Text(stringResource(R.string.network_public_notfound), color = PgColor.Text.Secondary, style = PgType.Small)
        null -> {
            val provider = listOfNotNull(info.asn?.let { "AS$it" }, info.holder).joinToString(" · ")
            Fact(R.string.network_public_provider, listOfNotNull(provider.ifEmpty { null }))
            Fact(R.string.network_public_prefix, listOfNotNull(info.prefix))
            val country = info.country?.let { Locale.Builder().setRegion(it).build().displayCountry.ifEmpty { it } }
            val place = listOfNotNull(country, info.city).joinToString(", ")
            if (place.isNotEmpty()) {
                Fact(R.string.network_public_place, listOf(stringResource(R.string.network_public_approx, place)))
            }

            Spacer(Modifier.size(PgSpace.S))
            Text(stringResource(R.string.network_public_source), color = PgColor.Text.Muted, style = PgType.Caption)
        }
    }
}

@Composable
private fun ProbeCard(probe: ProbeUi, protected: Boolean, actions: InspectorActions) {
    PgCard(Modifier.fillMaxWidth().testTag("inspect.probe")) {
        Overline(stringResource(R.string.probe_kind))
        Spacer(Modifier.size(PgSpace.Xs))
        val kinds = listOf(ProbeKind.Dns to R.string.probe_kind_dns, ProbeKind.Tcp to R.string.probe_kind_tcp, ProbeKind.Tls to R.string.probe_kind_tls)
        for ((kind, label) in kinds) {
            PgChoiceRow(stringResource(label), null, probe.kind == kind, "inspect.probe.${kind.name}") { actions.kind(kind) }
        }

        Text(
            stringResource(
                when (probe.kind) {
                    ProbeKind.Dns -> R.string.probe_method_dns
                    ProbeKind.Tcp -> R.string.probe_method_tcp
                    ProbeKind.Tls -> R.string.probe_method_tls
                },
            ),
            color = PgColor.Text.Secondary,
            style = PgType.Small,
        )
        Spacer(Modifier.size(PgSpace.S))
        Field(stringResource(R.string.probe_target), probe.target, stringResource(R.string.probe_target_placeholder), KeyboardType.Uri, "inspect.probe.target", actions.target)
        if (probe.kind != ProbeKind.Dns) {
            Spacer(Modifier.size(PgSpace.S))
            Field(stringResource(R.string.probe_portLabel), probe.port, "443", KeyboardType.Number, "inspect.probe.port", actions.port)
        }

        Spacer(Modifier.size(PgSpace.S))
        Text(stringResource(if (protected) R.string.probe_path_vpn else R.string.android_probe_path_direct), color = PgColor.Text.Muted, style = PgType.Caption)
        Spacer(Modifier.size(PgSpace.M))
        PgButton(stringResource(R.string.probe_run), !probe.running, "inspect.probe.run", actions.run)
        probe.error?.let {
            Text(stringResource(it), color = PgColor.Status.Warning.Text, style = PgType.Small, modifier = Modifier.padding(top = PgSpace.S).testTag("inspect.probe.error"))
        }

        probe.result?.let { Result(it) }
    }
}

@Composable
private fun Field(label: String, value: String, placeholder: String, keyboard: KeyboardType, tag: String, onChange: (String) -> Unit) {
    Text(label, color = PgColor.Text.Secondary, style = PgType.Caption)
    Spacer(Modifier.size(PgSpace.Xxs))
    val shape = RoundedCornerShape(PgRadius.Input)
    BasicTextField(
        value = value,
        onValueChange = onChange,
        singleLine = true,
        textStyle = PgType.Mono.copy(color = PgColor.Text.Primary),
        cursorBrush = SolidColor(PgColor.Accent.Base),
        keyboardOptions = KeyboardOptions(keyboardType = keyboard),
        modifier = Modifier
            .fillMaxWidth()
            .clip(shape)
            .background(PgColor.Surface.Inset)
            .border(1.dp, PgColor.Line.Border, shape)
            .padding(PgSpace.M)
            .testTag(tag),
        decorationBox = { inner ->
            if (value.isEmpty()) {
                Text(placeholder, color = PgColor.Text.Muted, style = PgType.Mono)
            }

            inner()
        },
    )
}

@Composable
private fun Result(result: ProbeResult) {
    val summary = result.summary()
    Spacer(Modifier.size(PgSpace.M))
    Text(stringResource(ProbeTexts.summary(summary.key), *summary.args.toTypedArray()), style = PgType.BodyStrong, modifier = Modifier.testTag("inspect.probe.summary"))
    for (step in result.steps) {
        val time = step.timeMs?.let { stringResource(R.string.probe_ms, it.toString()) }
        val line = listOfNotNull("${step.index}.", step.address, stringResource(ProbeTexts.outcome(step.outcome)), time).joinToString(" ")
        Text(line, color = PgColor.Text.Secondary, style = PgType.Mono)
        step.tls?.let { Tls(it) }
    }
}

@Composable
private fun Tls(tls: TlsFacts) {
    Fact(R.string.probe_tls_protocol, listOf(tls.protocol))
    Fact(R.string.probe_tls_cipher, listOf(tls.cipher))
    Fact(R.string.probe_tls_alpn, listOfNotNull(tls.alpn))
    Fact(R.string.probe_tls_subject, listOf(tls.subject))
    Fact(R.string.probe_tls_issuer, listOf(tls.issuer))
    val date = DateTimeFormatter.ofLocalizedDate(FormatStyle.MEDIUM).withZone(ZoneId.systemDefault())
    val now = Instant.now()
    val valid = if (now.isAfter(tls.notAfter)) {
        stringResource(R.string.probe_tls_expired, date.format(tls.notAfter))
    } else {
        stringResource(R.string.probe_tls_until, date.format(tls.notAfter), Duration.between(now, tls.notAfter).toDays().toString())
    }

    Fact(R.string.probe_tls_valid, listOf(valid))
    Fact(R.string.probe_tls_names, tls.names)
    val check = if (tls.problems.isEmpty()) listOf(stringResource(R.string.probe_tls_ok)) else tls.problems.map { stringResource(ProbeTexts.problem(it)) }
    Fact(R.string.probe_tls_check, check)
}

/** Тексты замеров — ключи десктопа (probe.*). */
object ProbeTexts {
    @StringRes
    fun summary(key: String): Int = when (key) {
        "probe.summary.notFound" -> R.string.probe_summary_notFound
        "probe.summary.dns" -> R.string.probe_summary_dns
        "probe.summary.tcp" -> R.string.probe_summary_tcp
        "probe.summary.refused" -> R.string.probe_summary_refused
        "probe.summary.timedOut" -> R.string.probe_summary_timedOut
        "probe.summary.unreachable" -> R.string.probe_summary_unreachable
        "probe.summary.tls" -> R.string.probe_summary_tls
        "probe.summary.tls.problems" -> R.string.probe_summary_tls_problems
        else -> R.string.probe_summary_failed
    }

    @StringRes
    fun outcome(outcome: ProbeOutcome): Int = when (outcome) {
        ProbeOutcome.Ok -> R.string.probe_outcome_ok
        ProbeOutcome.TimedOut -> R.string.probe_outcome_timedout
        ProbeOutcome.Refused -> R.string.probe_outcome_refused
        ProbeOutcome.Unreachable -> R.string.probe_outcome_unreachable
        ProbeOutcome.NotFound -> R.string.probe_outcome_notfound
        ProbeOutcome.Failed -> R.string.probe_outcome_failed
    }

    @StringRes
    fun problem(problem: TlsProblem): Int = when (problem) {
        TlsProblem.NameMismatch -> R.string.probe_tls_problem_namemismatch
        TlsProblem.Expired -> R.string.probe_tls_problem_expired
        TlsProblem.NotYetValid -> R.string.probe_tls_problem_notyetvalid
        TlsProblem.Untrusted -> R.string.probe_tls_problem_untrusted
        TlsProblem.Other -> R.string.probe_tls_problem_other
    }
}

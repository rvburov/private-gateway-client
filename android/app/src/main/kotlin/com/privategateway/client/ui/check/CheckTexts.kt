package com.privategateway.client.ui.check

import android.content.Context
import androidx.annotation.StringRes
import com.privategateway.client.R
import com.privategateway.client.core.inspect.PhoneNetwork
import com.privategateway.client.core.inspect.PhoneNetworkKind
import com.privategateway.client.core.servers.TransportSecurity
import com.privategateway.client.core.text.Plural
import com.privategateway.client.inspect.CheckUi
import com.privategateway.client.ui.MainUi
import com.privategateway.client.ui.ProtectionView
import com.privategateway.client.vpn.DelayBreakdown
import com.privategateway.client.vpn.JournalEntry
import com.privategateway.client.vpn.JournalLevel
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.Locale
import kotlin.math.roundToInt

/** Строка «Задержки»: что измерено, пояснение и число, мс; не измерено — null («—»). */
data class DelayRow(@param:StringRes val title: Int, @param:StringRes val text: Int, val value: Int?)

/**
 * «Задержка» (DelayRows, DelayWhy и DelayHint десктопа): три строки, «Откуда … мс» и почему чисел нет. Числа — только под
 * защитой и не во время замера: тогда «—» и «Меряем…», как на доске; без защиты — «Подключитесь…».
 */
object DelayRows {
    fun of(ui: MainUi, check: CheckUi): List<DelayRow> {
        val delay = shown(ui, check)
        return listOf(
            DelayRow(R.string.diag_delay_toServer, R.string.diag_delay_toServer_text, delay?.toServerMs),
            DelayRow(R.string.diag_delay_through, R.string.diag_delay_through_text, delay?.throughServerMs),
            DelayRow(R.string.diag_delay_fresh, R.string.diag_delay_fresh_text, delay?.newConnectionMs),
        )
    }

    /** Замер, который сейчас показывается; нет — null. */
    fun shown(ui: MainUi, check: CheckUi): DelayBreakdown? = check.delay?.takeIf { working(ui) && !check.measuringDelay }

    /** Туннель работает: задержка меряется через сервер. */
    fun working(ui: MainUi): Boolean = ui.view == ProtectionView.Connected || ui.view == ProtectionView.Leak

    /**
     * Откуда число нового соединения: сколько кругов до сервера (по замерам, а не по учебнику), чье рукопожатие (REALITY, TLS
     * или протокола) и сколько длится один круг. [language] — язык текстов: форма слова «круг».
     */
    fun why(ui: MainUi, check: CheckUi, language: String): CheckText? {
        val delay = shown(ui, check) ?: return null
        val one = delay.toServerMs ?: return null
        val fresh = delay.newConnectionMs ?: return null
        val rounds = delay.rounds ?: return null
        val count = maxOf(1, rounds.roundToInt())
        val word = when (Plural.form(count, language)) {
            "one" -> R.string.diag_delay_rounds_one
            "few" -> R.string.diag_delay_rounds_few
            else -> R.string.diag_delay_rounds_many
        }
        val handshake: Any = when (ui.inUse?.security) {
            TransportSecurity.Reality -> "REALITY"
            TransportSecurity.Tls -> "TLS"
            else -> CheckText.of(R.string.diag_delay_handshake_protocol)
        }
        return CheckText.of(R.string.diag_delay_why, fresh.toString(), count.toString(), CheckText.of(word), handshake, one.toString())
    }

    /** Почему чисел нет: идет замер, нет подключения; есть что показать — null. */
    @StringRes
    fun hint(ui: MainUi, check: CheckUi): Int? = when {
        check.measuringDelay -> R.string.diag_delay_measuring
        !working(ui) -> R.string.diag_delay_off
        else -> null
    }

    /** «Обновлено 15:42:12» — местное время замера. */
    fun updated(at: Long): String = DateTimeFormatter.ofPattern("HH:mm:ss", Locale.ROOT).format(Instant.ofEpochMilli(at).atZone(ZoneId.systemDefault()))
}

/** Сеть телефона словами — для страницы «Сеть телефона» и отчета. */
object NetworkTexts {
    @StringRes
    fun kind(kind: PhoneNetworkKind): Int = when (kind) {
        PhoneNetworkKind.Wifi -> R.string.network_kind_wifi
        PhoneNetworkKind.Cellular -> R.string.network_kind_cellular
        PhoneNetworkKind.Ethernet -> R.string.network_kind_ethernet
        PhoneNetworkKind.Vpn -> R.string.network_kind_tunnel
        PhoneNetworkKind.Other -> R.string.network_kind_other
    }

    /** «Wi-Fi · wlan0 · по умолчанию». */
    fun title(context: Context, network: PhoneNetwork): String = listOfNotNull(
        context.getString(kind(network.kind)),
        network.name,
        context.getString(R.string.android_inspect_default).takeIf { network.isDefault },
    ).joinToString(" · ")

    /** Адреса, шлюзы, DNS, частный DNS и MTU — «Подпись: значения»; чего нет, того и строки нет. */
    fun facts(context: Context, network: PhoneNetwork): List<String> = listOfNotNull(
        fact(context, R.string.network_ipv4, network.addresses.filter { ':' !in it }),
        fact(context, R.string.network_ipv6, network.addresses.filter { ':' in it }),
        fact(context, R.string.network_gateway, network.gateways),
        fact(context, R.string.network_dns, network.dns),
        network.privateDns?.let { fact(context, R.string.android_inspect_privateDns, listOf(it.ifEmpty { context.getString(R.string.android_inspect_privateDns_auto) })) },
        network.mtu?.let { context.getString(R.string.network_mtu, it.toString()) },
    )

    private fun fact(context: Context, @StringRes title: Int, values: List<String>): String? =
        if (values.isEmpty()) null else context.getString(title) + ": " + values.joinToString(", ")
}

/** Запись журнала строкой — «15:42:10 INF  tunnel up»: местное время, уровень как у десктопа и текст. */
object JournalText {
    fun time(at: Long): String = DateTimeFormatter.ofPattern("HH:mm:ss", Locale.ROOT).format(Instant.ofEpochMilli(at).atZone(ZoneId.systemDefault()))

    fun level(level: JournalLevel): String = when (level) {
        JournalLevel.Debug -> "DBG"
        JournalLevel.Info -> "INF"
        JournalLevel.Warning -> "WRN"
        JournalLevel.Error -> "ERR"
    }

    fun line(entry: JournalEntry): String = time(entry.at) + " " + level(entry.level) + "  " + entry.message
}

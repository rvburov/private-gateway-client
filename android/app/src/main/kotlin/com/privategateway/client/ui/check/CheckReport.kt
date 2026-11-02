package com.privategateway.client.ui.check

import android.content.Context
import android.os.Build
import com.privategateway.client.BuildConfig
import com.privategateway.client.R
import com.privategateway.client.core.inspect.PhoneNetwork
import com.privategateway.client.core.settings.IpAddress
import com.privategateway.client.core.settings.TunnelSettings
import com.privategateway.client.core.text.SecretMasker
import com.privategateway.client.inspect.CheckUi
import com.privategateway.client.ui.MainUi
import com.privategateway.client.ui.StateLook
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.Locale

/**
 * Обезличенный отчет «Проверки» (BuildReport десктопа): версия, Android, когда проверено и состояние, те же строки, что на
 * экране, задержка, что мешает, сети телефона и журнал туннеля. Каждая строка проходит через [ReportAnonymizer] (и
 * SecretMasker): публичные адреса — метками, MAC скрыт; реальный IP заменен звездочками еще до того. Ссылок, ключей, UUID,
 * адресов и названий серверов и адресов подписок в отчете нет — их нет ни в строках экрана, ни в журнале.
 */
object CheckReport {
    private const val HIDDEN = "***"

    /** Имя файла по умолчанию в окне «Сохранить»: private-gateway-report-20261102-1542.txt. */
    fun fileName(now: Long): String =
        "private-gateway-report-" + DateTimeFormatter.ofPattern("yyyyMMdd-HHmm", Locale.ROOT).format(Instant.ofEpochMilli(now).atZone(ZoneId.systemDefault())) + ".txt"

    fun build(
        context: Context,
        ui: MainUi,
        check: CheckUi,
        networks: List<PhoneNetwork>,
        core: String?,
        tunnel: TunnelSettings,
        language: String,
        now: Long = System.currentTimeMillis(),
    ): String {
        val time = DateTimeFormatter.ofPattern("yyyy-MM-dd HH:mm:ss xxx", Locale.ROOT).format(Instant.ofEpochMilli(now).atZone(ZoneId.systemDefault()))
        val lines = mutableListOf(
            context.getString(R.string.diag_report_title, BuildConfig.VERSION_NAME),
            "Android " + Build.VERSION.RELEASE + " · " + (Build.SUPPORTED_ABIS.firstOrNull() ?: "—"),
            context.getString(R.string.diag_report_checkedAt, time),
            context.getString(R.string.diag_report_state, context.getString(StateLook.of(ui.view).title)),
            "",
            context.getString(R.string.diag_report_checks),
        )
        for (row in CheckRows.of(ui, core, check.hints, tunnel, check.running)) {
            val detail = row.detail.joinToString(" · ") { it.resolve(context) }
            lines += "  [" + row.chip.resolve(context) + "] " + context.getString(row.title) + " — " + detail
        }

        lines += ""
        lines += context.getString(R.string.diag_report_delay)
        for (delay in DelayRows.of(ui, check)) {
            lines += "  " + context.getString(delay.title) + ": " + (delay.value?.let { context.getString(R.string.diag_delay_ms, it.toString()) } ?: "—") +
                " — " + context.getString(delay.text)
        }

        DelayRows.why(ui, check, language)?.let { lines += "  " + it.resolve(context) }
        lines += ""
        lines += context.getString(R.string.diag_report_hinder)
        for (row in HinderRows.of(check.hints)) {
            lines += "  [" + row.chip.resolve(context) + "] " + row.title.resolve(context) +
                row.detail.joinToString(" ") { it.resolve(context) }.let { if (it.isEmpty()) "" else " — $it" }
        }

        lines += ""
        lines += context.getString(R.string.android_inspect_title)
        if (networks.isEmpty()) {
            lines += "  " + context.getString(R.string.android_inspect_none)
        }

        for (network in networks) {
            lines += "  " + NetworkTexts.title(context, network)
            NetworkTexts.facts(context, network).forEach { lines += "    $it" }
        }

        lines += ""
        lines += context.getString(R.string.diag_report_journal)
        for (entry in check.journal) {
            lines += "  " + JournalText.line(entry)
        }

        lines += ""
        lines += context.getString(R.string.diag_report_anonymous)

        // Реальный IP пользователя в отчет не попадает, даже если он сейчас виден сайтам.
        val direct = ui.ip.direct
        val anonymizer = ReportAnonymizer(context.getString(R.string.diag_report_addressLabel))
        return lines.joinToString("\n", postfix = "\n") { line ->
            anonymizer.clean(if (direct != null) line.replace(direct, HIDDEN) else line)
        }
    }
}

/**
 * Обезличивание отчета — ReportAnonymizer десктопа: публичные адреса IPv4 и все адреса IPv6 (в них бывает MAC или постоянный
 * номер устройства) — метками «[адрес-1]», «[адрес-2]»…, один адрес везде одной меткой; MAC — «[MAC]». Частные и служебные
 * IPv4 (локальная сеть, туннель, петля), известные открытые резолверы и широкие сети маршрутов (/8 и шире) остаются: без них
 * отчет не объяснит маршрут. Секреты и ссылки потом скрывает SecretMasker. Адреса разбираются без поиска имен (IpAddress).
 */
class ReportAnonymizer(private val label: String) {
    private val tokens = LinkedHashMap<List<Byte>, String>()

    /** Строка с метками вместо личных адресов и MAC и без секретов. */
    fun clean(text: String): String {
        if (text.isEmpty()) {
            return text
        }

        var result = MAC.replace(text, "[MAC]")
        result = IPV6.replace(result) { match -> replace(match, match.groups["address"]?.value?.substringBefore('%'), 16) }
        result = IPV4.replace(result) { match -> replace(match, match.groups["address"]?.value, 4) }
        return SecretMasker.mask(result)
    }

    private fun replace(match: MatchResult, address: String?, size: Int): String {
        val bytes = address?.let { IpAddress.parse(it) }?.takeIf { it.size == size } ?: return match.value
        if (keep(bytes) || wide(match)) {
            return match.value
        }

        return token(bytes) + (match.groups["prefix"]?.value ?: "")
    }

    private fun token(bytes: ByteArray): String = tokens.getOrPut(bytes.toList()) { "[$label-${tokens.size + 1}]" }

    companion object {
        // Открытые резолверы и проверочные адреса: известны всем и ничего не говорят о пользователе.
        private val WELL_KNOWN = listOf(
            "1.1.1.1", "1.0.0.1", "8.8.8.8", "8.8.4.4", "9.9.9.9", "149.112.112.112", "2606:4700:4700::1111", "2606:4700:4700::1001",
            "2001:4860:4860::8888", "2001:4860:4860::8844",
        ).mapNotNull { IpAddress.parse(it)?.toList() }.toSet()

        // MAC: шесть пар через двоеточие или дефис.
        private val MAC = Regex("""\b[0-9A-Fa-f]{2}(?:[:-][0-9A-Fa-f]{2}){5}\b""")

        // IPv4 — четыре числа до 255, и длина префикса сети, если есть.
        private val IPV4 = Regex("""(?<![\d.])(?<address>(?:25[0-5]|2[0-4]\d|1?\d?\d)(?:\.(?:25[0-5]|2[0-4]\d|1?\d?\d)){3})(?<prefix>/\d{1,2}(?!\d))?(?![\d.])""")

        // IPv6 — группы через двоеточие, с «::», зоной и длиной префикса; разбор решает, адрес ли это.
        private val IPV6 = Regex("""(?<![0-9A-Fa-f:])(?<address>(?:[0-9A-Fa-f]{0,4}:){2,7}[0-9A-Fa-f]{0,4}(?:%[0-9A-Za-z]+)?)(?<prefix>/\d{1,3}(?!\d))?(?![0-9A-Fa-f:])""")

        /** Адрес остается в отчете: частный или служебный IPv4, петля и пустой IPv6, известный открытый резолвер. */
        fun keep(bytes: ByteArray): Boolean {
            if (bytes.toList() in WELL_KNOWN) {
                return true
            }

            if (bytes.size == 16) {
                val loopback = bytes.take(15).all { it.toInt() == 0 } && bytes[15].toInt() == 1
                return loopback || bytes.all { it.toInt() == 0 }
            }

            val a = bytes[0].toInt() and 0xFF
            val b = bytes[1].toInt() and 0xFF
            return a == 0 || a == 10 || a == 127 || a >= 224 ||
                a == 100 && b in 64..127 ||
                a == 169 && b == 254 ||
                a == 172 && b in 16..31 ||
                a == 192 && b == 168 ||
                a == 198 && (b == 18 || b == 19)
        }

        // Сеть маршрута шириной /8 и больше — общая для всех, а не адрес пользователя.
        private fun wide(match: MatchResult): Boolean =
            match.groups["prefix"]?.value?.drop(1)?.toIntOrNull()?.let { it <= 8 } == true
    }
}

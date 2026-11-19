package com.privategateway.client.ui

import android.content.Context
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.privategateway.client.BuildConfig
import com.privategateway.client.R
import com.privategateway.client.core.inspect.PhoneNetwork
import com.privategateway.client.core.inspect.PhoneNetworkKind
import com.privategateway.client.core.settings.DnsChoice
import com.privategateway.client.core.settings.TunnelSettings
import com.privategateway.client.core.text.SecretMasker
import com.privategateway.client.core.vpn.TunnelStatus
import com.privategateway.client.inspect.CheckUi
import com.privategateway.client.inspect.PhoneHints
import com.privategateway.client.ui.check.CheckReport
import com.privategateway.client.ui.check.JournalText
import com.privategateway.client.ui.check.ReportAnonymizer
import com.privategateway.client.vpn.JournalEntry
import com.privategateway.client.vpn.JournalLevel
import com.privategateway.client.vpn.TunnelJournal
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue
import org.junit.Test
import org.junit.runner.RunWith

/** Обезличенный отчет «Проверки»: метки вместо публичных адресов, без реального IP и секретов, те же строки, что на экране. */
@RunWith(AndroidJUnit4::class)
class CheckReportTest {
    private val context = ApplicationProvider.getApplicationContext<Context>()

    @Test
    fun `anonymizer labels public addresses and keeps the local ones`() {
        val anonymizer = ReportAnonymizer("адрес")
        val line = "v6 2001:db8::23/64 exit 203.0.113.24 then 203.0.113.24 and 198.51.100.7, lan 192.168.1.10, dns 1.1.1.1, " +
            "route 128.0.0.0/1, at 15:42:12, mac 02:00:00:aa:bb:cc, link vless://abc@host:443"
        assertEquals(
            "v6 [адрес-1]/64 exit [адрес-2] then [адрес-2] and [адрес-3], lan 192.168.1.10, dns 1.1.1.1, " +
                "route 128.0.0.0/1, at 15:42:12, mac [MAC], link vless://" + SecretMasker.HIDDEN,
            anonymizer.clean(line),
        )
        // Один адрес — одна метка во всем отчете.
        assertEquals("[адрес-2]", anonymizer.clean("203.0.113.24"))
        assertTrue(ReportAnonymizer.keep(byteArrayOf(10, 0, 0, 1)))
        assertFalse(ReportAnonymizer.keep(byteArrayOf(192.toByte(), 0, 2, 1)))
    }

    @Test
    fun `report has the screen rows without the real ip and secrets`() {
        val ui = MainUi(status = TunnelStatus(), ip = WebIp(direct = "198.51.100.7"))
        val journal = TunnelJournal()
        journal.info("connect  mode=vpn")
        journal.error("core: failed vless://11111111-2222-3333-4444-555555555555@203.0.113.9:443")
        val check = CheckUi(hints = PhoneHints(proxy = "192.168.1.1:3128"), journal = journal.entries.value)
        val wifi = PhoneNetwork(
            PhoneNetworkKind.Wifi, "wlan0", isDefault = true, addresses = listOf("192.168.1.23/24", "2001:db8::23/64"), gateways = listOf("192.168.1.1"),
            dns = listOf("192.168.1.1"), privateDns = null, mtu = 1500, metered = false, validated = true, captive = false,
        )
        val report = CheckReport.build(context, ui, check, listOf(wifi), "26.10.21", TunnelSettings(), "en")
        val lines = report.lines()

        assertEquals(context.getString(R.string.diag_report_title, BuildConfig.VERSION_NAME), lines.first())
        assertTrue(context.getString(R.string.diag_report_checks) in lines)
        assertTrue(
            "  [" + context.getString(R.string.diag_exit_ip_off_chip) + "] " + context.getString(R.string.diag_exit_ip_title) + " — " +
                context.getString(R.string.diag_exit_ip_off, "***") in lines,
            report,
        )
        assertTrue(
            "  [" + context.getString(R.string.diag_hinder_chip_set) + "] " + context.getString(R.string.diag_hinder_system) + " — " +
                context.getString(R.string.diag_hinder_detail, "192.168.1.1:3128", context.getString(R.string.diag_hinder_unknown)) + " " +
                context.getString(R.string.diag_hinder_how_system) in lines,
            report,
        )
        assertTrue(lines.any { it.endsWith("INF  connect  mode=vpn") }, report)
        assertTrue(lines.any { it.contains("192.168.1.23/24") }, "частные адреса остаются: $report")
        assertEquals(context.getString(R.string.diag_report_anonymous), lines.last { it.isNotEmpty() })
        // Реального IP, UUID, ссылки сервера и публичных адресов в отчете нет.
        assertFalse("198.51.100.7" in report, report)
        assertFalse("11111111-2222-3333-4444-555555555555" in report, report)
        assertFalse("203.0.113.9" in report, report)
        assertFalse("2001:db8::23" in report, report)
    }

    /** Решение 750: раздел DNS без адреса своего DNS и ошибки журнала одной строкой с числом повторов, как у десктопа. */
    @Test
    fun `report has the dns section and groups repeated problems`() {
        val journal = TunnelJournal()
        journal.add(JournalLevel.Error, "start: PGC-NET-001 no answer", at = 1_000L)
        journal.add(JournalLevel.Info, "connected", at = 2_000L)
        journal.add(JournalLevel.Warning, "reconnect 1: PGC-SVC-002", at = 3_000L)
        journal.add(JournalLevel.Error, "start: PGC-NET-001 no answer", at = 4_000L)
        val tunnel = TunnelSettings(DnsChoice.Custom, "https://198.51.100.53/dns-query?token=secret-token", fakeDns = true)
        val report = CheckReport.build(context, MainUi(), CheckUi(journal = journal.entries.value), emptyList(), null, tunnel, "en")
        val lines = report.lines()

        assertTrue(context.getString(R.string.diag_report_dns_title) in lines, report)
        assertTrue("  " + context.getString(R.string.diag_report_dns_selected, context.getString(R.string.prot_dns_custom)) in lines, report)
        assertTrue(
            "  " + context.getString(R.string.diag_report_dns_custom, context.getString(R.string.diag_report_dns_custom_doh)) in lines,
            report,
        )
        assertFalse("secret-token" in report, "адреса своего DNS в отчете нет")
        assertTrue("  " + context.getString(R.string.diag_report_dns_fake, context.getString(R.string.diag_report_dns_on)) in lines, report)
        val start = lines.indexOf(context.getString(R.string.diag_report_problems))
        assertTrue(start > 0, report)
        val repeated = context.getString(R.string.diag_report_repeated, "2", JournalText.time(4_000L))
        assertEquals("  " + JournalText.line(JournalEntry(1_000L, JournalLevel.Error, "start: PGC-NET-001 no answer")) + "  " + repeated, lines[start + 1])
        assertEquals("  " + JournalText.line(JournalEntry(3_000L, JournalLevel.Warning, "reconnect 1: PGC-SVC-002")), lines[start + 2])
        assertEquals("", lines[start + 3], "информационные записи в раздел не идут")
    }

    @Test
    fun `file name has the date and time`() {
        val name = CheckReport.fileName(System.currentTimeMillis())
        assertTrue(Regex("""private-gateway-report-\d{8}-\d{4}\.txt""").matches(name), name)
    }

    @Test
    fun `journal entries are kept in a ring without secrets`() {
        val journal = TunnelJournal()
        repeat(TunnelJournal.CAPACITY + 5) { journal.add(JournalLevel.Debug, "entry $it", at = it.toLong()) }
        val entries = journal.entries.value
        assertEquals(TunnelJournal.CAPACITY, entries.size)
        assertEquals(JournalEntry(5L, JournalLevel.Debug, "entry 5"), entries.first())

        journal.warning("link vless://11111111-2222-3333-4444-555555555555@example.com:443")
        val masked = journal.entries.value.last().message
        assertFalse("11111111-2222-3333-4444-555555555555" in masked, masked)
        assertTrue(SecretMasker.HIDDEN in masked, masked)
    }
}

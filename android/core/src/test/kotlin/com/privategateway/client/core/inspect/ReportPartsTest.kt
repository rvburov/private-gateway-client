package com.privategateway.client.core.inspect

import com.privategateway.client.core.settings.DnsChoice
import com.privategateway.client.core.settings.TunnelSettings
import org.junit.jupiter.params.ParameterizedTest
import org.junit.jupiter.params.provider.CsvSource
import kotlin.test.assertEquals

/** Вид своего DNS для отчета — как `DnsReport` десктопа и iOS: сам адрес в отчет не попадает. */
class ReportPartsTest {
    @ParameterizedTest
    @CsvSource(
        "'', missing",
        "'  ', missing",
        "192.168.1.53, private",
        "198.51.100.53, address",
        "2001:db8::53, address",
        "https://198.51.100.53/dns-query?token=secret, doh",
        "dns.example.com, invalid",
        "https://dns.example.com/q, invalid",
    )
    fun `custom dns kind never shows the address`(custom: String, kind: String) {
        assertEquals(kind, ReportParts.customKind(TunnelSettings(DnsChoice.Custom, custom)))
    }
}

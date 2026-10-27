package com.privategateway.client.core.inspect

import org.junit.jupiter.api.Test
import org.junit.jupiter.params.ParameterizedTest
import org.junit.jupiter.params.provider.CsvSource
import org.junit.jupiter.params.provider.NullSource
import org.junit.jupiter.params.provider.ValueSource
import java.net.InetAddress
import kotlin.test.assertEquals
import kotlin.test.assertNull

/** Узел замера, внешний IP и его провайдер — те же случаи, что `ProbeTargetTests` и `AddressLookupTests` десктопа. */
class PublicAddressTest {
    @ParameterizedTest
    @CsvSource(
        "example.com, example.com",
        "'  Example.COM.  ', example.com",
        "www.example.org, www.example.org",
        "localhost, localhost",
        "xn--e1afmkfd.xn--p1ai, xn--e1afmkfd.xn--p1ai",
        "пример.рф, xn--e1afmkfd.xn--p1ai",
        "203.0.113.10, 203.0.113.10",
        "2001:db8::10, 2001:db8::10",
        "[2001:DB8::10], 2001:db8::10",
        "2001:0db8:0000::0010, 2001:db8::10",
    )
    fun `host is normalized like the desktop`(text: String, expected: String) {
        assertEquals(expected, ProbeTarget.normalize(text))
    }

    @ParameterizedTest
    @NullSource
    @ValueSource(
        strings = [
            "", "   ", "https://example.com/", "example.com/path", "example.com:443", "example.com; reboot", "\$(id).example.com",
            "exa mple.com", "-example.com", "example-.com", "example..com", "1.2.3", "12345", "user@example.com", "--help",
            "exa_mple.com", "[example.com]", "пример..рф", "256.1.1.1",
        ],
    )
    fun `anything else is not a host`(text: String?) {
        assertNull(ProbeTarget.normalize(text))
    }

    @Test
    fun `name longer than dns allows is not a host and port is 1 to 65535`() {
        assertEquals("a".repeat(63) + ".example.com", ProbeTarget.normalize("a".repeat(63) + ".example.com"))
        assertNull(ProbeTarget.normalize("a".repeat(64) + ".example.com"), "метка — не длиннее 63")
        assertNull(ProbeTarget.normalize(List(4) { "a".repeat(62) }.joinToString(".") + ".com"), "имя — не длиннее 253")
        assertEquals(443, ProbeTarget.port(" 443 "))
        listOf("0", "65536", "-1", "443a", "", null).forEach { assertNull(ProbeTarget.port(it), it.toString()) }
    }

    @ParameterizedTest
    @CsvSource(
        "2001:db8:0:0:0:0:0:1, 2001:db8::1",
        "2001:db8:0:0:1:0:0:1, 2001:db8::1:0:0:1",
        "0:0:0:0:0:0:0:1, ::1",
        "2001:db8:1:2:3:4:5:6, 2001:db8:1:2:3:4:5:6",
        "fe80:0:0:0:0:0:0:0, fe80::",
        "2001:0:0:1:0:0:0:1, 2001:0:0:1::1",
    )
    fun `ipv6 is written short`(full: String, short: String) {
        assertEquals(short, AddressText.of(InetAddress.getByName(full)))
    }

    @ParameterizedTest
    @CsvSource(
        "1.1.1.1, true", "9.9.9.9, true", "2606:4700:4700::1111, true", "::ffff:1.1.1.1, true", "11.0.0.1, true",
        "100.128.0.1, true", "172.32.0.1, true", "223.255.255.254, true", "0.0.0.0, false", "10.1.2.3, false",
        "100.64.0.1, false", "127.0.0.1, false", "169.254.1.1, false", "172.16.0.1, false", "172.31.255.254, false",
        "192.0.0.8, false", "192.0.2.1, false", "192.168.1.1, false", "198.18.0.1, false", "198.51.100.7, false",
        "203.0.113.24, false", "224.0.0.1, false", "255.255.255.255, false", "::1, false", "::ffff:10.0.0.1, false",
        "64:ff9b::101:101, false", "fe80::1, false", "fd00::1, false", "ff02::1, false", "2001:db8::1, false",
        "2001:0:4136:e378::1, false", "3fff::1, false",
    )
    fun `only a public address is worth asking about`(address: String, public: Boolean) {
        assertEquals(public, PublicAddress.isPublic(InetAddress.getByName(address)), address)
    }

    @Test
    fun `external ip comes from the trace answer`() {
        assertEquals("203.0.113.24", PublicAddress.traceIp("fl=1\nh=1.1.1.1\nip=203.0.113.24\nts=1\n"))
        assertEquals("2001:db8::7", PublicAddress.traceIp("ip=2001:0db8::7\n"))
        assertNull(PublicAddress.traceIp("ip=example.com\n"), "только адрес")
        assertNull(PublicAddress.traceIp("<html>"))
        assertNull(PublicAddress.traceIp(null))
    }

    @Test
    fun `ripestat answers give the provider network and place`() {
        val address = "203.0.113.24"
        assertEquals(AddressInfo(address, 64500, "EXAMPLE-AS - Example Hosting B.V.", "203.0.113.0/24", "NL", "Amsterdam", null), PublicAddress.parse(address, OVERVIEW, GEO))
        assertEquals(
            AddressInfo(address, null, null, null, "NL", "Amsterdam", null),
            PublicAddress.parse(address, """{"status":"ok","data":{"announced":false,"asns":[],"resource":"203.0.113.24"}}""", GEO),
            "сеть не объявлена — ни провайдера, ни сети, а место есть",
        )
        assertEquals(
            AddressInfo(address, 64500, "EXAMPLE-AS - Example Hosting B.V.", "203.0.113.0/24", "DE", null, null),
            PublicAddress.parse(
                address,
                OVERVIEW,
                """{"status":"ok","data":{"located_resources":[{"resource":"203.0.113.0/24","locations":[{"country":"??","city":""},{"country":"de","city":" "}]}]}}""",
            ),
            "«??» — не страна, пустой город — не город",
        )
        assertNull(PublicAddress.parse(address, OVERVIEW, null).country, "место не ответило — провайдер все равно есть")
        assertNull(PublicAddress.parse(address, """{"status":"ok","data":{"announced":false,"asns":[],"resource":"203.0.113.0/24"}}""", GEO).prefix, "сеть — только объявленная")
        assertEquals(
            AddressInfo(address, 64500, null, null, null, null, null),
            PublicAddress.parse(address, """{"status":"ok","data":{"announced":true,"asns":[{"asn":64500}],"resource":"203.0.113.24"}}""", null),
            "сам адрес вместо сети — не сеть; владельца нет — только номер",
        )
    }

    @Test
    fun `without answers or without facts the reason is named`() {
        val address = "203.0.113.24"
        assertEquals(AddressFailure.Unreachable, PublicAddress.parse(address, null, null).failure)
        assertEquals(AddressFailure.NotFound, PublicAddress.parse(address, "<html>", "<html>").failure, "ответ был, но не о том")
        assertEquals(AddressFailure.NotFound, PublicAddress.parse(address, """{"status":"error","data":{"asns":[{"asn":64500}]}}""", null).failure, "ответ с ошибкой — не сведения")
        assertEquals(
            AddressFailure.NotFound,
            PublicAddress.parse(address, """{"status":"ok","data":{"announced":true,"asns":[{"asn":0}],"resource":"203.0.113.0/24"}}""", """{"status":"ok","data":{"located_resources":[]}}""").failure,
        )
    }

    @Test
    fun `requests carry only the canonical address`() {
        assertEquals(
            listOf(
                "https://stat.ripe.net/data/prefix-overview/data.json?resource=2606:4700:4700::1111&sourceapp=private-gateway",
                "https://stat.ripe.net/data/maxmind-geo-lite/data.json?resource=2606:4700:4700::1111&sourceapp=private-gateway",
            ),
            PublicAddress.requests(InetAddress.getByName("2606:4700:4700:0:0:0:0:1111")),
        )
    }

    private companion object {
        const val OVERVIEW = """{"messages":[],"data_call_name":"prefix-overview","data":{"is_less_specific":false,"announced":true,
 "asns":[{"asn":64500,"holder":"EXAMPLE-AS - Example Hosting B.V."}],"related_prefixes":[],"resource":"203.0.113.0/24",
 "type":"prefix","block":{"resource":"203.0.0.0/8","desc":"APNIC (Status: ALLOCATED)","name":"IANA IPv4 Address Space Registry"},
 "actual_num_related":0,"query_time":"2026-10-26T12:00:00","num_filtered_out":0},"status":"ok","status_code":200}"""

        const val GEO = """{"messages":[],"data_call_name":"maxmind-geo-lite","data":{"located_resources":[{"resource":"203.0.113.0/24",
 "locations":[{"country":"NL","city":"Amsterdam","resources":["203.0.113.0/24"],"latitude":52.37,"longitude":4.89,"covered_percentage":100.0}]}],
 "result_time":"2026-10-01T00:00:00"},"status":"ok","status_code":200}"""
    }
}

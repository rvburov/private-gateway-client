package com.privategateway.client.core.settings

import org.junit.jupiter.api.Test
import org.junit.jupiter.params.ParameterizedTest
import org.junit.jupiter.params.provider.CsvSource
import org.junit.jupiter.params.provider.ValueSource
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertNull

/** Запись адреса IP — строго, как для своего DNS, — и частные сети, как у десктопа (`ProtectionSettingsTests`). */
class IpAddressTest {
    @ParameterizedTest
    @CsvSource(
        "192.0.2.53, c0000235",
        "0.0.0.0, 00000000",
        "255.255.255.255, ffffffff",
        "2001:db8::53, 20010db8000000000000000000000053",
        "2001:DB8::53, 20010db8000000000000000000000053",
        "::, 00000000000000000000000000000000",
        "::1, 00000000000000000000000000000001",
        "1::, 00010000000000000000000000000000",
        "1:2:3:4:5:6:7:8, 00010002000300040005000600070008",
        "1:2:3:4:5:6:7::, 00010002000300040005000600070000",
        "::2:3:4:5:6:7:8, 00000002000300040005000600070008",
        "1:2:3::6:7:8, 00010002000300000000000600070008",
        "fd00:0:0:0:0:0:0:53, fd000000000000000000000000000053",
        "::ffff:192.0.2.53, 00000000000000000000ffffc0000235",
        "1:2:3:4:5:6:192.0.2.53, 000100020003000400050006c0000235",
        "ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff, ffffffffffffffffffffffffffffffff",
    )
    fun `address literals give their bytes`(text: String, hex: String) {
        assertContentEquals(hex.chunked(2).map { it.toInt(16).toByte() }.toByteArray(), IpAddress.parse(text))
    }

    @ParameterizedTest
    @ValueSource(
        strings = [
            "", "1.2", "1.2.3", "1.2.3.4.5", "256.1.1.1", "010.0.0.1", "1.2.3.", " 192.0.2.53",
            "::::", ":::", "1::2::3", "::1::", "1:2:3:4:5:6:7:8:9", "1:2:3:4:5:6:7", "1:2:3:4:5:6:7:8::", "::1:2:3:4:5:6:7:8",
            "12345::", "g::1", ":1::2", "1::2:", ":1:2:3:4:5:6:7:8", "1:2:3:4:5:6:7:8:", "1.2.3.4::", "::1.2.3", "::256.0.0.1",
            "1:2:3:4:5:6:7:1.2.3.4", "::1.2.3.4:5", "fe80::1%wlan0", "[2001:db8::1]", "2001:db8::1/64", "dns.example.com", "abcd",
        ],
    )
    fun `anything else is not an address`(text: String) {
        assertNull(IpAddress.parse(text))
    }

    @ParameterizedTest
    @CsvSource(
        "10.0.0.53, true",
        "172.16.0.1, true",
        "172.31.255.1, true",
        "192.168.1.53, true",
        "169.254.1.1, true",
        "fd00::53, true",
        "fc00::1, true",
        "fe80::1, true",
        "febf::1, true",
        "::ffff:192.168.1.53, true",
        "172.32.0.1, false",
        "172.15.0.1, false",
        "11.0.0.1, false",
        "192.169.0.1, false",
        "169.255.0.1, false",
        "9.9.9.9, false",
        "2001:db8::53, false",
        "fe00::1, false",
        "fec0::1, false",
        "::ffff:198.51.100.53, false",
        "::ffff:0:192.168.1.53, false",
        "dns.example.com, false",
    )
    fun `private networks are the ones the local network setting opens`(address: String, lan: Boolean) {
        assertEquals(lan, PrivateNetwork.contains(address))
    }

    @Test
    fun `ipv4 inside ipv6 counts as ipv4 only in the mapped form`() {
        assertEquals(true, PrivateNetwork.contains("0:0:0:0:0:ffff:a00:35"), "::ffff:10.0.0.53 — частная сеть")
        assertEquals(false, PrivateNetwork.contains("::a00:35"), "без ffff — адрес IPv6, не частный")
        assertEquals(false, PrivateNetwork.contains("::1:ffff:192.168.1.53"), "перед ffff — только нули")
    }
}

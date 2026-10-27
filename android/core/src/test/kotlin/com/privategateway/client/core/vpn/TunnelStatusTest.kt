package com.privategateway.client.core.vpn

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonObject
import org.junit.jupiter.api.Test
import java.io.File
import kotlin.test.assertEquals

class TunnelStatusTest {
    @Test
    fun `nothing is known before the first start`() {
        assertEquals(TunnelStatus(TunnelState.Off, null, null, null, null, null, attempt = 0), TunnelStatus())
    }

    @Test
    fun `codes are the desktop ones and have texts in both languages`() {
        val codes = listOf(TunnelErrors.CORE_FAILED, TunnelErrors.NO_SERVER, TunnelErrors.UNSUPPORTED, TunnelErrors.SERVER_NOT_RESPONDING, TunnelErrors.OTHER_VPN)
        assertEquals(listOf("PGC-SVC-002", "PGC-SVC-003", "PGC-SVC-005", "PGC-NET-001", "PGC-NET-010"), codes)
        val localization = File(System.getProperty("testdata") ?: error("Нет пути к shared/testdata")).resolve("../specifications/localization")
        for (language in listOf("ru", "en")) {
            val texts = Json.parseToJsonElement(localization.resolve("$language.json").readText()).jsonObject
            assertEquals(emptyList(), codes.filter { "error.$it" !in texts }, language)
        }
    }
}

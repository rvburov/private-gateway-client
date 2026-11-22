package com.privategateway.client.vpn

import androidx.test.ext.junit.runners.AndroidJUnit4
import com.privategateway.client.core.servers.LatencyReason
import com.privategateway.client.core.xray.PingBatch
import com.privategateway.client.data.BlockedAccess
import com.privategateway.client.data.BlockedSites
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import org.json.JSONObject
import org.junit.Test
import org.junit.runner.RunWith
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

@RunWith(AndroidJUnit4::class)
class NativeProbePrivacyTest {
    @Test
    fun `native diagnostics keep actual delay and category without returning any arbitrary strings`() {
        val source = """{"success":true,"raw":"p7!","data":{"results":[{"success":true,"delay":42,"detail":"p7!"},{"success":false,"error":"TLS error with header p7!","delay":10000}]}}"""
        val safe = assertNotNull(NativeProbeResponse.safe(source))
        assertFalse(safe.contains("p7!"))
        assertEquals(42, PingBatch.parse(safe, 2)[0].ms)
        assertEquals(LatencyReason.Tls, PingBatch.parse(safe, 2)[1].reason)
        val failed = assertNotNull(NativeProbeResponse.safe("""{"success":false,"error":"invalid custom header p7!"}"""))
        assertFalse(failed.contains("p7!"))
        assertTrue(failed.contains("probe failed"))
    }

    @Test
    fun `invalid or oversized native output fails closed and main process console is never redirected`() {
        assertNull(NativeProbeResponse.safe("not json"))
        assertNull(NativeProbeResponse.safe("x".repeat(LatencyProbeService.MAX_REQUEST + 1)))
        assertFalse(NativeProbeLogs.discard())
    }

    @Test
    fun `main core errors never fall through to console even without site block rules`() {
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        val access = BlockedAccess(BlockedSites(), scope)
        try {
            val root = JSONObject(access.configure("""{"log":{"loglevel":"warning","access":"none"}}""", enabled = false))
            val log = root.getJSONObject("log")
            assertEquals("none", log.getString("access"))
            assertTrue(log.getString("error").startsWith("/proc/self/fd/") || log.getString("loglevel") == "none")
            assertTrue(log.getString("loglevel") in setOf("warning", "none"))
        } finally {
            access.close()
            scope.cancel()
        }
    }
}

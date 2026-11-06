package com.privategateway.client.vpn

import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.privategateway.client.core.links.LinkParser
import org.junit.Test
import org.junit.runner.RunWith
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertTrue

/** Проба сервера мимо туннеля: UDP-протоколы по TCP не меряются — так и пишется, без ложного «сервер молчит». */
@RunWith(AndroidJUnit4::class)
class ServerTcpTest {
    @Test
    fun udp_servers_are_not_judged_by_tcp() {
        val hysteria = assertNotNull(LinkParser.parse("hysteria2://pg-test-password@203.0.113.10:443?sni=example.com#Hy2").server)
        val vless = assertNotNull(
            LinkParser.parse("vless://11111111-2222-3333-4444-555555555555@203.0.113.10:443?encryption=none&security=none&type=tcp#V").server,
        )

        assertFalse(ServerTcp.measurable(hysteria))
        assertTrue(ServerTcp.measurable(vless))
        assertEquals("server tcp not checked  udp protocol", ServerTcp.verdict(ApplicationProvider.getApplicationContext(), hysteria))
    }
}

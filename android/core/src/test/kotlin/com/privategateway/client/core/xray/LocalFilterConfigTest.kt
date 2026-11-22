package com.privategateway.client.core.xray

import com.privategateway.client.core.links.JsonTree
import com.privategateway.client.core.servers.ProtocolOptions
import com.privategateway.client.core.servers.ProxyProtocol
import com.privategateway.client.core.servers.SecurityOptions
import com.privategateway.client.core.servers.ServerCredentials
import com.privategateway.client.core.servers.ServerDefinition
import com.privategateway.client.core.servers.TransportOptions
import com.privategateway.client.core.settings.RuleAction
import com.privategateway.client.core.settings.SiteRule
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertTrue

class LocalFilterConfigTest {
    @Test
    fun `local filtering keeps block and dns routes but contains no remote protocol or credentials`() {
        val anchor = ServerDefinition("local", "", ProxyProtocol.Vless, "127.0.0.1", 1, TransportOptions.Tcp, SecurityOptions.None, ProtocolOptions.Empty, ServerCredentials())
        val settings = CoreSettings(rules = listOf(SiteRule("example.com", RuleAction.Block)), blockOthers = true)
        assertFalse(XrayConfig.build(anchor, settings, 7).isOk, "пустой профиль не должен стать обычным VPN")
        val json = assertNotNull(XrayConfig.build(anchor, settings, 7, localOnly = true).json)
        val root = JsonTree.parse(json).value as Map<*, *>
        val outbounds = root["outbounds"] as List<*>
        val first = outbounds.first() as Map<*, *>
        assertEquals("freedom", first["protocol"])
        assertEquals(XrayConfig.PROXY_TAG, first["tag"])
        assertTrue(json.contains("domain:example.com"))
        assertTrue(json.contains("dns-out"))
        assertFalse(json.contains("vless"))
        assertFalse(json.contains("vnext"))
    }
}

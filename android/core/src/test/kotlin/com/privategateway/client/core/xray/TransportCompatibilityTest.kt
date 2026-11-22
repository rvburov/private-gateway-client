package com.privategateway.client.core.xray

import com.privategateway.client.core.links.ImportErrors
import com.privategateway.client.core.links.LinkParser
import com.privategateway.client.core.links.LinkWriter
import com.privategateway.client.core.servers.TransportParameterRules
import java.io.File
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

class TransportCompatibilityTest {
    @Test
    fun `sensitive headers include nested downloads without treating other fields as credentials`() {
        val server = fixture("xhttp-extra")
        val extra = """{"headers":{"X-Private-Key":"demo-secret","Authorization":"Bearer demo-bearer"},"xPaddingKey":"public-padding","downloadSettings":{"network":"xhttp","xhttpSettings":{"extra":{"headers":{"Cookie":"demo-cookie"}}}}}"""
        assertEquals(setOf("demo-secret", "Bearer demo-bearer", "demo-cookie"), TransportParameterRules.sensitiveExtraValues(server.copy(transport = server.transport.copy(extraJson = extra))).toSet())
        val invalid = """{"headers":{"X-Key":"untrusted"},"sockopt":{}}"""
        assertTrue(TransportParameterRules.sensitiveExtraValues(server.copy(transport = server.transport.copy(extraJson = invalid))).isEmpty())
    }

    @Test
    fun `extra and alpn survive stored links and reach only xhttp settings`() {
        val original = fixture("xhttp-extra")
        val server = assertNotNull(LinkParser.parse(LinkWriter.write(original)).server)
        assertEquals(original, server)
        assertEquals(listOf("h3"), server.security.alpn)
        val result = XrayConfig.build(server, CoreSettings(), 42)
        val config = Json.parseToJsonElement(assertNotNull(result.json)).jsonObject
        val proxy = config["outbounds"]!!.jsonArray.map { it.jsonObject }.single { it["tag"]!!.jsonPrimitive.content == "proxy" }
        val extra = proxy["streamSettings"]!!.jsonObject["xhttpSettings"]!!.jsonObject["extra"]!!.jsonObject
        assertEquals("true", extra["noGRPCHeader"]!!.jsonPrimitive.content)
        assertEquals("4-8", extra["xmux"]!!.jsonObject["maxConcurrency"]!!.jsonPrimitive.content)
        assertEquals("download.example.com", extra["downloadSettings"]!!.jsonObject["address"]!!.jsonPrimitive.content)
    }

    @Test
    fun `vless encryption is not silently replaced with none and supports transport without tls`() {
        val server = fixture("encryption-mlkem")
        assertFalse(XrayConfig.unencrypted(server))
        val config = Json.parseToJsonElement(assertNotNull(XrayConfig.build(server, CoreSettings(), 42).json)).jsonObject
        val proxy = config["outbounds"]!!.jsonArray.map { it.jsonObject }.single { it["tag"]!!.jsonPrimitive.content == "proxy" }
        val user = proxy["settings"]!!.jsonObject["vnext"]!!.jsonArray[0].jsonObject["users"]!!.jsonArray[0].jsonObject
        assertEquals(server.options.encryption, user["encryption"]!!.jsonPrimitive.content)
    }

    @Test
    fun `unsafe extras and unsupported encryption are rejected before the core`() {
        val server = fixture("xhttp-extra")
        val unsafe = "{\"downloadSettings\":{\"sockopt\":{\"dialerProxy\":\"direct\"}}}"
        assertNull(TransportParameterRules.normalizeExtra(unsafe))
        assertEquals(CoreRejection.UnsupportedTransport, XrayConfig.build(server.copy(transport = server.transport.copy(extraJson = unsafe)), CoreSettings(), 42).rejection)
        val unsupported = server.copy(options = server.options.copy(encryption = "unknown"))
        assertEquals(CoreRejection.UnsupportedTransport, XrayConfig.build(unsupported, CoreSettings(), 42).rejection)
        assertEquals(ImportErrors.UNSUPPORTED_PARAMETER, LinkParser.parse(LinkWriter.write(unsupported)).error)
        assertFalse(TransportParameterRules.validEncryption("mlkem768x25519plus.native.1rtt.bad"))
        assertTrue(TransportParameterRules.validEncryption(server.options.encryption))
    }

    private fun fixture(name: String) = assertNotNull(LinkParser.parse(File(System.getProperty("testdata"), "links/vless/$name.txt").readText()).server)
}

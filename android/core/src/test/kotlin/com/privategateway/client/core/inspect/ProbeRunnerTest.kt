package com.privategateway.client.core.inspect

import org.junit.jupiter.api.AfterAll
import org.junit.jupiter.api.BeforeAll
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.TestInstance
import java.io.File
import java.io.IOException
import java.net.InetAddress
import java.net.ServerSocket
import java.net.UnknownHostException
import java.nio.file.Files
import java.security.KeyStore
import java.security.cert.X509Certificate
import java.time.Instant
import javax.net.ssl.ExtendedSSLSession
import javax.net.ssl.KeyManagerFactory
import javax.net.ssl.SNIHostName
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLServerSocket
import javax.net.ssl.SSLSocket
import javax.net.ssl.TrustManagerFactory
import javax.net.ssl.X509TrustManager
import kotlin.concurrent.thread
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertTrue

/**
 * Замеры инспектора Android на 127.0.0.1: открытый и закрытый порт, рукопожатие TLS с сертификатом, выпущенным в тесте
 * (keytool из JDK, ключ живет только на время теста), — доверенный, чужой, на другое имя, вне срока.
 */
@TestInstance(TestInstance.Lifecycle.PER_CLASS)
class ProbeRunnerTest {
    private val dir = Files.createTempDirectory("pg-probe").toFile()
    private lateinit var store: KeyStore
    private lateinit var trusted: X509TrustManager
    private lateinit var server: SSLServerSocket
    private val loopback = InetAddress.getLoopbackAddress()

    /** Какое имя назвал клиент в рукопожатии (SNI) — по нему сервер с несколькими сайтами выбирает сертификат. */
    @Volatile private var requested: List<String> = emptyList()

    @BeforeAll
    fun start() {
        store = keystore(dir.resolve("server.p12"))
        val trustStore = KeyStore.getInstance("PKCS12").apply {
            load(null, null)
            setCertificateEntry("pg", store.getCertificate("pg"))
        }
        trusted = TrustManagerFactory.getInstance(TrustManagerFactory.getDefaultAlgorithm()).apply { init(trustStore) }
            .trustManagers.filterIsInstance<X509TrustManager>().first()

        val keys = KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm()).apply { init(store, PASS.toCharArray()) }
        val context = SSLContext.getInstance("TLS").apply { init(keys.keyManagers, null, null) }
        server = context.serverSocketFactory.createServerSocket(0, 10, loopback) as SSLServerSocket
        server.sslParameters = server.sslParameters.apply { applicationProtocols = arrayOf("h2") }
        thread(isDaemon = true) {
            while (!server.isClosed) {
                try {
                    (server.accept() as SSLSocket).use {
                        it.startHandshake()
                        requested = (it.session as ExtendedSSLSession).requestedServerNames.filterIsInstance<SNIHostName>().map { name -> name.asciiName }
                        it.inputStream.read()
                    }
                } catch (_: IOException) {
                    // Клиент закрыл соединение или не прошел рукопожатие — ждем следующего.
                }
            }
        }
    }

    @AfterAll
    fun stop() {
        server.close()
        dir.deleteRecursively()
    }

    private fun runner(trust: X509TrustManager = trusted, now: Instant = Instant.now(), names: Map<String, List<InetAddress>> = mapOf("localhost" to listOf(loopback))) =
        ProbeRunner(resolve = { names[it] ?: throw UnknownHostException(it) }, trust = trust, now = { now })

    @Test
    fun `trusted certificate for the name gives the handshake facts and no problems`() {
        val result = runner().tls("localhost", server.localPort)
        val step = result.steps.single()
        assertEquals(ProbeOutcome.Ok, step.outcome)
        assertEquals("127.0.0.1", step.address)
        val tls = assertNotNull(step.tls)
        assertEquals("TLSv1.3", tls.protocol)
        assertEquals("h2", tls.alpn)
        assertTrue(tls.subject.contains("CN=localhost"), tls.subject)
        assertEquals(listOf("localhost", "127.0.0.1"), tls.names)
        assertEquals(emptyList(), tls.problems)
        assertEquals(ProbeSummary("probe.summary.tls", listOf("TLSv1.3")), result.summary())
        assertEquals(listOf("localhost"), requested, "имя узла уходит в SNI")
    }

    @Test
    fun `self signed certificate is untrusted for the phone roots`() {
        val result = runner(trust = ProbeRunner.defaultTrust()).tls("localhost", server.localPort)
        assertEquals(listOf(TlsProblem.Untrusted), result.steps.single().tls?.problems)
        assertEquals(ProbeSummary("probe.summary.tls.problems"), result.summary())
    }

    @Test
    fun `certificate for another name and out of its dates is said so`() {
        val other = runner(names = mapOf("pg.example.org" to listOf(loopback))).tls("pg.example.org", server.localPort)
        assertEquals(listOf(TlsProblem.NameMismatch), other.steps.single().tls?.problems)

        val tls = runner(now = Instant.now().plusSeconds(400L * 24 * 3600)).tls("localhost", server.localPort).steps.single().tls
        assertEquals(listOf(TlsProblem.Expired), tls?.problems)
        assertEquals(listOf(TlsProblem.NotYetValid), runner(now = Instant.now().minusSeconds(3 * 24 * 3600)).tls("localhost", server.localPort).steps.single().tls?.problems)
    }

    @Test
    fun `ip target is checked against ip names`() {
        val result = runner(names = mapOf("127.0.0.1" to listOf(loopback))).tls("127.0.0.1", server.localPort)
        assertEquals(emptyList(), result.steps.single().tls?.problems)
        assertEquals(emptyList(), requested, "адрес в SNI не отправляется")
    }

    @Test
    fun `not tls on the port is a failed handshake`() {
        ServerSocket(0, 5, loopback).use { plain ->
            thread(isDaemon = true) { runCatching { plain.accept().use { it.getOutputStream().write("HTTP/1.1 400 Bad\r\n\r\n".toByteArray()) } } }
            val result = runner().tls("localhost", plain.localPort)
            assertEquals(ProbeOutcome.Failed, result.steps.single().outcome)
            assertEquals(ProbeSummary("probe.summary.failed"), result.summary())
        }
    }

    @Test
    fun `tcp port open closed and name not found`() {
        val open = runner().tcp("localhost", server.localPort)
        assertEquals(ProbeOutcome.Ok, open.steps.single().outcome)
        assertEquals("probe.summary.tcp", open.summary().key)
        assertEquals(server.localPort.toString(), open.summary().args.first())

        val closed = ServerSocket(0, 1, loopback).use { it.localPort }
        val refused = runner().tcp("localhost", closed)
        assertEquals(ProbeOutcome.Refused, refused.steps.single().outcome)
        assertEquals(ProbeSummary("probe.summary.refused", listOf(closed.toString())), refused.summary())

        val missing = runner().tcp("missing.example.org", 443)
        assertEquals(ProbeOutcome.NotFound, missing.steps.single().outcome)
        assertEquals(ProbeSummary("probe.summary.notFound"), missing.summary())
    }

    @Test
    fun `dns lists every address and says when the name is not found`() {
        val names = mapOf("example.org" to listOf(InetAddress.getByName("203.0.113.10"), InetAddress.getByName("2001:db8:0:0:0:0:0:10")), "empty.example.org" to emptyList())
        val found = runner(names = names).dns("example.org")
        assertEquals(listOf("203.0.113.10", "2001:db8::10"), found.steps.map { it.address })
        assertEquals(listOf(1, 2), found.steps.map { it.index })
        assertEquals("probe.summary.dns", found.summary().key)
        assertEquals("2", found.summary().args.first())

        assertEquals(ProbeSummary("probe.summary.notFound"), runner(names = names).dns("empty.example.org").summary())
        assertEquals(ProbeSummary("probe.summary.notFound"), runner(names = names).dns("missing.example.org").summary())
    }

    @Test
    fun `wildcard names match one label only`() {
        assertTrue(ProbeRunner.Names.matches(listOf("*.example.com"), "www.example.com"))
        assertFalse(ProbeRunner.Names.matches(listOf("*.example.com"), "example.com"))
        assertFalse(ProbeRunner.Names.matches(listOf("*.example.com"), "a.b.example.com"))
        assertTrue(ProbeRunner.Names.matches(listOf("Example.COM"), "example.com"))
        assertFalse(ProbeRunner.Names.matches(listOf("example.com"), "203.0.113.10"), "адрес — только по IP из SAN")
        assertTrue(ProbeRunner.Names.matches(listOf("2001:db8:0:0:0:0:0:10"), "2001:db8::10"))
    }

    @Test
    fun `certificate without san is named by its common name`() {
        val certificate = keystore(dir.resolve("nosan.p12"), san = null).getCertificate("pg") as X509Certificate
        assertEquals(listOf("localhost"), ProbeRunner.names(certificate))
    }

    private fun keystore(file: File, san: String? = "dns:localhost,ip:127.0.0.1"): KeyStore {
        val keytool = File(System.getProperty("java.home"), "bin/keytool").path
        val command = mutableListOf(
            keytool, "-genkeypair", "-alias", "pg", "-keyalg", "EC", "-groupname", "secp256r1", "-dname", "CN=localhost",
            "-validity", "30", "-storetype", "PKCS12", "-keystore", file.path, "-storepass", PASS, "-keypass", PASS,
        )
        san?.let { command += listOf("-ext", "SAN=$it") }
        val process = ProcessBuilder(command).redirectErrorStream(true).start()
        val output = process.inputStream.bufferedReader().readText()
        check(process.waitFor() == 0) { output }
        return KeyStore.getInstance("PKCS12").apply { file.inputStream().use { load(it, PASS.toCharArray()) } }
    }

    private companion object {
        /** Пароль хранилища ключа, который живет только на время теста. */
        const val PASS = "test-only-store"
    }
}

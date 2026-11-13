package com.privategateway.client.core.account

import com.sun.net.httpserver.HttpServer
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.jupiter.api.AfterEach
import org.junit.jupiter.api.Test
import org.junit.jupiter.params.ParameterizedTest
import org.junit.jupiter.params.provider.CsvSource
import org.junit.jupiter.params.provider.Arguments
import org.junit.jupiter.params.provider.MethodSource
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.URI
import java.time.Instant
import java.util.concurrent.CopyOnWriteArrayList
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

/** API сайта на локальном сервере 127.0.0.1 — сети машины тест не касается. */
class AccountApiTest {
    private val server = HttpServer.create(InetSocketAddress(InetAddress.getLoopbackAddress(), 0), 0).apply { start() }
    private val base = URI("http://127.0.0.1:${server.address.port}")
    private val api = AccountApi(base)
    private val device = DeviceInfo("11111111-2222-3333-4444-555555555555", "Google Pixel 8", "0.2.0")

    /** Что пришло на сервер: метод, путь, заголовок Authorization и тело. */
    private class Request(val method: String, val path: String, val authorization: String?, val contentType: String?, val body: String)

    private val requests = CopyOnWriteArrayList<Request>()

    @AfterEach
    fun stop() = server.stop(0)

    private fun serve(path: String, status: Int, body: String = "", headers: Map<String, String> = emptyMap()) {
        server.createContext(path) { exchange ->
            requests += Request(
                exchange.requestMethod,
                exchange.requestURI.path,
                exchange.requestHeaders.getFirst("Authorization"),
                exchange.requestHeaders.getFirst("Content-Type"),
                exchange.requestBody.readBytes().toString(Charsets.UTF_8),
            )
            headers.forEach { (name, value) -> exchange.responseHeaders.add(name, value) }
            exchange.responseHeaders.add("Content-Type", "application/json")
            val bytes = body.toByteArray()
            exchange.sendResponseHeaders(status, if (bytes.isEmpty()) -1 else bytes.size.toLong())
            exchange.responseBody.use { it.write(bytes) }
        }
    }

    private val user = """{"email":"user@example.com","display_name":"Участник"}"""
    private val deviceJson = """{"id":42,"name":"Google Pixel 8","platform":"android","app_version":"0.2.0","ip_masked":"192.0.2.x",""" +
        """"created_at":"2026-11-12T10:00:00Z","last_seen_at":"2026-11-12T10:00:00Z"}"""
    private val loginOk = """{"token":"tok-secret-1","expires_at":"2026-12-12T10:00:00.123456Z","user":$user,"device":$deviceJson}"""

    private fun body(request: Request): JsonObject = Json.parseToJsonElement(request.body).jsonObject

    @Test
    fun `login sends the device and a 2FA code and reads the session`() {
        serve("/api/app/login", 200, loginOk)
        val result = api.login(" user@example.com ", "pw \"q\"", "123456", null, device)

        val session = (result as LoginResult.Ok).session
        assertEquals("tok-secret-1", session.token)
        assertEquals(Instant.parse("2026-12-12T10:00:00.123456Z"), session.expiresAt)
        assertEquals(AccountUser("user@example.com", "Участник"), session.user)
        assertEquals(AccountDevice(42, "Google Pixel 8"), session.device)
        assertFalse("tok-secret-1" in session.toString(), "токена нет в toString")

        val request = requests.single()
        assertEquals("POST", request.method)
        assertNull(request.authorization)
        assertTrue(request.contentType.orEmpty().startsWith("application/json"))
        val sent = body(request)
        assertEquals("user@example.com", sent["email"]?.jsonPrimitive?.content, "почта без пробелов по краям")
        assertEquals("pw \"q\"", sent["password"]?.jsonPrimitive?.content, "пароль как введен, кавычки экранированы")
        assertEquals("123456", sent["code"]?.jsonPrimitive?.content)
        assertFalse("recovery_code" in sent, "ровно один из кодов")
        val sentDevice = sent["device"]!!.jsonObject
        assertEquals(device.uid, sentDevice["uid"]?.jsonPrimitive?.content)
        assertEquals("Google Pixel 8", sentDevice["name"]?.jsonPrimitive?.content)
        assertEquals("android", sentDevice["platform"]?.jsonPrimitive?.content)
        assertEquals("0.2.0", sentDevice["app_version"]?.jsonPrimitive?.content)
    }

    @Test
    fun `login with a recovery code sends recovery_code only`() {
        serve("/api/app/login", 200, loginOk)
        api.login("user@example.com", "pw", null, " abcd-efgh ", device)
        val sent = body(requests.single())
        assertEquals("abcd-efgh", sent["recovery_code"]?.jsonPrimitive?.content)
        assertFalse("code" in sent)
        assertFailsWith<IllegalArgumentException> { api.login("user@example.com", "pw", "123456", "abcd", device) }
    }

    @Test
    fun `first step sends only the email and the password`() {
        // 0.2.1: 2FA — только у администраторов; участник входит почтой и паролем, кода в запросе нет вовсе.
        serve("/api/app/login", 200, loginOk)
        assertTrue(api.login("user@example.com", "pw", null, null, device) is LoginResult.Ok)
        assertEquals(setOf("email", "password", "device"), body(requests.single()).keys)
    }

    @Test
    fun `code_required asks for the second step`() {
        serve("/api/app/login", 401, """{"error":{"code":"code_required"}}""")
        assertEquals(LoginResult.CodeRequired, api.login("admin@example.com", "pw", null, null, device))
    }

    @ParameterizedTest
    @CsvSource(
        "401, invalid_credentials, Invalid",
        "401, some_new_reason, Invalid",
        "400, invalid_code, Code",
        "400, code_required, Code",
        "403, setup_required, SetupRequired",
        "403, device_change_limit, SetupRequired",
        "409, device_limit, DeviceLimit",
        "409, device_change_limit, DeviceChangeLimit",
        "409, some_new_limit, DeviceLimit",
        "422, validation_error, Validation",
        "500, internal, Server",
        "502, bad_gateway, Server",
        "503, busy, Server",
        "404, not_found, Server",
    )
    fun `login errors map to their reasons`(status: Int, code: String, error: AccountError) {
        serve("/api/app/login", status, """{"error":{"code":"$code"}}""")
        assertEquals(LoginResult.Failed(error), api.login("user@example.com", "pw", "123456", null, device))
    }

    @ParameterizedTest
    @MethodSource("unreadableErrors")
    fun `unreadable error body falls back to the safe reason of the status`(status: Int, body: String, error: AccountError) {
        // Незнакомый или нечитаемый ответ не открывает второй шаг: 401 — «неверная почта или пароль», 409 — «лимит устройств».
        serve("/api/app/login", status, body)
        assertEquals(LoginResult.Failed(error), api.login("user@example.com", "pw", null, null, device))
    }

    @ParameterizedTest
    @CsvSource("1, 1", "60, 1", "61, 2", "600, 10", "0, 1", "soon, 1")
    fun `rate limit gives minutes from Retry-After rounded up`(header: String, minutes: Int) {
        serve("/api/app/login", 429, """{"error":{"code":"rate_limited"}}""", mapOf("Retry-After" to header))
        assertEquals(LoginResult.Failed(AccountError.RateLimited, minutes), api.login("user@example.com", "pw", "123456", null, device))
    }

    @Test
    fun `rate limit without Retry-After is one minute`() {
        serve("/api/app/login", 429, """{"error":{"code":"rate_limited"}}""")
        assertEquals(LoginResult.Failed(AccountError.RateLimited, 1), api.login("user@example.com", "pw", "123456", null, device))
    }

    @Test
    fun `garbage or incomplete success is a server error`() {
        serve("/api/app/login", 200, "<html>не JSON</html>")
        assertEquals(LoginResult.Failed(AccountError.Server), api.login("user@example.com", "pw", "123456", null, device))
        server.removeContext("/api/app/login")
        serve("/api/app/login", 200, """{"token":"t","expires_at":"не дата","user":$user,"device":$deviceJson}""")
        assertEquals(LoginResult.Failed(AccountError.Server), api.login("user@example.com", "pw", "123456", null, device))
        server.removeContext("/api/app/login")
        serve("/api/app/login", 200, """{"token":"","expires_at":"2026-12-12T10:00:00Z","user":$user,"device":$deviceJson}""")
        assertEquals(LoginResult.Failed(AccountError.Server), api.login("user@example.com", "pw", "123456", null, device))
    }

    @Test
    fun `no answer is a network error`() {
        val port = ServerSocket(0, 1, InetAddress.getLoopbackAddress()).use { it.localPort }
        val closed = AccountApi(URI("http://127.0.0.1:$port"), timeoutMs = 2_000)
        assertEquals(LoginResult.Failed(AccountError.Network), closed.login("user@example.com", "pw", "123456", null, device))
        assertEquals(StatusResult.Unreachable, closed.status("tok"))
        assertFalse(closed.logout("tok"))
    }

    @Test
    fun `status sends the bearer token and reads the new expiry`() {
        serve("/api/app/status", 200, """{"status":"ok","expires_at":"2027-01-11T10:00:00+00:00","user":$user,"device":$deviceJson}""")
        val result = api.status("tok-secret-1")
        assertEquals(StatusResult.Ok(Instant.parse("2027-01-11T10:00:00Z"), AccountUser("user@example.com", "Участник"), AccountDevice(42, "Google Pixel 8")), result)
        val request = requests.single()
        assertEquals("GET", request.method)
        assertEquals("Bearer tok-secret-1", request.authorization)
    }

    @ParameterizedTest
    @CsvSource("unauthorized", "code_required")
    fun `only 401 revokes access whatever the code`(code: String) {
        // Код сайта читается только при входе: у проверки любой 401 — отзыв, а не второй шаг входа.
        serve("/api/app/status", 401, """{"error":{"code":"$code"}}""")
        assertEquals(StatusResult.Revoked, api.status("tok"))
    }

    @ParameterizedTest
    @CsvSource("403", "404", "429", "500", "502", "503")
    fun `other answers are not a revocation`(status: Int) {
        serve("/api/app/status", status, """{"error":{"code":"x"}}""")
        assertEquals(StatusResult.Unreachable, api.status("tok"))
    }

    @Test
    fun `status with a wrong body is not a revocation`() {
        serve("/api/app/status", 200, """{"status":"blocked","expires_at":"2027-01-11T10:00:00Z","user":$user,"device":$deviceJson}""")
        assertEquals(StatusResult.Unreachable, api.status("tok"))
    }

    @Test
    fun `redirects are not followed and the token stays here`() {
        val other = HttpServer.create(InetSocketAddress(InetAddress.getLoopbackAddress(), 0), 0).apply { start() }
        val hits = CopyOnWriteArrayList<String?>()
        other.createContext("/") { exchange ->
            hits += exchange.requestHeaders.getFirst("Authorization")
            exchange.sendResponseHeaders(200, -1)
            exchange.close()
        }
        try {
            serve("/api/app/status", 302, headers = mapOf("Location" to "http://127.0.0.1:${other.address.port}/steal"))
            assertEquals(StatusResult.Unreachable, api.status("tok"))
            assertEquals(emptyList(), hits)
        } finally {
            other.stop(0)
        }
    }

    @Test
    fun `too large answer is not read`() {
        serve("/api/app/status", 200, """{"status":"ok","pad":"${"a".repeat(5000)}"}""")
        assertEquals(StatusResult.Unreachable, AccountApi(base, maxBytes = 4096).status("tok"))
    }

    @Test
    fun `logout posts with the token`() {
        serve("/api/app/logout", 204)
        assertTrue(api.logout("tok-secret-1"))
        val request = requests.single()
        assertEquals("POST", request.method)
        assertEquals("Bearer tok-secret-1", request.authorization)
    }

    @Test
    fun `only https leaves the computer`() {
        assertFailsWith<IllegalArgumentException> { AccountApi(URI("http://private-gateway.duckdns.org")) }
        assertFailsWith<IllegalArgumentException> { AccountApi(URI("http://192.0.2.1")) }
        assertFailsWith<IllegalArgumentException> { AccountApi(URI("ftp://127.0.0.1")) }
        assertFailsWith<IllegalArgumentException> { AccountApi(URI("http://127.example.com")) }
        assertTrue(AccountApi.secure(AccountApi.SITE))
        assertTrue(AccountApi.secure(URI("http://localhost:8080")))
        assertTrue(AccountApi.secure(URI("http://[::1]:8080")))
        assertEquals("https://private-gateway.duckdns.org/devices", AccountApi.page(AccountApi.DEVICES_PAGE).toString())
        assertEquals("https://private-gateway.duckdns.org/login", AccountApi.page(AccountApi.LOGIN_PAGE).toString())
    }

    @Test
    fun `site time with and without offset`() {
        assertEquals(Instant.parse("2026-12-12T10:00:00Z"), AccountApi.instant("2026-12-12T10:00:00Z"))
        assertEquals(Instant.parse("2026-12-12T07:00:00Z"), AccountApi.instant("2026-12-12T10:00:00+03:00"))
        assertEquals(Instant.parse("2026-12-12T10:00:00.5Z"), AccountApi.instant("2026-12-12T10:00:00.500000"))
        assertNull(AccountApi.instant("12.12.2026"))
    }

    companion object {
        @JvmStatic
        fun unreadableErrors(): List<Arguments> = listOf(
            Arguments.of(401, "", AccountError.Invalid),
            Arguments.of(401, "<html>code_required</html>", AccountError.Invalid),
            Arguments.of(401, """{"code":"code_required"}""", AccountError.Invalid),
            Arguments.of(401, """{"error":"code_required"}""", AccountError.Invalid),
            Arguments.of(401, """{"error":{"code":7}}""", AccountError.Invalid),
            Arguments.of(401, """["code_required"]""", AccountError.Invalid),
            Arguments.of(401, """{"error":{"code":"code_required","pad":"${"x".repeat(AccountApi.MAX_BYTES)}"}}""", AccountError.Invalid),
            Arguments.of(409, "", AccountError.DeviceLimit),
            Arguments.of(409, """{"error":{"code":null}}""", AccountError.DeviceLimit),
            Arguments.of(409, """{"error":{"code":"DEVICE_CHANGE_LIMIT"}}""", AccountError.DeviceLimit),
        )
    }

    @Test
    fun `phone name for the site`() {
        assertEquals("Google Pixel 8", DeviceInfo.phoneName("Google", "Pixel 8"))
        assertEquals("samsung SM-S911B", DeviceInfo.phoneName("samsung", "SM-S911B"))
        assertEquals("Google Pixel 8", DeviceInfo.phoneName("Google", "Google Pixel 8"), "производитель не повторяется")
        assertEquals("Xiaomi 13", DeviceInfo.phoneName(" Xiaomi\u0000 ", " 13  "))
        assertEquals("Android", DeviceInfo.phoneName(null, " "))
        assertEquals(DeviceInfo.NAME_MAX, DeviceInfo.phoneName("A", "b".repeat(300)).length)
        assertTrue(DeviceInfo.validUid("11111111-2222-3333-4444-555555555555"))
        assertFalse(DeviceInfo.validUid("short"))
    }
}

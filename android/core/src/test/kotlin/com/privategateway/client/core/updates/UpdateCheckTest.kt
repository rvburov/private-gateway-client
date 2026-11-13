package com.privategateway.client.core.updates

import com.privategateway.client.core.account.UpdatesFeed
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.security.KeyPair
import java.security.KeyPairGenerator
import java.security.Signature
import java.security.spec.ECGenParameterSpec
import java.util.Base64
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

/** Проверка обновления Android по ленте сайта: подписанный update.json байт в байт, APK по билету сайта, как у службы десктопа. */
class UpdateCheckTest {
    private val key = keyPair()
    private val pem = pem(key)
    private val files = UpdateCheck.FILES
    private val sha = "ab".repeat(32)

    private fun keyPair(): KeyPair = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }.generateKeyPair()

    private fun pem(pair: KeyPair) =
        "-----BEGIN PUBLIC KEY-----\n" + Base64.getMimeEncoder(64, "\n".toByteArray()).encodeToString(pair.public.encoded) + "\n-----END PUBLIC KEY-----\n"

    private fun sign(bytes: ByteArray, pair: KeyPair = key): String =
        Base64.getEncoder().encodeToString(Signature.getInstance("SHA256withECDSA").run { initSign(pair.private); update(bytes); sign() })

    /** update.json как пишет scripts/release.cs; [apk] — строка android-apk в assets (null — APK нет). */
    private fun manifest(
        version: String,
        channel: String = if ('-' in version) "beta" else "stable",
        rollout: Int = 20,
        apk: String? = """{"file":"PrivateGateway-$version-android.apk","sha256":"$sha","size":31457280}""",
        notes: String = "https://private-gateway.duckdns.org/downloads",
    ): ByteArray {
        val assets = listOfNotNull(
            """"win-x64-msi":{"file":"PrivateGateway-$version-win-x64.msi","sha256":"${"cd".repeat(32)}","size":52428800}""",
            apk?.let { """"android-apk":$it""" },
        ).joinToString(",")
        return ("""{"version":"$version","channel":"$channel","rollout":$rollout,"published":"2026-11-13T00:00:00Z","notes":"$notes",""" +
            """"highlights":["Обновления с сайта","Установка из приложения"],"assets":{$assets}}""").toByteArray()
    }

    /** Выпуск ленты сайта: манифест base64, подпись и ссылка-билет на APK. */
    private fun release(
        version: String,
        body: ByteArray = manifest(version),
        signature: String = sign(body),
        url: String? = files + "eyJ2IjoiJHZlcnNpb24ifQ.${"A".repeat(43)}",
    ): String {
        val apk = url?.let { """"android-apk":{"url":"$it","expires_at":"2026-11-13T10:30:00Z"}""" } ?: ""
        return """{"version":"$version","channel":"${if ('-' in version) "beta" else "stable"}",""" +
            """"manifest":"${Base64.getEncoder().encodeToString(body)}","signature":"$signature","files":{$apk}}"""
    }

    private fun feed(vararg releases: String) = """{"releases":[${releases.joinToString(",")}]}"""

    /** Сайт: что спросили (канал) и что ответить. */
    private class Site(val answer: UpdatesFeed) {
        val asked = mutableListOf<String>()

        suspend fun get(channel: String): UpdatesFeed {
            asked += channel
            return answer
        }
    }

    private fun check(installed: String, site: Site, keys: List<String> = listOf(pem), bucket: Int? = null): UpdateResult {
        var result: UpdateResult = UpdateResult.Failed
        runTest { result = UpdateCheck.check(installed, keys, site::get, files, bucket) }
        return result
    }

    private fun check(installed: String, json: String, keys: List<String> = listOf(pem), bucket: Int? = null) =
        check(installed, Site(UpdatesFeed.Ok(json)), keys, bucket)

    private fun version(result: UpdateResult) = (result as UpdateResult.Available).version

    @Test
    fun `newer signed release is offered with its apk from the manifest`() {
        val ticket = files + "eyJ2IjoiMC4zLjAifQ.${"B".repeat(43)}"
        val result = check("0.2.1", feed(release("0.3.0", url = ticket), release("0.2.1")))
        assertEquals(
            UpdateResult.Available(
                "0.3.0", ticket, sha, 31457280, "https://private-gateway.duckdns.org/downloads",
                listOf("Обновления с сайта", "Установка из приложения"),
            ),
            result,
        )
        assertFalse(ticket in result.toString(), "билета нет в toString")
        assertEquals("https://private-gateway.duckdns.org/api/app/updates/files/", UpdateCheck.FILES)
    }

    @Test
    fun `same or older version is up to date`() {
        assertEquals(UpdateResult.UpToDate, check("0.3.0", feed(release("0.3.0"), release("0.2.1"))))
        assertEquals(UpdateResult.UpToDate, check("0.3.0", feed()))
    }

    @Test
    fun `without keys in the app the site is not asked`() {
        val site = Site(UpdatesFeed.Ok(feed(release("0.3.0"))))
        assertEquals(UpdateResult.NotSigned, check("0.2.1", site, keys = emptyList()))
        assertEquals(UpdateResult.NotSigned, check("0.2.1", site, keys = listOf("не ключ")))
        assertTrue(site.asked.isEmpty(), "без ключей — ни одного запроса")
    }

    @Test
    fun `forged signature or tampered manifest is skipped for the previous release`() {
        val forged = release("0.3.0", signature = sign(manifest("0.3.0"), keyPair()))
        assertEquals("0.2.2", version(check("0.2.1", feed(forged, release("0.2.2")))), "подпись чужим ключом")

        // Сайт поменял байт манифеста после подписи (доля раскатки, размер APK) — подпись не сходится.
        val signed = manifest("0.3.0")
        val tampered = String(signed).replace("\"rollout\":20", "\"rollout\":90").toByteArray()
        assertEquals(UpdateResult.UpToDate, check("0.2.1", feed(release("0.3.0", body = tampered, signature = sign(signed)))))
        val bigger = String(signed).replace("31457280", "31457281").toByteArray()
        assertEquals(UpdateResult.UpToDate, check("0.2.1", feed(release("0.3.0", body = bigger, signature = sign(signed)))))
        assertEquals(UpdateResult.UpToDate, check("0.2.1", feed(release("0.3.0", signature = "не base64!"))))
    }

    @Test
    fun `manifest must match its release, channel and rollout`() {
        assertEquals(UpdateResult.UpToDate, check("0.2.1", feed(release("0.3.0", body = manifest("0.3.1")))), "версия манифеста — не та")
        assertEquals(UpdateResult.UpToDate, check("0.2.1", feed(release("0.3.0", body = manifest("0.3.0", rollout = 0)))), "раскатка остановлена")
        assertEquals(UpdateResult.UpToDate, check("0.2.1", feed(release("0.3.0", body = manifest("0.3.0", channel = "beta")))), "бета-манифест у обычного выпуска")
        assertEquals("0.3.0", version(check("0.2.1", feed(release("0.3.0", body = manifest("0.3.0", rollout = 1))))), "по кнопке частичная раскатка видна")
    }

    @Test
    fun `the automatic check follows the rollout by the install number`() {
        val json = feed(release("0.3.0", body = manifest("0.3.0", rollout = 20)), release("0.2.2", body = manifest("0.2.2", rollout = 100)))
        assertEquals("0.3.0", version(check("0.2.1", json, bucket = 19)), "доля 20 % — номера 0–19")
        assertEquals("0.2.2", version(check("0.2.1", json, bucket = 20)), "до этой установки 0.3.0 еще не дошла — предыдущая")
        assertEquals("0.3.0", version(check("0.2.1", json)), "по кнопке — новейшая")
        assertFailsWith<IllegalArgumentException> { runTest { UpdateCheck.check("0.2.1", listOf(pem), { UpdatesFeed.Ok(json) }, files, 100) } }
    }

    @Test
    fun `betas reach only beta installs and the channel follows the install`() {
        val json = feed(release("0.3.0-beta.2"), release("0.2.2"))
        val stable = Site(UpdatesFeed.Ok(json))
        assertEquals("0.2.2", version(check("0.2.1", stable)), "обычная версия бет не видит")
        assertEquals(listOf("stable"), stable.asked)
        val beta = Site(UpdatesFeed.Ok(json))
        assertEquals("0.3.0-beta.2", version(check("0.3.0-beta.1", beta)))
        assertEquals(listOf("beta"), beta.asked)
        assertEquals("0.3.0", version(check("0.3.0-beta.2", feed(release("0.3.0")))), "с беты — на выпуск")
    }

    @Test
    fun `release without an apk in its manifest is not offered`() {
        // Манифесты до 0.3.0 APK не содержат; телефону ставить нечего — и предыдущий выпуск тоже не подходит.
        assertEquals(UpdateResult.UpToDate, check("0.2.1", feed(release("0.3.0", body = manifest("0.3.0", apk = null)))))
        assertEquals("0.2.2", version(check("0.2.1", feed(release("0.3.0", body = manifest("0.3.0", apk = null)), release("0.2.2")))))
        val badSha = manifest("0.3.0", apk = """{"file":"a.apk","sha256":"${"AB".repeat(32)}","size":10}""")
        assertEquals(UpdateResult.UpToDate, check("0.2.1", feed(release("0.3.0", body = badSha))), "SHA-256 не строчными hex")
        val noSize = manifest("0.3.0", apk = """{"file":"a.apk","sha256":"$sha","size":0}""")
        assertEquals(UpdateResult.UpToDate, check("0.2.1", feed(release("0.3.0", body = noSize))))
        assertEquals(UpdateResult.UpToDate, check("0.2.1", feed(release("0.3.0", url = null))), "сайт не дал ссылку на APK")
    }

    @Test
    fun `apk link is only a ticket of the site`() {
        val ticket = "eyJ2IjoiMC4zLjAifQ.${"C".repeat(43)}"
        listOf(
            "https://example.com/api/app/updates/files/$ticket",
            "http://private-gateway.duckdns.org/api/app/updates/files/$ticket",
            "https://private-gateway.duckdns.org.example.com/api/app/updates/files/$ticket",
            "https://private-gateway.duckdns.org/api/app/updates/other/$ticket",
            "https://private-gateway.duckdns.org/downloads/$ticket",
            files + "../../evil/$ticket",
            "$files$ticket?redirect=https://example.com",
            files,
        ).forEach { url ->
            assertEquals(UpdateResult.UpToDate, check("0.2.1", feed(release("0.3.0", url = url))), url)
        }
    }

    @Test
    fun `site answers without a feed`() {
        assertEquals(UpdateResult.SignedOut, check("0.2.1", Site(UpdatesFeed.SignedOut)))
        assertEquals(UpdateResult.SignedOut, check("0.2.1", Site(UpdatesFeed.Revoked)))
        assertEquals(UpdateResult.Failed, check("0.2.1", Site(UpdatesFeed.Unreachable)))
        assertEquals(UpdateResult.Failed, check("0.2.1", "<html>не JSON</html>"))
        assertEquals(UpdateResult.Failed, check("0.2.1", "[]"))
        assertEquals(UpdateResult.Failed, check("0.2.1", """{"releases":{}}"""))
        assertEquals(UpdateResult.Failed, check("не версия", feed(release("0.3.0"))))
    }

    @Test
    fun `odd entries of the feed are skipped`() {
        val odd = listOf(
            "7",
            """{"version":"latest","manifest":"e30=","signature":"c2ln"}""",
            """{"version":"0.4.0","manifest":"не base64!","signature":"c2ln"}""",
            """{"version":"0.4.0","signature":"c2ln"}""",
            """{"version":"0.4.0","manifest":"e30=","signature":"${"s".repeat(2000)}"}""",
        )
        assertEquals("0.3.0", version(check("0.2.1", feed(*odd.toTypedArray(), release("0.3.0")))))
        assertEquals(1, UpdateCheck.releases(feed(*odd.toTypedArray(), release("0.3.0")))!!.size)
    }

    @Test
    fun `notes outside https are dropped, highlights are trimmed`() {
        val body = manifest("0.3.0", notes = "http://private-gateway.duckdns.org/downloads")
        assertNull((check("0.2.1", feed(release("0.3.0", body = body))) as UpdateResult.Available).notes)
        val many = String(manifest("0.3.0")).replace(
            """"highlights":["Обновления с сайта","Установка из приложения"]""",
            """"highlights":[${(1..7).joinToString(",") { "\"${"x".repeat(400)}\"" }},7]""",
        ).toByteArray()
        val highlights = (check("0.2.1", feed(release("0.3.0", body = many))) as UpdateResult.Available).highlights
        assertEquals(5, highlights.size)
        assertTrue(highlights.all { it.length == 300 })
    }

    @Test
    fun `semver orders releases and betas`() {
        val order = listOf("0.1.0-alpha", "0.1.0-beta.1", "0.1.0-beta.2", "0.1.0-beta.11", "0.1.0-rc.1", "0.1.0", "0.1.1", "0.10.0", "1.0.0")
        val parsed = order.map { SemVer.parse(it)!! }
        assertEquals(parsed, parsed.shuffled(java.util.Random(7)).sorted())
        assertNull(SemVer.parse("1.2"))
        assertNull(SemVer.parse("01.2.3"))
        assertNull(SemVer.parse("v1.2.3"))
    }
}

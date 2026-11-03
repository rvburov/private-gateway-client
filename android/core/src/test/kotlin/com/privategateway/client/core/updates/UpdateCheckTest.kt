package com.privategateway.client.core.updates

import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.security.KeyPair
import java.security.KeyPairGenerator
import java.security.Signature
import java.security.spec.ECGenParameterSpec
import java.util.Base64
import kotlin.test.assertEquals
import kotlin.test.assertNull
import kotlin.test.assertTrue

/** «Проверить» обновление Android (DECISIONS.md, п. 8): подписанный update.json выпуска на GitHub, как у службы десктопа. */
class UpdateCheckTest {
    private val key = keyPair()
    private val pem = pem(key)
    private val downloads = UpdateCheck.DOWNLOADS

    private fun keyPair(): KeyPair = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }.generateKeyPair()

    private fun pem(pair: KeyPair) =
        "-----BEGIN PUBLIC KEY-----\n" + Base64.getMimeEncoder(64, "\n".toByteArray()).encodeToString(pair.public.encoded) + "\n-----END PUBLIC KEY-----\n"

    private fun sign(bytes: ByteArray, pair: KeyPair = key): ByteArray =
        Base64.getEncoder().encode(Signature.getInstance("SHA256withECDSA").run { initSign(pair.private); update(bytes); sign() })

    private fun manifest(version: String, channel: String = if ('-' in version) "beta" else "stable", rollout: Int = 20) =
        """{"version":"$version","channel":"$channel","rollout":$rollout,"published":"2026-11-03T00:00:00Z","notes":"https://github.com/rvburov/private-gateway-client/releases/tag/v$version","highlights":[],"assets":{}}""".toByteArray()

    private fun release(tag: String, prerelease: Boolean = '-' in tag, draft: Boolean = false, assets: Boolean = true, host: String = downloads) =
        """{"tag_name":"$tag","draft":$draft,"prerelease":$prerelease,"assets":[""" +
            (if (assets) """{"name":"update.json","browser_download_url":"$host$tag/update.json"},{"name":"update.json.sig","browser_download_url":"$host$tag/update.json.sig"}""" else "") +
            "]}"

    /** Сеть: список выпусков и файлы по адресам; чего нет — null. */
    private class Net(val files: Map<String, ByteArray>) {
        val asked = mutableListOf<String>()

        suspend fun get(url: String): ByteArray? {
            asked += url
            return files[url]
        }
    }

    private fun net(vararg releases: Pair<String, ByteArray?>, signatures: Map<String, ByteArray> = emptyMap()): Net {
        val files = mutableMapOf<String, ByteArray>()
        files[UpdateCheck.RELEASES] = ("[" + releases.joinToString(",") { release(it.first) } + "]").toByteArray()
        for ((tag, body) in releases) {
            body ?: continue
            files["$downloads$tag/update.json"] = body
            files["$downloads$tag/update.json.sig"] = signatures[tag] ?: sign(body)
        }
        return Net(files)
    }

    private fun check(installed: String, net: Net, keys: List<String> = listOf(pem)): UpdateResult {
        var result: UpdateResult = UpdateResult.Failed
        runTest { result = UpdateCheck.check(installed, keys, net::get) }
        return result
    }

    @Test
    fun `newer signed release is offered with its page`() {
        val net = net("v0.2.0" to manifest("0.2.0"), "v0.1.0" to manifest("0.1.0"))
        assertEquals(UpdateResult.Available("0.2.0", "https://github.com/rvburov/private-gateway-client/releases/tag/v0.2.0"), check("0.1.0", net))
    }

    @Test
    fun `same or older version is up to date`() {
        assertEquals(UpdateResult.UpToDate, check("0.2.0", net("v0.2.0" to manifest("0.2.0"), "v0.1.0" to manifest("0.1.0"))))
    }

    @Test
    fun `without keys in the app nothing is asked`() {
        val net = net("v0.2.0" to manifest("0.2.0"))
        assertEquals(UpdateResult.NotSigned, check("0.1.0", net, keys = emptyList()))
        assertTrue(net.asked.isEmpty(), "без ключей — ни одного запроса")
        assertEquals(UpdateResult.NotSigned, check("0.1.0", net, keys = listOf("не ключ")))
    }

    @Test
    fun `forged or foreign signature is skipped for the previous release`() {
        val forged = net("v0.3.0" to manifest("0.3.0"), "v0.2.0" to manifest("0.2.0"), signatures = mapOf("v0.3.0" to sign(manifest("0.3.0"), keyPair())))
        assertEquals("0.2.0", (check("0.1.0", forged) as UpdateResult.Available).version)
        val changed = net("v0.2.0" to manifest("0.2.0"), signatures = mapOf("v0.2.0" to sign(manifest("0.2.1"))))
        assertEquals(UpdateResult.UpToDate, check("0.1.0", changed), "манифест изменен после подписи")
    }

    @Test
    fun `manifest must match its release, channel and rollout`() {
        assertEquals(UpdateResult.UpToDate, check("0.1.0", net("v0.2.0" to manifest("0.3.0"))), "версия манифеста — не тег")
        assertEquals(UpdateResult.UpToDate, check("0.1.0", net("v0.2.0" to manifest("0.2.0", rollout = 0))), "раскатка остановлена")
        assertEquals(UpdateResult.UpToDate, check("0.1.0", net("v0.2.0" to manifest("0.2.0", channel = "beta"))), "бета-манифест у обычного выпуска")
        assertEquals("0.2.0", (check("0.1.0", net("v0.2.0" to manifest("0.2.0", rollout = 1))) as UpdateResult.Available).version, "частичная раскатка — по кнопке видна")
    }

    @Test
    fun `betas reach only beta installs`() {
        val net = net("v0.2.0-beta.2" to manifest("0.2.0-beta.2"), "v0.1.0" to manifest("0.1.0"))
        assertEquals(UpdateResult.UpToDate, check("0.1.0", net), "обычная версия бет не видит")
        assertEquals("0.2.0-beta.2", (check("0.1.0-beta.1", net) as UpdateResult.Available).version)
        assertEquals("0.2.0", (check("0.2.0-beta.2", net("v0.2.0" to manifest("0.2.0"))) as UpdateResult.Available).version, "с беты — на выпуск")
    }

    @Test
    fun `links outside this repository's downloads are ignored`() {
        val tag = "v0.2.0"
        val body = manifest("0.2.0")
        val evil = "https://example.com/releases/download/"
        val files = mapOf(
            UpdateCheck.RELEASES to "[${release(tag, host = evil)}]".toByteArray(),
            "$evil$tag/update.json" to body,
            "$evil$tag/update.json.sig" to sign(body),
        )
        val net = Net(files)
        assertEquals(UpdateResult.UpToDate, check("0.1.0", net))
        assertEquals(listOf(UpdateCheck.RELEASES), net.asked, "чужие ссылки не загружаются")
    }

    @Test
    fun `network failure is a failed check, drafts and odd tags are skipped`() {
        assertEquals(UpdateResult.Failed, check("0.1.0", Net(emptyMap())))
        val broken = Net(mapOf(UpdateCheck.RELEASES to "{}".toByteArray()))
        assertEquals(UpdateResult.Failed, check("0.1.0", broken))
        val listing = "[${release("v0.3.0", draft = true)},${release("latest")},${release("v0.2.0", assets = false)}]"
        assertEquals(UpdateResult.UpToDate, check("0.1.0", Net(mapOf(UpdateCheck.RELEASES to listing.toByteArray()))))
        val noManifest = net("v0.2.0" to manifest("0.2.0")).files.filterKeys { !it.endsWith("update.json.sig") }
        assertEquals(UpdateResult.Failed, check("0.1.0", Net(noManifest)), "манифест есть в списке, но не загрузился")
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

package com.privategateway.client.core.subscriptions

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.jupiter.api.DynamicTest
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.TestFactory
import java.io.File
import java.net.URI
import kotlin.test.assertEquals
import kotlin.test.assertIs
import kotlin.test.assertTrue

/**
 * Загрузка и разбор подписки при обновлении — случаи десктопа (`SubscriptionUpdateTests`: неудача с кодом ответа, ответ
 * без серверов, битая строка пропускается) на подделке вместо сети и на общих эталонах shared/testdata.
 */
class SubscriptionFetchTest {
    private val url = URI("https://sub.example.com/sub/fake-token-1")
    private val testData = File(System.getProperty("testdata") ?: error("Нет пути к shared/testdata"))

    /** Подделка сети: отдает текст или бросает ошибку загрузки и запоминает, что спросили. */
    private class FakeSource(var text: String = "", var title: String? = null, var failure: SubscriptionDownloadException? = null) : SubscriptionSource {
        val requested = ArrayList<URI>()

        override fun download(url: URI): SubscriptionContent {
            requested.add(url)
            failure?.let { throw it }
            return SubscriptionContent(text, title)
        }
    }

    @TestFactory
    fun `every shared subscription gives the servers that parsed`() = testData.resolve("subscriptions").walkTopDown()
        .filter { it.name.endsWith(".expected.json") }.sortedBy { it.path }.map { expectedFile ->
            val fixture = expectedFile.path.removeSuffix(".expected.json")
            DynamicTest.dynamicTest(expectedFile.relativeTo(testData).path) {
                val expected = Json.parseToJsonElement(expectedFile.readText()).jsonObject["lines"]!!.jsonArray.map { it.jsonObject }
                    .filter { "error" !in it }
                    .map { (it["name"] ?: it["server"]!!.jsonObject["name"])!!.jsonPrimitive.content }

                val result = SubscriptionFetch.fetch(url, FakeSource(File("$fixture.txt").readText()))

                assertTrue(expected.isNotEmpty(), fixture)
                assertEquals(expected, assertIs<FetchResult.Fetched>(result, fixture).servers.map { it.name }, fixture)
            }
        }.toList()

    @Test
    fun `lines that do not parse are skipped and the title comes through`() {
        val source = FakeSource("trojan://pw-a@198.51.100.1:443#A\ntrojan://pw-b@198.51.100.2:443#B\nbroken", title = "Ноутбук")

        val result = assertIs<FetchResult.Fetched>(SubscriptionFetch.fetch(url, source))

        assertEquals(listOf("A", "B"), result.servers.map { it.name })
        assertEquals("Ноутбук", result.title)
        assertEquals(listOf(url), source.requested)
    }

    @Test
    fun `answer without servers keeps the previous list`() {
        assertEquals(FetchResult.Failed(DownloadFailure.NoServers), SubscriptionFetch.fetch(url, FakeSource("<html>не подписка</html>")))
        assertEquals(FetchResult.Failed(DownloadFailure.NoServers), SubscriptionFetch.fetch(url, FakeSource("vless://broken@203.0.113.24:abc\n")))
        assertEquals(FetchResult.Failed(DownloadFailure.NoServers), SubscriptionFetch.fetch(url, FakeSource("")))
    }

    @Test
    fun `download failure keeps the reason and the status code`() {
        val forbidden = FakeSource(failure = SubscriptionDownloadException(DownloadFailure.Status, 403))
        assertEquals(FetchResult.Failed(DownloadFailure.Status, 403), SubscriptionFetch.fetch(url, forbidden))

        val timeout = FakeSource(failure = SubscriptionDownloadException(DownloadFailure.Timeout))
        assertEquals(FetchResult.Failed(DownloadFailure.Timeout, null), SubscriptionFetch.fetch(url, timeout))
    }

    @Test
    fun `suggested name is the profile title or the subscription host`() {
        assertEquals("Ноутбук", SubscriptionFetch.suggestName("Ноутбук", url))
        assertEquals("sub.example.com", SubscriptionFetch.suggestName(null, url))
        assertEquals("203.0.113.5", SubscriptionFetch.suggestName(null, URI("http://203.0.113.5:2096/sub/fake-id")))
    }
}

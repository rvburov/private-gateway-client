package com.privategateway.client.core.settings

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.jupiter.api.Test
import org.junit.jupiter.params.ParameterizedTest
import org.junit.jupiter.params.provider.CsvSource
import java.io.File
import kotlin.test.assertEquals
import kotlin.test.assertNull

/**
 * «Все адреса сервиса» (решение 741): список сервисов — ресурс модуля из shared/specifications/routing/services.json, как у
 * десктопа и iOS; из «geosite:» телефон берет только сервисы списка, ядро получает их домены.
 */
class ServiceSitesTest {
    @ParameterizedTest
    @CsvSource(
        "youtube.com, youtube",
        "m.youtube.com, youtube",
        "googlevideo.com, youtube",
        "t.me, telegram",
        "chatgpt.com, openai",
        "example.com, ''",
        "notyoutube.com, ''",
        "*.youtube.com, ''",
        "geosite:youtube, ''",
        "203.0.113.9, ''",
    )
    fun `plain domain finds its service`(pattern: String, id: String) {
        assertEquals(id.ifEmpty { null }, ServiceSites.find(pattern)?.id, pattern)
    }

    @Test
    fun `rule of a service is its geosite category and the core gets its domains`() {
        val youtube = ServiceSites.ofPattern("geosite:youtube") ?: error("нет YouTube")
        assertEquals("YouTube" to "geosite:youtube", youtube.name to youtube.pattern)
        assertNull(ServiceSites.ofPattern("geosite:category-ads-all"), "не из списка сервисов")
        assertEquals("geosite:youtube", SitePattern.normalize(" GEOSITE:YouTube "))
        assertNull(SitePattern.normalize("geosite:google"), "базы geosite у телефона нет")
        assertEquals(2 to 0, SitePattern.rank(SiteRule("geosite:youtube", RuleAction.Block)), "после доменов, до адресов — как у десктопа")
        assertEquals(youtube.domains.size + youtube.full.size, youtube.conditions.size)
        assertEquals("domain:googlevideo.com", youtube.conditions.first { it.endsWith("googlevideo.com") })
        assertEquals("full:yt3.googleusercontent.com", youtube.conditions.first { it.endsWith("yt3.googleusercontent.com") })
    }

    @Test
    fun `list is the shared one`() {
        val file = File(System.getProperty("testdata") ?: error("Нет пути к shared/testdata")).resolve("../specifications/routing/services.json")
        val shared = Json.parseToJsonElement(file.readText()).jsonObject["services"]!!.jsonArray.map { it.jsonObject }
        assertEquals(
            shared.map { s ->
                ServiceSite(
                    s["id"]!!.jsonPrimitive.content,
                    s["name"]!!.jsonPrimitive.content,
                    s["domains"]!!.jsonArray.map { it.jsonPrimitive.content },
                    s["full"]!!.jsonArray.map { it.jsonPrimitive.content },
                )
            },
            ServiceSites.all,
        )
    }
}

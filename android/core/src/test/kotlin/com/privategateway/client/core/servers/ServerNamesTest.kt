package com.privategateway.client.core.servers

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.jupiter.api.Test
import org.junit.jupiter.params.ParameterizedTest
import org.junit.jupiter.params.provider.ValueSource
import java.io.File
import kotlin.test.assertEquals
import kotlin.test.assertNull

// Правило карточки десктопа: ServerCardTests.Rename_changes_the_name_in_card_and_list_and_rejects_empty и
// ServerCatalogStorageTests.Changes_survive_a_restart («  Мой сервер » сохраняется без пробелов).
class ServerNamesTest {
    @Test
    fun `display spacing preserves emoji sequences and original names on shared fixtures`() {
        val fixtures = File(System.getProperty("testdata"), "server-name-display.json")
        for (fixture in Json.parseToJsonElement(fixtures.readText()).jsonArray) {
            val input = fixture.jsonObject.getValue("input").jsonPrimitive.content
            val expected = fixture.jsonObject.getValue("expected").jsonPrimitive.content
            assertEquals(expected, ServerNames.forDisplay(input), input)
            assertEquals(expected, ServerNames.forDisplay(expected), "повтор не добавляет пробелы")
            val server = ServerItem("id", input, "", ProxyProtocol.Vless, TransportSecurity.Reality, TransportKind.Tcp, 443)
            assertEquals(expected, server.title)
            assertEquals(input, server.displayName, "редактор и экспорт видят исходное имя")
        }
    }

    @Test
    fun `name is saved without spaces around`() {
        assertEquals("Варшава, работа", ServerNames.validate("  Варшава, работа "))
        assertEquals("Мой сервер", ServerNames.validate("\tМой сервер\n"))
    }

    @ParameterizedTest
    @ValueSource(strings = ["", "   ", " "])
    fun `empty name is refused`(name: String) {
        assertNull(ServerNames.validate(name))
    }

    @Test
    fun `name is at most 64 characters after trimming`() {
        val longest = "Н".repeat(64)

        assertEquals(64, ServerNames.MAX_LENGTH)
        assertEquals(longest, ServerNames.validate(longest))
        assertEquals(longest, ServerNames.validate("  $longest  "), "пробелы по краям в длину не идут")
        assertNull(ServerNames.validate(longest + "1"))
        assertEquals("Н", ServerNames.validate("Н"))
    }
}

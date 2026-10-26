package com.privategateway.client.core.servers

import org.junit.jupiter.api.Test
import org.junit.jupiter.params.ParameterizedTest
import org.junit.jupiter.params.provider.ValueSource
import kotlin.test.assertEquals
import kotlin.test.assertNull

// Правило карточки десктопа: ServerCardTests.Rename_changes_the_name_in_card_and_list_and_rejects_empty и
// ServerCatalogStorageTests.Changes_survive_a_restart («  Мой сервер » сохраняется без пробелов).
class ServerNamesTest {
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

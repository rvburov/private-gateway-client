package com.privategateway.client.data

import androidx.datastore.preferences.core.PreferenceDataStoreFactory
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import kotlin.test.assertEquals
import kotlin.test.assertNull

class SettingsRepositoryTest {
    @get:Rule
    val folder = TemporaryFolder()

    private val scope = CoroutineScope(Dispatchers.IO + SupervisorJob())

    @After
    fun stop() = scope.cancel()

    @Test
    fun `selected server survives a restart and can be cleared`() = runBlocking {
        val file = folder.root.resolve("settings.preferences_pb")
        val first = SettingsRepository(PreferenceDataStoreFactory.create(scope = scope) { file })
        assertNull(first.selectedServerId.first())
        first.select("nl")
        assertEquals("nl", first.selectedServerId.first())
        first.select(null)
        assertNull(first.selectedServerId.first())
        first.select("fi")
        scope.cancel()
        scope.coroutineContext[Job]?.join()

        // Новый процесс — новый DataStore на том же файле.
        val again = CoroutineScope(Dispatchers.IO + SupervisorJob())
        try {
            assertEquals("fi", SettingsRepository(PreferenceDataStoreFactory.create(scope = again) { file }).selectedServerId.first())
        } finally {
            again.cancel()
        }
    }
}

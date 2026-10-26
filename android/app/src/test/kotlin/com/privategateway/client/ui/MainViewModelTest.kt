package com.privategateway.client.ui

import android.content.Context
import androidx.datastore.preferences.core.PreferenceDataStoreFactory
import androidx.room.Room
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.privategateway.client.FakeBox
import com.privategateway.client.Fixtures
import com.privategateway.client.data.AppDatabase
import com.privategateway.client.data.ServerRepository
import com.privategateway.client.data.SettingsRepository
import com.privategateway.client.vpn.CoreEngine
import com.privategateway.client.vpn.TunnelMonitor
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.test.UnconfinedTestDispatcher
import kotlinx.coroutines.test.resetMain
import kotlinx.coroutines.test.setMain
import kotlinx.coroutines.withTimeout
import org.junit.After
import org.junit.Before
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import org.junit.runner.RunWith
import kotlin.test.assertEquals
import kotlin.test.assertNull

@OptIn(ExperimentalCoroutinesApi::class)
@RunWith(AndroidJUnit4::class)
class MainViewModelTest {
    @get:Rule
    val folder = TemporaryFolder()

    private val context = ApplicationProvider.getApplicationContext<Context>()
    private val database = Room.inMemoryDatabaseBuilder(context, AppDatabase::class.java).build()
    private val scope = CoroutineScope(Dispatchers.IO + SupervisorJob())
    private lateinit var settings: SettingsRepository
    private lateinit var model: MainViewModel

    private class FakeEngine : CoreEngine {
        override fun start(configJson: String, protect: (Int) -> Boolean, dns: String): String? = null

        override fun stop() = Unit

        override fun version() = "26.10.21"
    }

    @Before
    fun create() {
        Dispatchers.setMain(UnconfinedTestDispatcher())
        settings = SettingsRepository(PreferenceDataStoreFactory.create(scope = scope) { folder.root.resolve("settings.preferences_pb") })
        model = MainViewModel(TunnelMonitor(), ServerRepository(database.servers(), FakeBox()), settings, FakeEngine())
    }

    @After
    fun close() {
        scope.cancel()
        database.close()
        Dispatchers.resetMain()
    }

    private fun <T> wait(block: suspend () -> T): T = runBlocking { withTimeout(5_000) { block() } }

    @Test
    fun `first import selects the first new server and a later one keeps the choice`() {
        val links = Fixtures.servers().distinctBy { Fixtures.identity(it) }.take(3)
        val text = links.joinToString("\n") { com.privategateway.client.core.links.LinkWriter.write(it) }

        model.preview(text)
        wait { model.importing.first { it.batch != null } }
        assertEquals(3, model.importing.value.batch?.found)
        model.add()
        wait { model.importing.first { it.added != null } }
        assertEquals(3, model.importing.value.added)
        val ui = wait { model.ui.first { it.servers.size == 3 && it.selectedId != null } }
        assertEquals(ui.servers.first().id, ui.selectedId)
        assertEquals(links.first().name, ui.inUse?.name)

        // Второй импорт: выбор не меняется — сервер меняется только нажатием пользователя.
        model.closeImport()
        model.preview(com.privategateway.client.core.links.LinkWriter.write(Fixtures.servers().distinctBy { Fixtures.identity(it) }[3]))
        wait { model.importing.first { it.batch != null } }
        model.add()
        wait { model.importing.first { it.added != null } }
        assertEquals(ui.selectedId, wait { model.ui.first { it.servers.size == 4 } }.selectedId)
    }

    @Test
    fun `duplicates are counted and not added`() {
        val link = com.privategateway.client.core.links.LinkWriter.write(Fixtures.servers().first())
        model.preview(link)
        wait { model.importing.first { it.batch != null } }
        model.add()
        wait { model.importing.first { it.added != null } }
        model.closeImport()

        model.preview(link)
        wait { model.importing.first { it.batch != null } }
        model.add()
        val result = wait { model.importing.first { it.added != null } }
        assertEquals(0, result.added)
        assertEquals(1, result.duplicates)
    }

    @Test
    fun `deleting the selected server clears the choice`() {
        model.preview(com.privategateway.client.core.links.LinkWriter.write(Fixtures.servers().first()))
        wait { model.importing.first { it.batch != null } }
        model.add()
        val id = wait { model.ui.first { it.selectedId != null } }.selectedId!!
        model.delete(id)
        val ui = wait { model.ui.first { it.servers.isEmpty() && it.selectedId == null } }
        assertNull(ui.inUse)
    }

    @Test
    fun `select changes the choice and core version loads once`() {
        model.select("nl")
        assertEquals("nl", wait { settings.selectedServerId.first { it == "nl" } })
        model.loadCoreVersion()
        assertEquals("26.10.21", wait { model.core.first { it != null } })
    }
}

package com.privategateway.client.data

import android.content.Context
import android.database.Cursor
import androidx.room.Room
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.privategateway.client.FakeBox
import com.privategateway.client.Fixtures
import com.privategateway.client.core.subscriptions.DownloadFailure
import com.privategateway.client.core.subscriptions.SelectedChange
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Test
import org.junit.runner.RunWith
import java.net.URI
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/** Группы и подписки в хранилище: адрес подписки только запечатан, обновление — как ApplyUpdate десктопа. */
@RunWith(AndroidJUnit4::class)
class GroupRepositoryTest {
    private val context = ApplicationProvider.getApplicationContext<Context>()
    private val database = Room.inMemoryDatabaseBuilder(context, AppDatabase::class.java).allowMainThreadQueries().build()
    private val repository = ServerRepository(database, FakeBox())
    private val servers = Fixtures.servers().distinctBy { Fixtures.identity(it) }
    private val token = "0123456789abcdef0123456789abcdef"
    private val url = URI("https://example.com/sub/$token")

    @After
    fun close() = database.close()

    @Test
    fun `subscription keeps its address only sealed`() = runBlocking {
        assertEquals(AddResult(3, 0), repository.add(servers.take(3), " Провайдер ", url))
        val group = repository.groups.first().single()
        assertEquals("Провайдер", group.name)
        assertEquals(3, group.servers)
        assertEquals("https://example.com/sub/••••", group.maskedUrl)
        assertTrue(group.canUpdate)
        assertNotNull(group.updatedAt, "подписка по адресу только что загружена")
        assertEquals(url, repository.subscriptionUrl(group.id))
        assertEquals(0, repository.manualCount.first())

        // Токена подписки нет открытым текстом ни в одной колонке обеих таблиц.
        for (table in listOf("servers", "subscriptions")) {
            database.openHelper.readableDatabase.query("SELECT * FROM $table").use { cursor ->
                while (cursor.moveToNext()) {
                    for (column in 0 until cursor.columnCount) {
                        if (cursor.getType(column) == Cursor.FIELD_TYPE_STRING) {
                            assertFalse(cursor.getString(column).contains(token), "$table.${cursor.getColumnName(column)}")
                        }
                    }
                }
            }
        }
    }

    @Test
    fun `profile without an address does not update and servers without a group are mine`() = runBlocking {
        repository.add(servers.take(2), "Профиль", null)
        repository.add(servers.drop(2).take(1), "   ", url)
        val group = repository.groups.first().single()
        assertNull(group.maskedUrl)
        assertFalse(group.canUpdate)
        assertNull(repository.subscriptionUrl(group.id))
        assertEquals(1, repository.manualCount.first(), "без названия — в «Мои серверы», адрес не хранится")
    }

    @Test
    fun `update keeps own name favorite latency and choice and leaves the selected server marked missing`() = runBlocking {
        val (a, b, c, d) = servers.take(4).mapIndexed { index, server -> server.copy(name = "S$index") }
        repository.add(listOf(a, b, c), "Подписка", url)
        val group = repository.groups.first().single().id
        val (ra, _, rc) = database.servers().inGroup(group)
        database.servers().rename(ra.id, "Мой")
        database.servers().setFavorite(ra.id, true)
        database.servers().setLatency(ra.id, 42, null)
        database.servers().markUsed(ra.id, 100)

        // Пришли a и новый d; b ушел; c ушел, но он выбран — остается с пометкой.
        val result = repository.applyUpdate(group, listOf(a, d), selectedId = rc.id)
        assertEquals(UpdateResult(1, 1, SelectedChange.Missing, "S2"), result)

        val after = database.servers().inGroup(group).associateBy { it.name }
        assertEquals(setOf("S0", "S2", "S3"), after.keys)
        val kept = after.getValue("S0")
        assertEquals(ra.id, kept.id)
        assertEquals("Мой", kept.customName)
        assertTrue(kept.favorite)
        assertEquals(42, kept.latencyMs)
        assertEquals(100L, kept.lastUsedAt)
        assertNull(kept.missingSince)
        assertNotNull(after.getValue("S2").missingSince)
        assertEquals(group, after.getValue("S3").subscriptionId)

        val subscription = repository.groups.first().single()
        assertNull(subscription.failure)
        assertNotNull(subscription.updatedAt)
    }

    @Test
    fun `selected server whose name came back with other keys is changed`() = runBlocking {
        val (a, b, c) = servers.take(3).mapIndexed { index, server -> server.copy(name = "S$index") }
        repository.add(listOf(a, b), "Подписка", url)
        val group = repository.groups.first().single().id
        val selected = database.servers().inGroup(group)[1]
        // Под тем же названием — другой сервер: у поставщика новый адрес или ключи.
        val result = repository.applyUpdate(group, listOf(a, c.copy(name = "S1")), selectedId = selected.id)
        assertEquals(SelectedChange.Changed, result.selected)
        assertEquals("S1", result.selectedName)
    }

    @Test
    fun `server that does not open counts as gone`() = runBlocking {
        repository.add(servers.take(2), "Подписка", url)
        val group = repository.groups.first().single().id
        val broken = database.servers().inGroup(group)[1]
        database.servers().updateAll(listOf(broken.copy(sealed = byteArrayOf(1, 2, 3))))

        assertEquals(1, repository.applyUpdate(group, servers.take(1), selectedId = null).removed)
        assertEquals(1, database.servers().inGroup(group).size)
    }

    @Test
    fun `failure is remembered and a new address clears it`() = runBlocking {
        repository.add(servers.take(1), "Подписка", url)
        val group = repository.groups.first().single().id
        repository.markFailed(group, DownloadFailure.Status, 403)
        val failed = repository.groups.first().single()
        assertEquals(DownloadFailure.Status, failed.failure)
        assertEquals(403, failed.status)
        assertNotNull(failed.lastAttemptAt)

        repository.edit(group, "Новое", null)
        assertEquals(DownloadFailure.Status, repository.groups.first().single().failure, "только название — неудача остается")

        val next = URI("https://example.org/feed/abcdefabcdefabcdefabcdef")
        repository.edit(group, "Новое", next)
        val edited = repository.groups.first().single()
        assertEquals("Новое", edited.name)
        assertNull(edited.failure)
        assertNull(edited.status)
        assertEquals("https://example.org/feed/••••", edited.maskedUrl)
        assertEquals(next, repository.subscriptionUrl(group))
    }

    @Test
    fun `removing a group takes its servers and returns them`() = runBlocking {
        repository.add(servers.take(2), "Подписка", url)
        repository.add(servers.drop(2).take(1))
        val group = repository.groups.first().single().id
        val ids = database.servers().inGroup(group).map { it.id }

        assertEquals(ids, repository.remove(group))
        assertTrue(repository.groups.first().isEmpty())
        assertEquals(1, database.servers().all().size, "«Мои серверы» не тронуты")

        assertEquals(1, repository.remove(null).size)
        assertTrue(database.servers().all().isEmpty())
    }
}

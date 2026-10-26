package com.privategateway.client.data

import android.content.Context
import androidx.room.Room
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.privategateway.client.FakeBox
import com.privategateway.client.Fixtures
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Test
import org.junit.runner.RunWith
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

@RunWith(AndroidJUnit4::class)
class ServerRepositoryTest {
    private val context = ApplicationProvider.getApplicationContext<Context>()
    private val database = Room.inMemoryDatabaseBuilder(context, AppDatabase::class.java).allowMainThreadQueries().build()
    private val box = FakeBox()
    private val repository = ServerRepository(database.servers(), box)

    @After
    fun close() = database.close()

    @Test
    fun `every fixture server comes back as it went in`() = runBlocking {
        val servers = Fixtures.servers()
        assertTrue(servers.size >= 15, "эталонов слишком мало: ${servers.size}")
        // Дубль не добавляется — остается первый с этим отпечатком.
        val unique = servers.distinctBy { Fixtures.identity(it) }.associateBy { Fixtures.identity(it) }

        val result = repository.add(servers)
        assertEquals(unique.size, result.added)
        assertEquals(servers.size - unique.size, result.duplicates)

        val items = repository.servers.first()
        assertEquals(unique.size, items.size)
        for (item in items) {
            val loaded = assertNotNull(repository.load(item.id), item.name)
            assertEquals(unique[Fixtures.identity(loaded)], loaded, item.name)
            assertEquals(loaded.port, item.port)
            assertEquals(loaded.protocol, item.protocol)
        }
    }

    @Test
    fun `address and keys are stored only sealed`() = runBlocking {
        val servers = Fixtures.servers()
        repository.add(servers)
        val entities = database.servers().all()
        assertEquals(entities.size, box.sealed)
        for (entity in entities) {
            val server = assertNotNull(repository.load(entity.id))
            val open = listOf(entity.id, entity.name, entity.countryCode, entity.protocol, entity.security, entity.transport).joinToString("|")
            // Адрес открыт только в названии, если названия в ссылке не было: тогда сервер называется адресом (как на
            // десктопе). Ключи и пароли не открыты никогда.
            val secrets = listOfNotNull(
                server.host.takeIf { it != entity.name },
                server.credentials.uuid,
                server.credentials.password,
                server.credentials.privateKey,
                server.credentials.presharedKey,
            ).filter { it.length >= 4 }
            for (secret in secrets) {
                assertFalse(open.contains(secret), "${entity.name}: «$secret» лежит открыто")
                assertFalse(String(entity.sealed, Charsets.UTF_8).contains(secret), "${entity.name}: запечатанное не прошло через SecretBox")
            }
        }
    }

    @Test
    fun `the same server under another name is a duplicate`() = runBlocking {
        val server = Fixtures.servers().first()
        assertEquals(AddResult(1, 0), repository.add(listOf(server)))
        assertEquals(AddResult(0, 1), repository.add(listOf(server.copy(name = "Другое название"))))
        assertEquals(AddResult(1, 0), repository.add(listOf(server.copy(port = server.port % 65535 + 1))))
    }

    @Test
    fun `a server that does not open is listed but not loaded`() = runBlocking {
        // Ключ Keystore утерян (сброс устройства, восстановление чужой базы): сервер виден, удалить его можно, подключиться — нет.
        database.servers().insertAll(
            listOf(ServerEntity("broken", "Сломанный", "NL", "Vless", "Reality", "Tcp", 443, 0, 0L, byteArrayOf(1, 2, 3))),
        )
        assertEquals(listOf("broken"), repository.servers.first().map { it.id })
        assertNull(repository.load("broken"))
        repository.delete("broken")
        assertTrue(repository.servers.first().isEmpty())
    }

    @Test
    fun `adds at the same time do not add the same servers twice`() = runBlocking {
        val slow = ServerRepository(database.servers(), FakeBox(delayMillis = 20))
        val servers = Fixtures.servers().distinctBy { Fixtures.identity(it) }.take(5)
        val results = (1..3).map { async(Dispatchers.Default) { slow.add(servers) } }.awaitAll()
        assertEquals(servers.size, results.sumOf { it.added })
        assertEquals(2 * servers.size, results.sumOf { it.duplicates })
        val stored = database.servers().all()
        assertEquals(servers.map { it.name }, stored.map { it.name })
        assertEquals(servers.indices.toList(), stored.map { it.position })
    }

    @Test
    fun `new servers go after the old ones`() = runBlocking {
        val servers = Fixtures.servers().distinctBy { Fixtures.identity(it) }
        repository.add(servers.take(2))
        repository.add(servers.drop(2).take(2))
        assertEquals(servers.take(4).map { it.name }, repository.servers.first().map { it.name })
    }
}

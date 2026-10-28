package com.privategateway.client.data

import android.content.Context
import androidx.room.Room
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.privategateway.client.FakeBox
import com.privategateway.client.Fixtures
import com.privategateway.client.core.links.LinkParser
import com.privategateway.client.core.servers.Latency
import com.privategateway.client.core.servers.LatencyFailure
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
    private val repository = ServerRepository(database, box)

    @After
    fun close() = database.close()

    @Test
    fun `every fixture server comes back as it went in`() = runBlocking<Unit> {
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
            val loaded = assertNotNull(repository.load(item.id), item.displayName)
            assertEquals(unique[Fixtures.identity(loaded)], loaded, item.displayName)
            assertEquals(loaded.port, item.port)
            assertEquals(loaded.protocol, item.protocol)
        }
    }

    @Test
    fun `address and keys are stored only sealed`() = runBlocking<Unit> {
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
    fun `the same server under another name is a duplicate`() = runBlocking<Unit> {
        val server = Fixtures.servers().first()
        assertEquals(AddResult(1, 0), repository.add(listOf(server)))
        assertEquals(AddResult(0, 1), repository.add(listOf(server.copy(name = "Другое название"))))
        assertEquals(AddResult(1, 0), repository.add(listOf(server.copy(port = server.port % 65535 + 1))))
    }

    @Test
    fun `the same server imported again takes the pinned certificate and keeps its name`() = runBlocking<Unit> {
        val insecure = assertNotNull(LinkParser.parse("trojan://pw-a@198.51.100.1:443?sni=example.com&allowInsecure=1#Self-signed").server)
        assertEquals(AddResult(1, 0), repository.add(listOf(insecure)))
        val id = repository.servers.first().single().id
        repository.rename(id, "Мой сервер")

        // Тот же сервер с закрепленным сертификатом — дубль, но защита берется из новой ссылки (решение 484).
        val pinned = assertNotNull(LinkParser.parse("trojan://pw-a@198.51.100.1:443?sni=example.com&pcs=a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90#Pinned").server)
        assertEquals(AddResult(0, 1), repository.add(listOf(pinned)))

        val loaded = assertNotNull(repository.load(id))
        assertEquals(listOf("a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90"), loaded.security.pinnedPeerCertSha256)
        assertFalse(loaded.security.allowInsecure)
        assertEquals("Мой сервер", repository.servers.first().single().displayName, "свое название остается")
    }

    @Test
    fun `a server that does not open is listed but not loaded`() = runBlocking<Unit> {
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
    fun `adds at the same time do not add the same servers twice`() = runBlocking<Unit> {
        val slow = ServerRepository(database, FakeBox(delayMillis = 20))
        val servers = Fixtures.servers().distinctBy { Fixtures.identity(it) }.take(5)
        val results = (1..3).map { async(Dispatchers.Default) { slow.add(servers) } }.awaitAll()
        assertEquals(servers.size, results.sumOf { it.added })
        assertEquals(2 * servers.size, results.sumOf { it.duplicates })
        val stored = database.servers().all()
        assertEquals(servers.map { it.name }, stored.map { it.name })
        assertEquals(servers.indices.toList(), stored.map { it.position })
    }

    @Test
    fun `own name is shown in the list while the link keeps its name`() = runBlocking<Unit> {
        val server = Fixtures.servers().first()
        repository.add(listOf(server))
        val stored = database.servers().all().single()
        // Свое название — только для списка; подписка узнает сервер по названию из ссылки, «Поделиться» отдает его же.
        database.servers().delete(stored.id)
        database.servers().insertAll(
            listOf(
                ServerEntity(
                    stored.id, stored.name, stored.countryCode, stored.protocol, stored.security, stored.transport, stored.port,
                    stored.position, stored.addedAt, stored.sealed, customName = "Мой сервер",
                ),
            ),
        )
        assertEquals("Мой сервер", repository.servers.first().single().displayName)
        assertEquals(server.name, repository.servers.first().single().linkName)
        assertEquals(server.name, assertNotNull(repository.load(stored.id)).name)
    }

    @Test
    fun `new servers go after the old ones`() = runBlocking<Unit> {
        val servers = Fixtures.servers().distinctBy { Fixtures.identity(it) }
        repository.add(servers.take(2))
        repository.add(servers.drop(2).take(2))
        assertEquals(servers.take(4).map { it.name }, repository.servers.first().map { it.linkName })
    }

    @Test
    fun `latency is stored per server and never measured stays null`() = runBlocking<Unit> {
        val servers = Fixtures.servers().distinctBy { Fixtures.identity(it) }.take(3)
        repository.add(servers)
        val (fast, silent, never) = repository.servers.first().map { it.id }

        repository.setLatency(mapOf(fast to Latency.of(42), silent to Latency.failed(LatencyFailure.NoResponse)))
        val items = repository.servers.first().associateBy { it.id }
        assertEquals(Latency.of(42), items.getValue(fast).latency)
        assertEquals(Latency.failed(LatencyFailure.NoResponse), items.getValue(silent).latency)
        assertNull(items.getValue(never).latency, "не мерили — не «нет ответа»")

        repository.setLatency(mapOf(silent to Latency.of(7)))
        assertEquals(Latency.of(7), repository.servers.first().single { it.id == silent }.latency, "новый замер снимает причину")
    }

    @Test
    fun `groups and their servers come out for the list`() = runBlocking<Unit> {
        val servers = Fixtures.servers().distinctBy { Fixtures.identity(it) }
        repository.add(servers.take(2), "Провайдер")
        repository.add(servers.drop(2).take(1))
        val group = repository.subscriptionRefs.first().single()
        assertEquals("Провайдер", group.name)
        assertNull(group.updatedAt, "профиль без адреса не обновлялся")
        assertEquals(listOf(group.id, group.id, null), repository.servers.first().map { it.subscriptionId })
    }

    @Test
    fun `card address comes from the sealed link with ipv6 in brackets`() = runBlocking<Unit> {
        val ipv6 = Fixtures.servers().first { ':' in it.host }
        repository.add(listOf(ipv6))
        val id = repository.servers.first().single().id
        assertEquals("[${ipv6.host}]:${ipv6.port}", repository.address(id))
        assertNull(repository.address("gone"))
    }
}

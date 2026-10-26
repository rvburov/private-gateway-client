package com.privategateway.client.data

import com.privategateway.client.core.links.LinkParser
import com.privategateway.client.core.links.LinkWriter
import com.privategateway.client.core.servers.ProxyProtocol
import com.privategateway.client.core.servers.ServerDefinition
import com.privategateway.client.core.servers.ServerFacts
import com.privategateway.client.core.servers.TransportKind
import com.privategateway.client.core.servers.TransportSecurity
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.withContext
import java.util.UUID
import javax.inject.Inject
import javax.inject.Singleton

/** Сервер для списка и главной — без адреса и ключа: их знает только запечатанная ссылка. */
data class ServerItem(
    val id: String,
    val name: String,
    val countryCode: String,
    val protocol: ProxyProtocol,
    val security: TransportSecurity,
    val transport: TransportKind,
    val port: Int,
) {
    /** «VLESS · REALITY». */
    val summary: String get() = ServerFacts.summary(protocol, security, transport)

    val encrypted: Boolean get() = ServerFacts.isEncrypted(protocol, security)
}

/** Итог добавления: новых и уже бывших в списке. */
data class AddResult(val added: Int, val duplicates: Int)

/**
 * Серверы пользователя. В базе сервер — ссылка, запечатанная ключом Android Keystore ([SecretBox]); открытыми лежат только
 * подписи для списка. Дубль — тот же сервер с другим названием: та же ссылка без имени и флага.
 */
@Singleton
class ServerRepository @Inject constructor(private val dao: ServerDao, private val box: SecretBox) {
    val servers: Flow<List<ServerItem>> = dao.observeAll().map { list -> list.mapNotNull { item(it) } }

    suspend fun add(servers: List<ServerDefinition>): AddResult = withContext(Dispatchers.IO) {
        val known = dao.all().mapNotNull { open(it) }.map { identity(it) }.toMutableSet()
        var position = dao.lastPosition()
        val now = System.currentTimeMillis()
        val fresh = ArrayList<ServerEntity>()
        var duplicates = 0
        for (server in servers) {
            if (!known.add(identity(server))) {
                duplicates++
                continue
            }

            position++
            fresh.add(
                ServerEntity(
                    id = UUID.randomUUID().toString(),
                    name = server.name,
                    countryCode = server.countryCode,
                    protocol = server.protocol.name,
                    security = server.security.kind.name,
                    transport = server.transport.kind.name,
                    port = server.port,
                    position = position,
                    addedAt = now,
                    sealed = box.seal(LinkWriter.write(server).toByteArray(Charsets.UTF_8)),
                ),
            )
        }

        dao.insertAll(fresh)
        AddResult(fresh.size, duplicates)
    }

    /** Сервер целиком — для подключения; нет в списке или не открылся (ключ Keystore утерян) — null. */
    suspend fun load(id: String): ServerDefinition? = withContext(Dispatchers.IO) { dao.byId(id)?.let { open(it) } }

    suspend fun delete(id: String) = withContext(Dispatchers.IO) { dao.delete(id) }

    private fun open(entity: ServerEntity): ServerDefinition? {
        val link = try {
            String(box.open(entity.sealed), Charsets.UTF_8)
        } catch (_: Exception) {
            return null
        }

        return LinkParser.parse(link).server?.copy(name = entity.name, countryCode = entity.countryCode)
    }

    private fun item(entity: ServerEntity): ServerItem? = try {
        ServerItem(
            id = entity.id,
            name = entity.name,
            countryCode = entity.countryCode,
            protocol = ProxyProtocol.valueOf(entity.protocol),
            security = TransportSecurity.valueOf(entity.security),
            transport = TransportKind.valueOf(entity.transport),
            port = entity.port,
        )
    } catch (_: IllegalArgumentException) {
        null
    }

    private fun identity(server: ServerDefinition) = LinkWriter.write(server.copy(name = "", countryCode = ""))
}

package com.privategateway.client.data

import androidx.room.withTransaction
import com.privategateway.client.core.links.LinkParser
import com.privategateway.client.core.links.LinkWriter
import com.privategateway.client.core.servers.Latency
import com.privategateway.client.core.servers.LatencyFailure
import com.privategateway.client.core.servers.ProxyProtocol
import com.privategateway.client.core.servers.ServerDefinition
import com.privategateway.client.core.servers.ServerFacts
import com.privategateway.client.core.servers.ServerItem
import com.privategateway.client.core.servers.ServerKeys
import com.privategateway.client.core.servers.SubscriptionRef
import com.privategateway.client.core.servers.TransportKind
import com.privategateway.client.core.servers.TransportSecurity
import com.privategateway.client.core.subscriptions.DownloadFailure
import com.privategateway.client.core.subscriptions.SelectedChange
import com.privategateway.client.core.subscriptions.StoredServer
import com.privategateway.client.core.subscriptions.SubscriptionAddress
import com.privategateway.client.core.subscriptions.SubscriptionMerge
import com.privategateway.client.core.subscriptions.UnreadableServer
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import java.net.URI
import java.util.UUID
import javax.inject.Inject
import javax.inject.Singleton

/** Итог добавления: новых и уже бывших в списке. */
data class AddResult(val added: Int, val duplicates: Int)

/**
 * Группа серверов для «Профили и подписки» (доска 12): подписка с адресом (открыто — только маска) или профиль без
 * адреса; последняя неудача обновления — причина и код ответа.
 */
data class GroupItem(
    val id: String,
    val name: String,
    val maskedUrl: String?,
    val servers: Int,
    val updatedAt: Long?,
    val lastAttemptAt: Long?,
    val failure: DownloadFailure?,
    val status: Int?,
) {
    /** Обновляется только подписка с адресом; профиль из ссылок — нет. */
    val canUpdate: Boolean get() = maskedUrl != null
}

/** Итог обновления подписки: сколько серверов пришло и ушло, что стало с выбранным и его название для уведомления. */
data class UpdateResult(val added: Int, val removed: Int, val selected: SelectedChange, val selectedName: String?)

/**
 * Серверы пользователя и их группы. В базе сервер — ссылка, запечатанная ключом Android Keystore ([SecretBox]); открытыми
 * лежат только подписи для списка. Адрес подписки (в нем токен доступа) — тоже только запечатанный, открыто — маска.
 * Дубль — тот же сервер под любым названием: тот же адрес, транспорт, защита и данные доступа (ServerKeys, как у
 * десктопа). Все записи — по одной (Mutex) и одной транзакцией.
 */
@Singleton
class ServerRepository @Inject constructor(private val database: AppDatabase, private val box: SecretBox) {
    private val dao = database.servers()
    private val subscriptions = database.subscriptions()

    val servers: Flow<List<ServerItem>> = dao.observeAll().map { list -> list.mapNotNull { item(it) } }

    val groups: Flow<List<GroupItem>> = subscriptions.observeWithCounts().map { list -> list.map { group(it) } }

    /** Подписки и профили в их порядке — заголовки групп списка серверов. */
    val subscriptionRefs: Flow<List<SubscriptionRef>> =
        subscriptions.observeAll().map { list -> list.map { SubscriptionRef(it.id, it.name, it.updatedAt) } }

    /** Сколько серверов в «Моих серверах» (без группы). */
    val manualCount: Flow<Int> = dao.observeManualCount()

    /** Добавления по одному: два сразу (двойное нажатие) прочли бы список до вставки и добавили бы те же серверы дважды. */
    private val writes = Mutex()

    /**
     * Добавить серверы; с названием группы — новой группой (подпиской, если есть [url], иначе профилем), без названия — в
     * «Мои серверы» (как у десктопа: адрес без названия не хранится).
     */
    suspend fun add(servers: List<ServerDefinition>, group: String? = null, url: URI? = null): AddResult =
        withContext(Dispatchers.IO) { writes.withLock { insert(servers, group?.trim()?.takeIf { it.isNotEmpty() }, url) } }

    private suspend fun insert(servers: List<ServerDefinition>, group: String?, url: URI?): AddResult {
        // Сохраненные серверы по ключу: тот же сервер, импортированный снова, берет защиту из новой ссылки (решение 484). Серверы
        // подписки с адресом — нет: их защиту ведет обновление подписки, иначе импорт и обновление переписывали бы ее по очереди.
        val refreshed = subscriptions.all().filter { it.sealedUrl != null }.map { it.id }.toSet()
        val known = HashSet<String>()
        val stored = HashMap<String, Pair<ServerEntity, ServerDefinition>>()
        for (entity in dao.all()) {
            val server = open(entity) ?: continue
            known.add(ServerKeys.key(server))
            if (entity.subscriptionId == null || entity.subscriptionId !in refreshed) {
                stored.putIfAbsent(ServerKeys.key(server), entity to server)
            }
        }

        var position = dao.lastPosition()
        val now = System.currentTimeMillis()
        val groupId = group?.let { UUID.randomUUID().toString() }
        val fresh = ArrayList<ServerEntity>()
        val renewed = LinkedHashMap<String, ByteArray>()
        var duplicates = 0
        for (server in servers) {
            val key = ServerKeys.key(server)
            if (!known.add(key)) {
                duplicates++
                val (entity, saved) = stored[key] ?: continue
                if (saved.security != server.security && entity.id !in renewed) {
                    // Название, группа, избранное — прежние; защита (закрепленный сертификат, отпечаток, ALPN) — из новой ссылки.
                    renewed[entity.id] = seal(saved.copy(security = server.security))
                }

                continue
            }

            fresh.add(entity(server, ++position, now, groupId))
        }

        val subscription = if (group != null && groupId != null) {
            SubscriptionEntity(
                id = groupId,
                name = group,
                maskedUrl = url?.let { SubscriptionAddress.mask(it) },
                sealedUrl = url?.let { box.seal(it.toString().toByteArray(Charsets.UTF_8)) },
                position = subscriptions.lastPosition() + 1,
                addedAt = now,
                updatedAt = url?.let { now },
                lastAttemptAt = url?.let { now },
            )
        } else {
            null
        }

        database.withTransaction {
            subscription?.let { subscriptions.insert(it) }
            renewed.forEach { (id, sealed) -> dao.setSealed(id, sealed) }

            dao.insertAll(fresh)
        }

        return AddResult(fresh.size, duplicates)
    }

    /** Сервер целиком — для подключения; нет в списке или не открылся (ключ Keystore утерян) — null. */
    suspend fun load(id: String): ServerDefinition? = withContext(Dispatchers.IO) { dao.byId(id)?.let { open(it) } }

    suspend fun delete(id: String) = withContext(Dispatchers.IO) { writes.withLock { dao.delete(id) } }

    /** Полный адрес подписки — для загрузки; не открылся (ключ Keystore утерян) или группа — профиль без адреса: null. */
    suspend fun subscriptionUrl(id: String): URI? = withContext(Dispatchers.IO) {
        val sealed = subscriptions.byId(id)?.sealedUrl ?: return@withContext null
        // Не расшифровался (ключ Keystore утерян, данные испорчены) или не адрес — подписку не загрузить: NoAddress.
        try {
            URI(String(box.open(sealed), Charsets.UTF_8))
        } catch (_: Exception) {
            null
        }
    }

    /**
     * Новый список подписки — как `ApplyUpdate` десктопа (SubscriptionMerge): свои серверы узнаются и остаются со своим
     * названием, избранным, задержкой и временем выбора; новые добавляются в группу; пропавшие удаляются, кроме выбранного —
     * он помечается «пропал из подписки» и остается, пока выбран. Подписка — «обновлена сейчас», прошлая неудача снята.
     */
    suspend fun applyUpdate(id: String, incoming: List<ServerDefinition>, selectedId: String?): UpdateResult = withContext(Dispatchers.IO) {
        writes.withLock {
            val subscription = subscriptions.byId(id) ?: return@withLock UpdateResult(0, 0, SelectedChange.None, null)
            val now = System.currentTimeMillis()
            val all = dao.all()
            val own = ArrayList<StoredServer>()
            val unreadable = ArrayList<UnreadableServer>()
            val elsewhere = HashSet<String>()
            for (entity in all) {
                val server = open(entity)
                when {
                    entity.subscriptionId != id -> server?.let { elsewhere.add(ServerKeys.key(it)) }
                    server == null -> unreadable.add(UnreadableServer(entity.id, entity.name))
                    else -> own.add(StoredServer(entity.id, server))
                }
            }

            val merge = SubscriptionMerge.merge(own, incoming, elsewhere, selectedId, unreadable)
            val byId = all.associateBy { it.id }
            // Запечатывание — до транзакции: Keystore не держит базу. Пишется только описание сервера: избранное, свое название,
            // задержка и время выбора, записанные тем временем, остаются.
            val kept = merge.kept.map { (keptId, server) -> Triple(keptId, server, seal(server)) }

            var position = dao.lastPosition()
            val added = merge.added.map { entity(it, ++position, now, id) }
            database.withTransaction {
                for ((keptId, server, sealed) in kept) {
                    dao.refresh(
                        keptId,
                        server.name,
                        server.countryCode,
                        server.protocol.name,
                        server.security.kind.name,
                        server.transport.kind.name,
                        server.port,
                        sealed,
                    )
                }

                dao.insertAll(added)
                if (merge.removed.isNotEmpty()) {
                    dao.deleteIds(merge.removed)
                }

                merge.missing?.let { dao.markMissing(it, now) }
                subscriptions.update(subscription.copy(updatedAt = now, lastAttemptAt = now, lastFailure = null, lastStatus = null))
            }

            val missing = merge.missing?.let { byId[it] }
            UpdateResult(added.size, merge.removed.size, merge.selected, missing?.let { it.customName ?: it.name })
        }
    }

    /** Подписка не обновилась: прошлый список остается, запоминаются время попытки, причина и код ответа. */
    suspend fun markFailed(id: String, failure: DownloadFailure, status: Int?) = withContext(Dispatchers.IO) {
        writes.withLock {
            val subscription = subscriptions.byId(id) ?: return@withLock
            subscriptions.update(subscription.copy(lastAttemptAt = System.currentTimeMillis(), lastFailure = failure.name, lastStatus = status))
        }
    }

    /** Новое название и, если дан, новый адрес подписки: с новым адресом прошлая неудача снимается. */
    suspend fun edit(id: String, name: String, url: URI?) = withContext(Dispatchers.IO) {
        writes.withLock {
            val subscription = subscriptions.byId(id) ?: return@withLock
            val renamed = subscription.copy(name = name.trim().ifEmpty { subscription.name })
            subscriptions.update(
                if (url == null) {
                    renamed
                } else {
                    renamed.copy(
                        maskedUrl = SubscriptionAddress.mask(url),
                        sealedUrl = box.seal(url.toString().toByteArray(Charsets.UTF_8)),
                        lastAttemptAt = null,
                        lastFailure = null,
                        lastStatus = null,
                    )
                },
            )
        }
    }

    /** Удалить группу со всеми ее серверами; null — «Мои серверы». Номера удаленных серверов — чтобы снять выбор. */
    suspend fun remove(group: String?): List<String> = withContext(Dispatchers.IO) {
        writes.withLock {
            val ids = (if (group == null) dao.manual() else dao.inGroup(group)).map { it.id }
            database.withTransaction {
                if (group == null) {
                    dao.deleteManual()
                } else {
                    dao.deleteGroup(group)
                    subscriptions.delete(group)
                }
            }

            ids
        }
    }

    suspend fun setFavorite(id: String, favorite: Boolean) = withContext(Dispatchers.IO) { dao.setFavorite(id, favorite) }

    /** Свое название; пустое — снова название из ссылки. */
    suspend fun rename(id: String, name: String?) = withContext(Dispatchers.IO) { dao.rename(id, name?.trim()?.takeIf { it.isNotEmpty() }) }

    /** Сервер выбран пользователем — для «Недавних». */
    suspend fun markUsed(id: String, at: Long = System.currentTimeMillis()) = withContext(Dispatchers.IO) { dao.markUsed(id, at) }

    /** Итоги замера задержки — одной транзакцией: список обновляется порцией, а не по строке. */
    suspend fun setLatency(results: Map<String, Latency>) = withContext(Dispatchers.IO) {
        database.withTransaction {
            for ((id, latency) in results) {
                dao.setLatency(id, latency.ms, latency.failure?.name)
            }
        }
    }

    /**
     * Ссылка для «Поделиться» — как запечатана: с названием из ссылки, а не своим (как у десктопа). Ключ Keystore
     * утерян — null. Открывается только по нажатию.
     */
    suspend fun shareLink(id: String): String? = withContext(Dispatchers.IO) {
        val sealed = dao.byId(id)?.sealed ?: return@withContext null
        try {
            String(box.open(sealed), Charsets.UTF_8)
        } catch (_: Exception) {
            null
        }
    }

    /** Адрес сервера для карточки — «адрес:порт», IPv6 в скобках; ссылка не открылась — null. */
    suspend fun address(id: String): String? = load(id)?.let { ServerFacts.address(it.host, it.port) }

    private fun entity(server: ServerDefinition, position: Int, now: Long, group: String?) = ServerEntity(
        id = UUID.randomUUID().toString(),
        name = server.name,
        countryCode = server.countryCode,
        protocol = server.protocol.name,
        security = server.security.kind.name,
        transport = server.transport.kind.name,
        port = server.port,
        position = position,
        addedAt = now,
        sealed = seal(server),
        subscriptionId = group,
    )

    private fun seal(server: ServerDefinition) = box.seal(LinkWriter.write(server).toByteArray(Charsets.UTF_8))

    /** Сервер из запечатанной ссылки; название — из ссылки (по нему подписка узнает «изменился»), не свое пользователя. */
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
            linkName = entity.name,
            countryCode = entity.countryCode,
            protocol = ProxyProtocol.valueOf(entity.protocol),
            security = TransportSecurity.valueOf(entity.security),
            transport = TransportKind.valueOf(entity.transport),
            port = entity.port,
            subscriptionId = entity.subscriptionId,
            favorite = entity.favorite,
            latency = latency(entity),
            lastUsedAt = entity.lastUsedAt,
            missingSince = entity.missingSince,
            customName = entity.customName,
        )
    } catch (_: IllegalArgumentException) {
        null
    }

    /** Замер: миллисекунды, причина без них (неизвестная — «нет ответа») или null — ни разу не мерили. */
    private fun latency(entity: ServerEntity): Latency? = when {
        entity.latencyMs != null -> Latency.of(entity.latencyMs)
        entity.latencyFailure != null ->
            Latency.failed(LatencyFailure.entries.firstOrNull { it.name == entity.latencyFailure } ?: LatencyFailure.NoResponse)
        else -> null
    }

    private fun group(row: SubscriptionWithCount) = GroupItem(
        id = row.subscription.id,
        name = row.subscription.name,
        maskedUrl = row.subscription.maskedUrl,
        servers = row.servers,
        updatedAt = row.subscription.updatedAt,
        lastAttemptAt = row.subscription.lastAttemptAt,
        failure = DownloadFailure.parse(row.subscription.lastFailure),
        status = row.subscription.lastStatus,
    )
}

package com.privategateway.client.core.subscriptions

import com.privategateway.client.core.servers.ServerDefinition
import com.privategateway.client.core.servers.ServerKeys

/** Что стало с выбранным сервером после обновления подписки. */
enum class SelectedChange {
    /** На месте или он не из этой подписки. */
    None,

    /** Пропал из подписки; оставлен в списке, пока выбран. */
    Missing,

    /** Пропал, но в подписке есть сервер с тем же названием — у него другой адрес или ключи. */
    Changed,
}

/** Сервер подписки в списке: свой номер и описание. */
data class StoredServer(val id: String, val server: ServerDefinition)

/**
 * Итог слияния: оставленные (номер → новое описание), новые, удаленные номера, выбранный, который пропал из подписки, и
 * что сказать о выбранном.
 */
data class SubscriptionMergeResult(
    val kept: List<Pair<String, ServerDefinition>>,
    val added: List<ServerDefinition>,
    val removed: List<String>,
    val missing: String?,
    val selected: SelectedChange,
)

/**
 * Обновление подписки — как `ApplyUpdate` десктопа: свой сервер узнается сначала точно ([ServerKeys.key]), потом по выходу
 * ([ServerKeys.exit]) — у него остаются номер, избранное и задержка; сервер, который уже есть в другой группе, второй раз
 * не добавляется; пропавшие удаляются, кроме выбранного: он остается, пока выбран, — другой сервер сам не выбирается.
 */
object SubscriptionMerge {
    fun merge(own: List<StoredServer>, incoming: List<ServerDefinition>, elsewhere: Set<String>, selectedId: String?): SubscriptionMergeResult {
        require(incoming.isNotEmpty()) { "Пустой список не заменяет подписку — прошлый остается" }
        val byKey = LinkedHashMap<String, StoredServer>()
        val byExit = LinkedHashMap<String, StoredServer>()
        for (stored in own) {
            byKey.putIfAbsent(ServerKeys.key(stored.server), stored)
            byExit.putIfAbsent(ServerKeys.exit(stored.server), stored)
        }

        val seen = HashSet<String>()
        val kept = ArrayList<Pair<String, ServerDefinition>>()
        val keptIds = HashSet<String>()
        val unmatched = ArrayList<ServerDefinition>()
        for (server in incoming) {
            val key = ServerKeys.key(server)
            if (!seen.add(key)) {
                continue
            }

            val stored = byKey[key]
            if (stored != null && keptIds.add(stored.id)) {
                kept.add(stored.id to server)
            } else {
                unmatched.add(server)
            }
        }

        val added = ArrayList<ServerDefinition>()
        for (server in unmatched) {
            val stored = byExit[ServerKeys.exit(server)]
            if (stored != null && keptIds.add(stored.id)) {
                kept.add(stored.id to server)
            } else if (ServerKeys.key(server) !in elsewhere) {
                added.add(server)
            }
        }

        val gone = own.filter { it.id !in keptIds }
        val selectedGone = gone.firstOrNull { it.id == selectedId }
        val change = when {
            selectedGone == null -> SelectedChange.None
            incoming.any { it.name == selectedGone.server.name } -> SelectedChange.Changed
            else -> SelectedChange.Missing
        }

        return SubscriptionMergeResult(kept, added, gone.filter { it !== selectedGone }.map { it.id }, selectedGone?.id, change)
    }
}

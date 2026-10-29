package com.privategateway.client.core.subscriptions

import com.privategateway.client.core.links.ImportParser
import com.privategateway.client.core.servers.ServerDefinition
import java.net.URI

/** Итог загрузки подписки для обновления: серверы и название либо почему не вышло. Адреса подписки здесь нет. */
sealed interface FetchResult {
    /** Загружено и разобрано: только строки, которые разобрались; список не пустой. */
    data class Fetched(val servers: List<ServerDefinition>, val title: String?) : FetchResult

    /** Не загрузилось или в ответе нет серверов — прошлый список остается; [status] — код ответа для [DownloadFailure.Status]. */
    data class Failed(val failure: DownloadFailure, val status: Int? = null) : FetchResult
}

/**
 * Загрузка и разбор подписки при обновлении — шаги `SubscriptionUpdater.UpdateAsync` десктопа: загрузить, разобрать,
 * оставить строки, которые разобрались; ни одного сервера — [DownloadFailure.NoServers], и прошлый список не трогается.
 * Загрузка блокирует поток — вызывать не из главного.
 */
object SubscriptionFetch {
    fun fetch(url: URI, source: SubscriptionSource): FetchResult {
        val content = try {
            source.download(url)
        } catch (e: SubscriptionDownloadException) {
            return FetchResult.Failed(e.failure, e.status)
        }

        return parse(content)
    }

    fun parse(content: SubscriptionContent): FetchResult {
        val servers = ImportParser.parse(content.text).lines.mapNotNull { it.result.server }
        return if (servers.isEmpty()) FetchResult.Failed(DownloadFailure.NoServers) else FetchResult.Fetched(servers, content.title)
    }

    /** Название новой подписки — как у десктопа: из заголовка Profile-Title, без него — сервер подписки. */
    fun suggestName(title: String?, url: URI): String = title ?: url.host.orEmpty()
}

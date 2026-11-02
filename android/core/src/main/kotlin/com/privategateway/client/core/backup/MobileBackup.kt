package com.privategateway.client.core.backup

import com.privategateway.client.core.links.JsonText
import com.privategateway.client.core.links.JsonTree
import java.time.Instant
import java.time.OffsetDateTime

/** Сервер в копии телефона: ссылка с ключами (как «Поделиться»), свое название и избранное. */
data class BackupServer(val link: String, val name: String?, val favorite: Boolean = false)

/** Подписка в копии: адрес и название группы. Адрес сохраняется и без ключей серверов — по нему они загрузятся снова. */
data class BackupSubscription(val url: String, val name: String)

/**
 * Содержимое копии телефона (решение 559) — внутри той же обертки .pgbackup, что у десктопа: серверы своими ссылками
 * (только с ключами — без ключей ссылка бесполезна), подписки адресами, настройки — значения ключей DataStore строками
 * (имена у Android и iOS одни, решение 459), поэтому копию Android открывает iPhone и наоборот. null — этой части в копии нет.
 * Копия десктопа устроена иначе (серверы — описанием десктопа): ее телефон узнает и говорит об этом, а не «файл испорчен».
 */
data class MobileBackup(
    val createdAt: Instant,
    val servers: List<BackupServer>? = null,
    val subscriptions: List<BackupSubscription>? = null,
    val settings: Map<String, String>? = null,
) {
    /** В копии есть ключи доступа — ссылки серверов. */
    val keys: Boolean get() = servers != null

    fun write(): ByteArray = JsonText.obj(
        "format" to FORMAT,
        "version" to VERSION,
        "createdAt" to createdAt.toString(),
        "servers" to servers?.map { linkedMapOf("link" to it.link, "name" to it.name, "favorite" to it.favorite) },
        "subscriptions" to subscriptions?.map { linkedMapOf("url" to it.url, "name" to it.name) },
        "settings" to settings?.toSortedMap(),
    ).toByteArray()

    companion object {
        const val FORMAT = "private-gateway-mobile"
        const val VERSION = 1

        /** Разобрать открытую копию; не копия телефона — [BackupException]: копия десктопа — [BackupError.FromComputer]. */
        fun read(content: ByteArray): MobileBackup {
            val root = try {
                JsonTree.parse(content.toString(Charsets.UTF_8)).value as? Map<*, *>
            } catch (_: JsonTree.SyntaxError) {
                null
            } ?: throw BackupException(BackupError.NotBackup)

            if (root["format"] != FORMAT) {
                // Копия десктопа: BackupPayload с CreatedAt и Settings/Groups (System.Text.Json, имена с заглавной).
                throw BackupException(if (root.containsKey("CreatedAt")) BackupError.FromComputer else BackupError.NotBackup)
            }

            val version = (root["version"] as? JsonTree.JsonNumber)?.text?.toIntOrNull() ?: throw BackupException(BackupError.NotBackup)
            if (version > VERSION) {
                throw BackupException(BackupError.NewerVersion)
            }

            val created = (root["createdAt"] as? String)?.let { runCatching { OffsetDateTime.parse(it).toInstant() }.getOrNull() }
                ?: throw BackupException(BackupError.NotBackup)
            val servers = (root["servers"] as? List<*>)?.mapNotNull { item ->
                val server = item as? Map<*, *> ?: return@mapNotNull null
                val link = server["link"] as? String ?: return@mapNotNull null
                BackupServer(link, server["name"] as? String, server["favorite"] == true)
            }
            val subscriptions = (root["subscriptions"] as? List<*>)?.mapNotNull { item ->
                val subscription = item as? Map<*, *> ?: return@mapNotNull null
                val url = subscription["url"] as? String ?: return@mapNotNull null
                BackupSubscription(url, subscription["name"] as? String ?: "")
            }
            val settings = (root["settings"] as? Map<*, *>)?.entries
                ?.mapNotNull { (key, value) -> (key as? String)?.let { name -> (value as? String)?.let { name to it } } }
                ?.toMap()
            return MobileBackup(created, servers, subscriptions, settings)
        }
    }
}

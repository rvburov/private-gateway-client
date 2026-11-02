package com.privategateway.client.data

import android.content.Context
import com.privategateway.client.core.backup.BackupEnvelope
import com.privategateway.client.core.backup.BackupError
import com.privategateway.client.core.backup.BackupException
import com.privategateway.client.core.backup.BackupServer
import com.privategateway.client.core.backup.BackupSubscription
import com.privategateway.client.core.backup.MobileBackup
import com.privategateway.client.core.links.LinkParser
import com.privategateway.client.subscriptions.SubscriptionUpdater
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File
import java.net.URI
import java.security.SecureRandom
import java.time.Instant
import javax.inject.Inject
import javax.inject.Singleton

/** Что положить в копию: серверы и подписки, настройки, ключи доступа (только вместе с серверами) — доска 14. */
data class BackupParts(val servers: Boolean, val settings: Boolean, val keys: Boolean)

/** Итог восстановления: сколько серверов добавлено, сколько подписок вернулось, вернулись ли настройки. */
data class BackupRestored(val servers: Int, val subscriptions: Int, val settings: Boolean)

/**
 * Резервная копия телефона (доска 14, решение 559) — `BackupService` десктопа: файл .pgbackup под паролем (тот же формат,
 * что у десктопа) и автокопия перед каждым восстановлением — со всем, включая ключи, зашифрована случайным ключом, который
 * сам запечатан ключом Android Keystore; файлы — в папке приложения. Долгое (PBKDF2, база) — не в главном потоке.
 */
@Singleton
class BackupRepository @Inject constructor(
    @param:ApplicationContext private val context: Context,
    private val servers: ServerRepository,
    private val settings: SettingsRepository,
    private val box: SecretBox,
    private val updater: SubscriptionUpdater,
) {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private val autoCopy get() = File(context.filesDir, "autocopy.pgbackup")
    private val autoKey get() = File(context.filesDir, "autocopy.key")

    /** Когда сделана последняя автокопия; null — ее нет. */
    suspend fun lastAutoCopy(): Instant? = withContext(Dispatchers.IO) {
        autoCopy.takeIf { it.exists() }?.let { BackupEnvelope.created(it.readText()) }
    }

    /** Текст файла копии под паролем. */
    suspend fun create(parts: BackupParts, password: String): String {
        val copy = collect(parts)
        return withContext(Dispatchers.Default) { BackupEnvelope.seal(copy.write(), password, copy.createdAt) }
    }

    /** Открыть копию паролем — что в ней; не та копия — [BackupException]. */
    suspend fun open(text: String, password: String): MobileBackup =
        withContext(Dispatchers.Default) { MobileBackup.read(BackupEnvelope.open(text, password)) }

    /** Вернуть серверы, подписки и настройки из копии; перед этим текущее — в автокопию: восстановление можно отменить. */
    suspend fun restore(copy: MobileBackup): BackupRestored {
        saveAutoCopy()
        return apply(copy)
    }

    /** Вернуть состояние из автокопии; текущее перед этим само станет автокопией. */
    suspend fun restoreAutoCopy(): BackupRestored {
        val copy = withContext(Dispatchers.IO) {
            val key = runCatching { box.open(autoKey.readBytes()) }.getOrNull()
            if (!autoCopy.exists() || key == null) {
                throw BackupException(BackupError.NotBackup)
            }

            MobileBackup.read(BackupEnvelope.open(autoCopy.readText(), key))
        }
        return restore(copy)
    }

    private suspend fun collect(parts: BackupParts): MobileBackup {
        val groups = servers.groups.first()
        // Профиль — группа из ссылок без адреса: ее серверы идут в копию вместе с названием группы.
        val profiles = groups.filter { it.maskedUrl == null }.associate { it.id to it.name }
        val subscriptions = if (parts.servers) {
            groups.filter { it.maskedUrl != null }.mapNotNull { group -> servers.subscriptionUrl(group.id)?.let { BackupSubscription(it.toString(), group.name) } }
        } else {
            null
        }
        val saved = if (parts.servers && parts.keys) {
            servers.servers.first()
                .filter { it.subscriptionId == null || it.subscriptionId in profiles }
                .mapNotNull { item ->
                    servers.shareLink(item.id)?.let { BackupServer(it, item.customName, item.favorite, item.subscriptionId?.let(profiles::get)) }
                }
        } else {
            null
        }

        return MobileBackup(Instant.now(), saved, subscriptions, if (parts.settings) settings.export() else null)
    }

    private suspend fun apply(copy: MobileBackup): BackupRestored {
        var added = 0
        copy.servers?.groupBy { it.group }?.forEach { (group, items) ->
            val definitions = items.mapNotNull { LinkParser.parse(it.link).server }
            added += servers.add(definitions, group).added
        }

        var restored = 0
        copy.subscriptions?.forEach { subscription ->
            val url = runCatching { URI(subscription.url) }.getOrNull() ?: return@forEach
            if (servers.subscriptionFor(url) == null) {
                servers.add(emptyList(), subscription.name.ifBlank { url.host ?: subscription.url }, url)
                restored++
            }

            // Серверы подписки загрузятся заново по ее адресу — в фоне: восстановление их не ждет.
            servers.subscriptionFor(url)?.let { id -> scope.launch { updater.update(id) } }
        }

        copy.settings?.let { settings.import(it) }
        return BackupRestored(added, restored, copy.settings != null)
    }

    private suspend fun saveAutoCopy() {
        val copy = collect(BackupParts(servers = true, settings = true, keys = true))
        withContext(Dispatchers.IO) {
            val key = runCatching { box.open(autoKey.readBytes()) }.getOrNull() ?: ByteArray(32).also { fresh ->
                SecureRandom().nextBytes(fresh)
                autoKey.writeBytes(box.seal(fresh))
            }
            val temporary = File(context.filesDir, "autocopy.pgbackup.tmp")
            temporary.writeText(BackupEnvelope.seal(copy.write(), key, copy.createdAt))
            temporary.renameTo(autoCopy)
        }
    }
}

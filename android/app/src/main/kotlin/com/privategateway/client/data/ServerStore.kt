package com.privategateway.client.data

import android.content.Context
import androidx.room.ColumnInfo
import androidx.room.Dao
import androidx.room.Database
import androidx.room.Embedded
import androidx.room.Entity
import androidx.room.Index
import androidx.room.Insert
import androidx.room.PrimaryKey
import androidx.room.Query
import androidx.room.Room
import androidx.room.RoomDatabase
import androidx.room.Update
import androidx.room.migration.Migration
import androidx.sqlite.db.SupportSQLiteDatabase
import kotlinx.coroutines.flow.Flow

/**
 * Сервер в базе: подписи для списка — открыто, сам сервер (ссылка с адресом и ключом) — запечатан [SecretBox]. Адрес
 * сервера и ключ без ключа Keystore этого устройства не прочитать. [name] — название из ссылки (по нему подписка узнает
 * сервер), [customName] — свое название пользователя, оно переживает обновления подписки. Группа — подписка или профиль
 * ([subscriptionId]), без группы — «Мои серверы». [missingSince] — выбранный сервер пропал из подписки и оставлен, пока
 * выбран.
 */
@Entity(tableName = "servers", indices = [Index("subscriptionId")])
data class ServerEntity(
    @PrimaryKey val id: String,
    val name: String,
    val countryCode: String,
    val protocol: String,
    val security: String,
    val transport: String,
    val port: Int,
    val position: Int,
    val addedAt: Long,
    val sealed: ByteArray,
    @ColumnInfo(defaultValue = "NULL") val subscriptionId: String? = null,
    @ColumnInfo(defaultValue = "NULL") val customName: String? = null,
    @ColumnInfo(defaultValue = "0") val favorite: Boolean = false,
    @ColumnInfo(defaultValue = "NULL") val latencyMs: Int? = null,
    @ColumnInfo(defaultValue = "NULL") val latencyFailure: String? = null,
    @ColumnInfo(defaultValue = "NULL") val lastUsedAt: Long? = null,
    @ColumnInfo(defaultValue = "NULL") val missingSince: Long? = null,
)

/**
 * Группа серверов: подписка с адресом или профиль без него (тогда [maskedUrl] и [sealedUrl] — null и группа не
 * обновляется). Адрес подписки — секрет (в нем токен доступа): целиком он только запечатан, открыто — маска
 * («https://example.com/sub/••••»). Последняя неудача — причина ([lastFailure], имя DownloadFailure) и код ответа.
 */
@Entity(tableName = "subscriptions")
data class SubscriptionEntity(
    @PrimaryKey val id: String,
    val name: String,
    val maskedUrl: String?,
    val sealedUrl: ByteArray?,
    val position: Int,
    val addedAt: Long,
    val updatedAt: Long? = null,
    val lastAttemptAt: Long? = null,
    val lastFailure: String? = null,
    val lastStatus: Int? = null,
)

@Dao
interface ServerDao {
    @Query("SELECT * FROM servers ORDER BY position, addedAt")
    fun observeAll(): Flow<List<ServerEntity>>

    @Query("SELECT * FROM servers ORDER BY position, addedAt")
    suspend fun all(): List<ServerEntity>

    @Query("SELECT * FROM servers WHERE id = :id")
    suspend fun byId(id: String): ServerEntity?

    @Query("SELECT COALESCE(MAX(position), -1) FROM servers")
    suspend fun lastPosition(): Int

    @Query("SELECT * FROM servers WHERE subscriptionId = :group ORDER BY position, addedAt")
    suspend fun inGroup(group: String): List<ServerEntity>

    @Query("SELECT * FROM servers WHERE subscriptionId IS NULL ORDER BY position, addedAt")
    suspend fun manual(): List<ServerEntity>

    @Query("SELECT COUNT(*) FROM servers WHERE subscriptionId IS NULL")
    fun observeManualCount(): Flow<Int>

    @Insert
    suspend fun insertAll(servers: List<ServerEntity>)

    @Update
    suspend fun updateAll(servers: List<ServerEntity>)

    @Query("DELETE FROM servers WHERE id = :id")
    suspend fun delete(id: String)

    @Query("DELETE FROM servers WHERE id IN (:ids)")
    suspend fun deleteIds(ids: List<String>)

    @Query("DELETE FROM servers WHERE subscriptionId = :group")
    suspend fun deleteGroup(group: String)

    @Query("DELETE FROM servers WHERE subscriptionId IS NULL")
    suspend fun deleteManual()

    /** Выбранный сервер пропал из подписки: время — первого раза, повторное обновление его не сдвигает. */
    @Query("UPDATE servers SET missingSince = :at WHERE id = :id AND missingSince IS NULL")
    suspend fun markMissing(id: String, at: Long)

    @Query("UPDATE servers SET favorite = :favorite WHERE id = :id")
    suspend fun setFavorite(id: String, favorite: Boolean)

    @Query("UPDATE servers SET customName = :name WHERE id = :id")
    suspend fun rename(id: String, name: String?)

    @Query("UPDATE servers SET lastUsedAt = :at WHERE id = :id")
    suspend fun markUsed(id: String, at: Long)

    @Query("UPDATE servers SET latencyMs = :ms, latencyFailure = :failure WHERE id = :id")
    suspend fun setLatency(id: String, ms: Int?, failure: String?)

    /** Только описание сервера (решение 484): название, избранное, задержку и выбор, записанные тем временем, не затирает. */
    @Query("UPDATE servers SET sealed = :sealed WHERE id = :id")
    suspend fun setSealed(id: String, sealed: ByteArray)
}

/** Подписка и число ее серверов — для «Профили и подписки». */
data class SubscriptionWithCount(@Embedded val subscription: SubscriptionEntity, val servers: Int)

@Dao
interface SubscriptionDao {
    @Query("SELECT * FROM subscriptions ORDER BY position, addedAt")
    fun observeAll(): Flow<List<SubscriptionEntity>>

    @Query("SELECT * FROM subscriptions ORDER BY position, addedAt")
    suspend fun all(): List<SubscriptionEntity>

    @Query("SELECT * FROM subscriptions WHERE id = :id")
    suspend fun byId(id: String): SubscriptionEntity?

    @Query(
        "SELECT subscriptions.*, (SELECT COUNT(*) FROM servers WHERE servers.subscriptionId = subscriptions.id) AS servers " +
            "FROM subscriptions ORDER BY position, addedAt",
    )
    fun observeWithCounts(): Flow<List<SubscriptionWithCount>>

    @Query("SELECT COALESCE(MAX(position), -1) FROM subscriptions")
    suspend fun lastPosition(): Int

    @Insert
    suspend fun insert(subscription: SubscriptionEntity)

    @Update
    suspend fun update(subscription: SubscriptionEntity)

    @Query("DELETE FROM subscriptions WHERE id = :id")
    suspend fun delete(id: String)
}

/**
 * База приложения. Схема меняется только миграцией: серверы пользователя не теряются при обновлении. Схемы версий 2 и
 * дальше выгружаются в `app/schemas` (проверка миграций MigrationTestHelper); версия 1 не выгружалась — миграцию 1 → 2
 * проверяет тест на базе, собранной по ее DDL.
 */
@Database(entities = [ServerEntity::class, SubscriptionEntity::class], version = 2, exportSchema = true)
abstract class AppDatabase : RoomDatabase() {
    abstract fun servers(): ServerDao

    abstract fun subscriptions(): SubscriptionDao

    companion object {
        const val NAME = "private-gateway.db"

        fun open(context: Context, name: String = NAME): AppDatabase =
            Room.databaseBuilder(context, AppDatabase::class.java, name).addMigrations(MIGRATION_1_2).build()

        /** 1 → 2: группы и подписки; свое название, избранное, задержка, когда выбирался, «пропал из подписки». */
        val MIGRATION_1_2 = object : Migration(1, 2) {
            override fun migrate(db: SupportSQLiteDatabase) {
                db.execSQL("ALTER TABLE `servers` ADD COLUMN `subscriptionId` TEXT DEFAULT NULL")
                db.execSQL("ALTER TABLE `servers` ADD COLUMN `customName` TEXT DEFAULT NULL")
                db.execSQL("ALTER TABLE `servers` ADD COLUMN `favorite` INTEGER NOT NULL DEFAULT 0")
                db.execSQL("ALTER TABLE `servers` ADD COLUMN `latencyMs` INTEGER DEFAULT NULL")
                db.execSQL("ALTER TABLE `servers` ADD COLUMN `latencyFailure` TEXT DEFAULT NULL")
                db.execSQL("ALTER TABLE `servers` ADD COLUMN `lastUsedAt` INTEGER DEFAULT NULL")
                db.execSQL("ALTER TABLE `servers` ADD COLUMN `missingSince` INTEGER DEFAULT NULL")
                db.execSQL("CREATE INDEX IF NOT EXISTS `index_servers_subscriptionId` ON `servers` (`subscriptionId`)")
                db.execSQL(
                    "CREATE TABLE IF NOT EXISTS `subscriptions` (`id` TEXT NOT NULL, `name` TEXT NOT NULL, `maskedUrl` TEXT, " +
                        "`sealedUrl` BLOB, `position` INTEGER NOT NULL, `addedAt` INTEGER NOT NULL, `updatedAt` INTEGER, " +
                        "`lastAttemptAt` INTEGER, `lastFailure` TEXT, `lastStatus` INTEGER, PRIMARY KEY(`id`))",
                )
            }
        }
    }
}

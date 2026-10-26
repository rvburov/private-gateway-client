package com.privategateway.client.data

import android.content.Context
import androidx.room.ColumnInfo
import androidx.room.Dao
import androidx.room.Database
import androidx.room.Entity
import androidx.room.Index
import androidx.room.Insert
import androidx.room.PrimaryKey
import androidx.room.Query
import androidx.room.Room
import androidx.room.RoomDatabase
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
class ServerEntity(
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
class SubscriptionEntity(
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

    @Insert
    suspend fun insertAll(servers: List<ServerEntity>)

    @Query("DELETE FROM servers WHERE id = :id")
    suspend fun delete(id: String)
}

@Dao
interface SubscriptionDao {
    @Query("SELECT * FROM subscriptions ORDER BY position, addedAt")
    fun observeAll(): Flow<List<SubscriptionEntity>>

    @Query("SELECT * FROM subscriptions ORDER BY position, addedAt")
    suspend fun all(): List<SubscriptionEntity>

    @Query("SELECT * FROM subscriptions WHERE id = :id")
    suspend fun byId(id: String): SubscriptionEntity?

    @Insert
    suspend fun insert(subscription: SubscriptionEntity)
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

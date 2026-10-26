package com.privategateway.client.data

import androidx.room.Dao
import androidx.room.Database
import androidx.room.Entity
import androidx.room.Insert
import androidx.room.PrimaryKey
import androidx.room.Query
import androidx.room.RoomDatabase
import kotlinx.coroutines.flow.Flow

/**
 * Сервер в базе: подписи для списка — открыто, сам сервер (ссылка с адресом и ключом) — запечатан [SecretBox]. Адрес
 * сервера и ключ без ключа Keystore этого устройства не прочитать.
 */
@Entity(tableName = "servers")
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

/** База приложения. Схема меняется только миграцией: серверы пользователя не теряются при обновлении. */
@Database(entities = [ServerEntity::class], version = 1, exportSchema = false)
abstract class AppDatabase : RoomDatabase() {
    abstract fun servers(): ServerDao
}

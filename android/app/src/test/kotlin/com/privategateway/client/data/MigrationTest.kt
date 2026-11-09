package com.privategateway.client.data

import android.content.Context
import android.database.sqlite.SQLiteDatabase
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Test
import org.junit.runner.RunWith
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull

/**
 * Обновление приложения не теряет серверы: база версии 1 (как ее создавал Room выпуска 9.1) и версии 2 открывается
 * текущей схемой через миграции, а Room сверяет итог с сущностями. Схема версии 1 не выгружалась — база собирается по ее
 * DDL; версии 2 — по DDL из выгруженной схемы.
 */
@RunWith(AndroidJUnit4::class)
class MigrationTest {
    private val context = ApplicationProvider.getApplicationContext<Context>()
    private val name = "migration-test.db"

    @After
    fun delete() {
        context.deleteDatabase(name)
    }

    @Test
    fun `servers of version 1 survive the upgrade and get defaults`() {
        version1 { database ->
            database.execSQL(
                "INSERT INTO servers (id, name, countryCode, protocol, security, transport, port, position, addedAt, sealed) " +
                    "VALUES ('nl', 'Нидерланды 01', 'NL', 'Vless', 'Reality', 'Tcp', 443, 0, 1000, X'500102')",
            )
        }

        val database = AppDatabase.open(context, name)
        try {
            runBlocking {
                val server = assertNotNull(database.servers().byId("nl"), "сервер версии 1 потерян")
                assertEquals("Нидерланды 01", server.name)
                assertEquals(443, server.port)
                assertContentEquals(byteArrayOf(0x50, 0x01, 0x02), server.sealed)
                // Новые поля — по умолчанию: без группы («Мои серверы»), не избранный, не мерился, не выбирался.
                assertNull(server.subscriptionId)
                assertNull(server.customName)
                assertFalse(server.favorite)
                assertNull(server.latencyMs)
                assertNull(server.latencyFailure)
                assertNull(server.lastUsedAt)
                assertNull(server.missingSince)

                // Таблица подписок есть и принимает профиль без адреса.
                database.subscriptions().insert(SubscriptionEntity("p", "Профиль", null, null, 0, 2000))
                val profile = assertNotNull(database.subscriptions().byId("p"))
                assertNull(profile.maskedUrl)
                assertNull(profile.sealedUrl)
                assertEquals(listOf("p"), database.subscriptions().all().map { it.id })
            }
        } finally {
            database.close()
        }
    }

    @Test
    fun `servers of version 2 survive the upgrade without a remembered exit`() {
        version2 { database ->
            database.execSQL(
                "INSERT INTO servers (id, name, countryCode, protocol, security, transport, port, position, addedAt, sealed, favorite, customName) " +
                    "VALUES ('nl', 'Нидерланды 01', 'NL', 'Vless', 'Reality', 'Tcp', 443, 0, 1000, X'500102', 1, 'Дом')",
            )
        }

        val database = AppDatabase.open(context, name)
        try {
            runBlocking {
                val server = assertNotNull(database.servers().byId("nl"), "сервер версии 2 потерян")
                assertEquals("Дом", server.customName)
                assertEquals(true, server.favorite)
                assertContentEquals(byteArrayOf(0x50, 0x01, 0x02), server.sealed)
                assertNull(server.sealedExit, "выход сервера еще не мерили (решение 583)")

                database.servers().setExit("nl", byteArrayOf(0x50, 0x03))
                assertContentEquals(byteArrayOf(0x50, 0x03), database.servers().byId("nl")?.sealedExit)
            }
        } finally {
            database.close()
        }
    }

    /** База версии 2 — DDL из выгруженной схемы `schemas/…/2.json` и номер версии 2. */
    private fun version2(fill: (SQLiteDatabase) -> Unit) {
        val file = context.getDatabasePath(name)
        file.parentFile?.mkdirs()
        SQLiteDatabase.openOrCreateDatabase(file, null).use { database ->
            database.execSQL(
                "CREATE TABLE IF NOT EXISTS `servers` (`id` TEXT NOT NULL, `name` TEXT NOT NULL, `countryCode` TEXT NOT NULL, " +
                    "`protocol` TEXT NOT NULL, `security` TEXT NOT NULL, `transport` TEXT NOT NULL, `port` INTEGER NOT NULL, " +
                    "`position` INTEGER NOT NULL, `addedAt` INTEGER NOT NULL, `sealed` BLOB NOT NULL, `subscriptionId` TEXT DEFAULT NULL, " +
                    "`customName` TEXT DEFAULT NULL, `favorite` INTEGER NOT NULL DEFAULT 0, `latencyMs` INTEGER DEFAULT NULL, " +
                    "`latencyFailure` TEXT DEFAULT NULL, `lastUsedAt` INTEGER DEFAULT NULL, `missingSince` INTEGER DEFAULT NULL, PRIMARY KEY(`id`))",
            )
            database.execSQL("CREATE INDEX IF NOT EXISTS `index_servers_subscriptionId` ON `servers` (`subscriptionId`)")
            database.execSQL(
                "CREATE TABLE IF NOT EXISTS `subscriptions` (`id` TEXT NOT NULL, `name` TEXT NOT NULL, `maskedUrl` TEXT, `sealedUrl` BLOB, " +
                    "`position` INTEGER NOT NULL, `addedAt` INTEGER NOT NULL, `updatedAt` INTEGER, `lastAttemptAt` INTEGER, " +
                    "`lastFailure` TEXT, `lastStatus` INTEGER, PRIMARY KEY(`id`))",
            )
            fill(database)
            database.version = 2
        }
    }

    /** База версии 1 — DDL, который Room 2.7 создавал для ServerEntity выпуска 9.1, и номер версии 1. */
    private fun version1(fill: (SQLiteDatabase) -> Unit) {
        val file = context.getDatabasePath(name)
        file.parentFile?.mkdirs()
        SQLiteDatabase.openOrCreateDatabase(file, null).use { database ->
            database.execSQL(
                "CREATE TABLE IF NOT EXISTS `servers` (`id` TEXT NOT NULL, `name` TEXT NOT NULL, `countryCode` TEXT NOT NULL, " +
                    "`protocol` TEXT NOT NULL, `security` TEXT NOT NULL, `transport` TEXT NOT NULL, `port` INTEGER NOT NULL, " +
                    "`position` INTEGER NOT NULL, `addedAt` INTEGER NOT NULL, `sealed` BLOB NOT NULL, PRIMARY KEY(`id`))",
            )
            fill(database)
            database.version = 1
        }
    }
}

package com.privategateway.client.data

import androidx.datastore.core.DataStoreFactory
import androidx.datastore.core.ReadScope
import androidx.datastore.core.Storage
import androidx.datastore.core.StorageConnection
import androidx.datastore.core.WriteScope
import androidx.datastore.core.createSingleProcessCoordinator
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.async
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.onEach
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import kotlinx.coroutines.withContext
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.File
import kotlin.test.assertEquals

/** Новый слушатель во время записи не должен навсегда пропустить ее результат (DataStore b/431787506). */
class DataStoreUpdateRaceTest {
    @get:Rule
    val folder = TemporaryFolder()

    @Test
    fun `a collector started during a write receives its result without another write`() = runBlocking<Unit> {
        withTimeout(10_000) {
            val job = SupervisorJob()
            val storage = PausedStorage(folder.root.resolve("settings"))
            val store = DataStoreFactory.create(storage = storage, scope = CoroutineScope(Dispatchers.Default + job))
            try {
                assertEquals(0, store.data.first())
                val write = async(Dispatchers.Default) { store.updateData { 1 } }
                storage.entered.await()

                // Счетчик версии уже вырос, но значение в хранилище пока старое: новый слушатель прочтет именно его.
                val firstRead = CompletableDeferred<Int>()
                val read = async(Dispatchers.Default) {
                    store.data.onEach { firstRead.complete(it) }.first { it == 1 }
                }
                assertEquals(0, firstRead.await())
                storage.release.complete(Unit)
                assertEquals(1, write.await())
                assertEquals(1, storage.value, "запись завершена и данные в хранилище уже новые")
                assertEquals(1, read.await(), "существующий слушатель получает новую версию без дополнительной записи")
            } finally {
                storage.release.complete(Unit)
                withContext(NonCancellable) { job.cancelAndJoin() }
            }
        }
    }

    /** Управляемая задержка записи через публичный Storage API; координация — настоящая однопроцессная DataStore. */
    private class PausedStorage(file: File) : Storage<Int> {
        val entered = CompletableDeferred<Unit>()
        val release = CompletableDeferred<Unit>()
        @Volatile var value = 0
        private val sync = createSingleProcessCoordinator(file)

        override fun createConnection(): StorageConnection<Int> = object : StorageConnection<Int> {
            override val coordinator = sync

            override suspend fun <R> readScope(block: suspend ReadScope<Int>.(Boolean) -> R): R =
                block(object : ReadScope<Int> {
                    override suspend fun readData() = value
                    override fun close() = Unit
                }, false)

            override suspend fun writeScope(block: suspend WriteScope<Int>.() -> Unit) {
                block(object : WriteScope<Int> {
                    override suspend fun readData() = value
                    override suspend fun writeData(value: Int) {
                        entered.complete(Unit)
                        release.await()
                        this@PausedStorage.value = value
                    }
                    override fun close() = Unit
                })
            }

            override fun close() = Unit
        }
    }
}

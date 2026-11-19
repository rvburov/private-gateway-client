package com.privategateway.client.data

import android.content.Context
import com.privategateway.client.core.countries.CountryDatabase
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import java.io.File
import java.io.IOException
import java.util.zip.GZIPInputStream
import javax.inject.Inject
import javax.inject.Singleton

/** Страна адреса сервера для флага — как CountryServerCatalog десктопа, но только для адреса цифрами: DNS телефон не спрашивает. */
interface CountryLookup {
    /** Код ISO 3166-1 («DE»); адрес не цифрами, частный, служебный или без записи — «». */
    suspend fun country(host: String): String

    /** Геобаза новая для этого запуска (поставили или обновили приложение) — страны сохраненных серверов устарели. */
    suspend fun renewed(): Boolean

    companion object {
        /** Без геобазы — тесты, где флаг не важен: страна только из названия ссылки. */
        val NONE: CountryLookup = object : CountryLookup {
            override suspend fun country(host: String) = ""

            override suspend fun renewed() = false
        }
    }
}

/**
 * Геобаза из assets/countries/country.bin.gz (DB-IP Country Lite, shared/countries, общая с десктопом). Распаковывается один
 * раз в noBackupFilesDir под номером сборки приложения и читается отображением в память: 25 МБ записей не занимают кучу.
 * Поврежденная или отсутствующая база не мешает работе — флага нет, как у десктопа.
 */
@Singleton
class AssetCountries @Inject constructor(@ApplicationContext private val context: Context) : CountryLookup {
    private val lock = Mutex()
    private var database: CountryDatabase? = null
    private var failed = false
    private var extracted = false

    // Номер и время установки сборки: новая сборка — геобаза распаковывается заново.
    private val revision: String = context.packageManager.getPackageInfo(context.packageName, 0).let { "${it.longVersionCode}-${it.lastUpdateTime}" }

    override suspend fun country(host: String): String = withContext(Dispatchers.IO) { database()?.lookup(host).orEmpty() }

    override suspend fun renewed(): Boolean = withContext(Dispatchers.IO) {
        database()
        extracted
    }

    private suspend fun database(): CountryDatabase? = lock.withLock {
        if (database == null && !failed) {
            database = try {
                open()
            } catch (_: IOException) {
                failed = true
                null
            }
        }

        database
    }

    private fun open(): CountryDatabase {
        val folder = File(context.noBackupFilesDir, "countries").apply { mkdirs() }
        val file = File(folder, "country-$revision.bin")
        if (!file.exists()) {
            folder.listFiles()?.forEach { it.delete() }
            val partial = File(folder, file.name + ".part")
            GZIPInputStream(context.assets.open("countries/country.bin.gz")).use { input ->
                partial.outputStream().use { input.copyTo(it) }
            }

            if (!partial.renameTo(file)) {
                throw IOException("Геобаза не сохранилась")
            }

            extracted = true
        }

        return CountryDatabase.open(file)
    }
}

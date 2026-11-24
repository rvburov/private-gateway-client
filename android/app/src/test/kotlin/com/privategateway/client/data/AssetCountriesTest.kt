package com.privategateway.client.data

import android.content.Context
import android.content.ContextWrapper
import android.graphics.BitmapFactory
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.privategateway.client.core.countries.CountryDatabase
import kotlinx.coroutines.runBlocking
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import org.junit.runner.RunWith
import java.io.File
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertTrue

/** Настоящие ресурсы APK: база и PNG должны открываться так же, как на телефоне, без внешнего DNS. */
@RunWith(AndroidJUnit4::class)
class AssetCountriesTest {
    @get:Rule val folder = TemporaryFolder()
    private val context = object : ContextWrapper(ApplicationProvider.getApplicationContext<Context>()) {
        override fun getNoBackupFilesDir(): File = folder.root
    }

    private fun expected(): String = context.assets.open("countries/country.bin").use {
        assertNotNull(CountryDatabase.read(it).lookup(PUBLIC_DNS))
    }

    @Test
    fun `bundled database resolves a public dns address and the matching flag exists`() = runBlocking {
        val lookup = AssetCountries(context)
        val code = lookup.country(PUBLIC_DNS)
        assertEquals(expected(), code)
        assertEquals(2, code.length)
        context.assets.open("countries/flags/$code.png").use { assertNotNull(BitmapFactory.decodeStream(it)) }
        assertTrue(lookup.renewed())
        assertFalse(lookup.renewed(), "пересчет сохраненных стран запрашивается один раз, а не при каждом событии сети")
        assertFalse(AssetCountries(context).renewed(), "исправный кеш не распаковывается при каждом запуске")
    }

    @Test
    fun `corrupted extracted database repairs itself from the apk without an application upgrade`() = runBlocking {
        val code = AssetCountries(context).country(PUBLIC_DNS)
        val cache = File(folder.root, "countries").listFiles()!!.single { it.extension == "bin" }
        cache.writeText("interrupted cached database")
        val repaired = AssetCountries(context)
        assertEquals(code, repaired.country(PUBLIC_DNS))
        assertTrue(repaired.renewed())
        assertTrue(cache.length() > 12)
    }

    @Test
    fun `an unmapped dns answer does not discard a consistent country from another answer`() = runBlocking {
        val lookup = AssetCountries(context)
        lookup.resolve = { listOf("192.0.2.1", PUBLIC_DNS) }
        assertEquals(expected(), lookup.resolvedCountry("vpn.example.com"))
    }

    private companion object {
        // Публичный DNS Cloudflare, только локальный поиск по геобазе, запросов к нему нет.
        const val PUBLIC_DNS = "1.1.1.1"
    }
}

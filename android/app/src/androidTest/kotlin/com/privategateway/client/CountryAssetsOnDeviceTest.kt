package com.privategateway.client

import android.graphics.BitmapFactory
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.privategateway.client.data.AssetCountries
import com.privategateway.client.ui.theme.CountryFlags
import kotlinx.coroutines.runBlocking
import org.junit.Test
import org.junit.runner.RunWith
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertTrue

/** Проверяет ресурсы реального APK на Android; обращения к публичному DNS не выполняются. */
@RunWith(AndroidJUnit4::class)
class CountryAssetsOnDeviceTest {
    @Test
    fun country_database_and_native_flag_decode_work_from_the_installed_apk() = runBlocking<Unit> {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        val country = AssetCountries(context).country("1.1.1.1")
        assertEquals(2, country.length)
        assertTrue(country.all { it in 'A'..'Z' })
        context.assets.open("countries/flags/$country.png").use { assertNotNull(BitmapFactory.decodeStream(it)) }
        assertNotNull(CountryFlags.image(context, country), "PNG выбранной страны должен читаться на Android")
    }
}

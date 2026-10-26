package com.privategateway.client.ui

import android.content.Context
import android.content.res.Configuration
import android.content.res.Resources
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.privategateway.client.Fixtures
import com.privategateway.client.R
import org.json.JSONObject
import org.junit.Test
import org.junit.runner.RunWith
import java.util.Locale
import kotlin.test.assertEquals
import kotlin.test.assertNotEquals

/**
 * Тексты приложения — из общих shared/specifications/localization через генератор build-logic и aapt: каждый ключ RU и EN
 * есть в ресурсах и читается Android тем же текстом.
 */
@RunWith(AndroidJUnit4::class)
class ResourcesTest {
    private val context = ApplicationProvider.getApplicationContext<Context>()

    private fun resources(language: String): Resources {
        val configuration = Configuration(context.resources.configuration)
        configuration.setLocale(Locale.forLanguageTag(language))
        return context.createConfigurationContext(configuration).resources
    }

    @Test
    fun `every shared text reads back in russian and english`() {
        for (language in listOf("ru", "en")) {
            val texts = JSONObject(Fixtures.localization.resolve("$language.json").readText())
            val resources = resources(language)
            for (key in texts.keys()) {
                val name = key.replace('.', '_').replace('-', '_')
                @Suppress("DiscouragedApi")
                val id = resources.getIdentifier(name, "string", context.packageName)
                assertNotEquals(0, id, "$language: нет ресурса $name")
                assertEquals(texts.getString(key), shared(resources.getString(id)), "$language: $key")
            }
        }
    }

    @Test
    fun `placeholders are filled by android formatting`() {
        assertEquals("порт 443", resources("ru").getString(R.string.server_port, "443"))
        assertEquals("Добавить 3 сервера", resources("ru").getString(R.string.import_add_few, "3"))
        assertEquals("IP скрыт", resources("ru").getString(R.string.status_connected_title))
    }

    @Test
    fun `phones in other languages get english`() {
        assertEquals(resources("en").getString(R.string.nav_servers), resources("de").getString(R.string.nav_servers))
        assertNotEquals(resources("ru").getString(R.string.nav_servers), resources("de").getString(R.string.nav_servers))
    }

    /** Текст ресурса в записи общих файлов: «%1$s» → «{0}», «%%» → «%» (у строк с подстановками). */
    private fun shared(text: String): String {
        val placeholder = Regex("""%(\d+)\${'$'}s""")
        if (!placeholder.containsMatchIn(text)) {
            return text
        }

        return Regex("""%(\d+)\${'$'}s|%%""").replace(text) { if (it.value == "%%") "%" else "{${it.groupValues[1].toInt() - 1}}" }
    }
}

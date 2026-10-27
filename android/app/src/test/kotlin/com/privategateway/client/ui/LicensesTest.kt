package com.privategateway.client.ui

import android.content.Context
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.assertTextContains
import androidx.compose.ui.test.assertTextEquals
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performScrollTo
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.privategateway.client.R
import com.privategateway.client.core.settings.TunnelSettings
import com.privategateway.client.licenses.OpenComponents
import com.privategateway.client.ui.settings.SettingsScreen
import com.privategateway.client.ui.theme.PgTheme
import org.json.JSONObject
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.annotation.Config
import kotlin.test.assertEquals
import kotlin.test.assertTrue

/** Лицензии открытых компонентов: что сборка положила в приложение (assets) и как это видно в «Настройках». */
@RunWith(AndroidJUnit4::class)
@Config(qualifiers = "w384dp-h832dp")
class LicensesTest {
    @get:Rule
    val compose = createComposeRule()

    private val context = ApplicationProvider.getApplicationContext<Context>()

    @Test
    fun `the app carries every component with its license text and the notices file`() {
        val components = OpenComponents.read(context.assets)
        val names = components.map { it.name }
        assertTrue(listOf("libXray", "Xray-core", "AndroidX", "Jetpack Compose", "Kotlin", "ZXing").all { it in names }, names.toString())
        val xray = components.single { it.name == "Xray-core" }
        assertEquals("MPL-2.0", xray.label)
        assertEquals("libXray", xray.inside)
        val libxray = components.single { it.name == "libXray" }
        assertTrue(libxray.version.isNotEmpty() && libxray.source.endsWith(libxray.version), libxray.source)
        // Каждый компонент из Maven пришел хотя бы одним модулем: сборка сверила список с зависимостями выпуска.
        for (component in components.filter { it.inside.isEmpty() && it.name != "libXray" }) {
            assertTrue(component.modules.isNotEmpty(), "${component.name}: нет модулей")
        }

        assertTrue(components.single { it.name == "Jetpack Compose" }.modules.all { it.startsWith("androidx.compose.") })
        for (id in components.flatMap { it.texts }.toSet()) {
            assertTrue(OpenComponents.text(context.assets, id).length > 500, "текст $id")
        }

        val notices = context.assets.open(OpenComponents.NOTICES).bufferedReader().use { it.readText() }
        for (name in names) {
            assertTrue(notices.lines().any { it == name || it.startsWith("$name ") }, "$name в THIRD-PARTY-NOTICES")
        }

        // SBOM — те же модули выпуска, каждый со своим purl и лицензией, и libXray с Xray-core внутри.
        val bom = JSONObject(context.assets.open(OpenComponents.SBOM).bufferedReader().use { it.readText() })
        assertEquals("CycloneDX", bom.getString("bomFormat"))
        val listed = bom.getJSONArray("components").let { array -> (0 until array.length()).map { array.getJSONObject(it) } }
        val purls = listed.map { it.getString("purl") }.toSet()
        for (module in components.flatMap { it.modules }) {
            val (key, version) = module.split(' ')
            assertTrue("pkg:maven/${key.replace(':', '/')}@$version" in purls, "$module в SBOM")
        }

        val bomXray = listed.single { it.getString("name") == "libXray" }
        assertEquals("pkg:github/XTLS/libXray@${libxray.version}", bomXray.getString("purl"))
        assertEquals("Xray-core", bomXray.getJSONArray("components").getJSONObject(0).getString("name"))
    }

    @Test
    fun `settings open the licenses and a component shows its full license`() {
        compose.setContent { PgTheme { SettingsScreen("26.10.21", {}, {}, TunnelSettings(), {}) } }
        compose.onNodeWithTag("settings.licenses").performScrollTo().performClick()
        compose.onNodeWithTag("licenses").assertIsDisplayed()
        compose.onNodeWithTag("licenses.Xray-core").performScrollTo().performClick()
        compose.onNodeWithTag("license.label").assertTextEquals("MPL-2.0")
        compose.onNodeWithTag("license.copyright").assertTextEquals("Project X (XTLS) contributors")
        compose.onNodeWithTag("license.url").assertTextEquals("https://github.com/XTLS/Xray-core")
        compose.onNodeWithTag("license.text.MPL-2.0").performScrollTo().assertTextContains("Mozilla Public License", substring = true)

        compose.onNodeWithContentDescription(context.getString(R.string.common_back)).performScrollTo().performClick()
        compose.onNodeWithTag("licenses.ZXing").performScrollTo().performClick()
        compose.onNodeWithTag("license.modules").performScrollTo().assertTextContains("com.google.zxing:core", substring = true)
        compose.onNodeWithTag("license.text.Apache-2.0").performScrollTo().assertTextContains("Apache License", substring = true)

        compose.onNodeWithContentDescription(context.getString(R.string.common_back)).performScrollTo().performClick()
        compose.onNodeWithContentDescription(context.getString(R.string.common_back)).performScrollTo().performClick()
        compose.onNodeWithTag("settings.licenses").performScrollTo().assertIsDisplayed()
    }
}

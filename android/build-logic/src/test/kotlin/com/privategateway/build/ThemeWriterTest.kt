package com.privategateway.build

import org.junit.jupiter.api.Test
import java.io.File
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue
import kotlin.test.fail

class ThemeWriterTest {
    private val repo = File(System.getProperty("repo"))
    private val tokensJson = repo.resolve("shared/specifications/design-tokens.json").readText()
    private val iconsXaml = repo.resolve("desktop/src/Client.UI/Themes/Icons.axaml").readText()
    private val files = ThemeWriter.write(tokensJson, iconsXaml)
    private val tokens = files.getValue("com/privategateway/client/ui/theme/Tokens.kt")
    private val icons = files.getValue("com/privategateway/client/ui/theme/Icons.kt")

    @Test
    fun `every color matches the desktop theme`() {
        // Tokens.axaml собран scripts/gen-tokens.cs из тех же токенов: цвет на Android — тот же ARGB, что на десктопе.
        val desktop = Regex("""<Color x:Key="PgColor\.([A-Za-z0-9.]+)">#([0-9A-F]{8})</Color>""")
            .findAll(repo.resolve("desktop/src/Client.UI/Themes/Tokens.axaml").readText())
            .associate { it.groupValues[1] to it.groupValues[2] }
        val json = SharedSources.json(tokensJson)
        var checked = 0
        fun walk(node: Map<*, *>, path: List<String>) {
            for ((key, value) in node) {
                val next = path + SharedSources.pascal(key as String)
                if (value is Map<*, *>) {
                    walk(value, next)
                    continue
                }

                if (key == "label" || value !is String || !(value.startsWith("#") || value.startsWith("rgba"))) {
                    continue
                }

                val name = next.joinToString(".")
                val expected = desktop[name] ?: fail("На десктопе нет PgColor.$name")
                assertEquals("0x$expected", SharedSources.kotlinColor(SharedSources.argb(value)), name)
                assertTrue(tokens.contains("val ${next.last()} = Color(0x$expected)"), name)
                checked++
            }
        }

        walk(json["color"] as Map<*, *>, emptyList())
        walk(json["state"] as Map<*, *>, listOf("State"))
        walk(json["glass"] as Map<*, *>, listOf("Glass"))
        assertEquals(desktop.size, checked)
    }

    @Test
    fun `styles carry size weight tracking and line height`() {
        assertTrue(tokens.contains("val H1 = TextStyle(fontSize = 22.sp, fontWeight = FontWeight(700), letterSpacing = (-0.015).em)"))
        assertTrue(tokens.contains("val Body = TextStyle(fontSize = 14.sp, fontWeight = FontWeight(400), lineHeight = 1.45.em)"))
        assertTrue(tokens.contains("val Overline = TextStyle(fontSize = 11.sp, fontWeight = FontWeight(600), letterSpacing = 0.06.em)"))
        assertTrue(tokens.contains("const val OVERLINE_UPPERCASE = true"))
        assertTrue(tokens.contains("val NavMobile = TextStyle(fontSize = 10.5.sp, fontWeight = FontWeight(500))"))
        assertTrue(tokens.contains("val NavMobileActive = TextStyle(fontSize = 10.5.sp, fontWeight = FontWeight(600))"))
        assertTrue(tokens.contains("val Mono = TextStyle(fontSize = 13.sp, fontWeight = FontWeight(500), fontFamily = FontFamily.Monospace)"))
    }

    @Test
    fun `sizes become dp and css columns are left out`() {
        assertTrue(tokens.contains("val WindowSideMobile = 16.dp"))
        assertTrue(tokens.contains("val Card = 19.dp"))
        assertTrue(tokens.contains("const val ROUND_PERCENT = 50"))
        assertTrue(tokens.contains("val Height = 68.dp"))
        assertTrue(tokens.contains("val TouchTarget = 44.dp"))
        assertTrue(tokens.contains("const val SATURATE = 1.7f"))
        assertTrue(tokens.contains("val Card = listOf(PgShadowLayer(0.dp, 1.dp, 2.dp, Color(0x0A0F172A)), PgShadowLayer(0.dp, 8.dp, 24.dp, Color(0x0F0F172A)))"))
        assertTrue(tokens.contains("val Sheet = listOf(PgShadowLayer(0.dp, (-12).dp, 40.dp, Color(0x330F172A)))"))
        assertFalse(tokens.contains("NavColumns"))
        assertFalse(tokens.contains("label"), "подпись состояния — из текстов")
    }

    @Test
    fun `state colors come without labels`() {
        assertTrue(tokens.contains("object Connected {\n        val Ring = Color(0xFF16A34A)"))
        assertTrue(tokens.contains("val Spinner = Color(0xFF2563EB)"))
    }

    @Test
    fun `every icon of the mockup is a path constant`() {
        val source = SharedSources.icons(iconsXaml)
        assertEquals(Regex("<StreamGeometry ").findAll(iconsXaml).count(), source.size)
        for ((key, path) in source) {
            assertTrue(icons.contains("const val ${SharedSources.pascal(key)} = \"$path\""), key)
        }

        assertTrue(icons.contains("const val NavServersDetail = \"M7.2 7.5h1M7.2 16.5h1M11.5 7.5h5M11.5 16.5h5\""))
    }

    @Test
    fun `names are unique in every object and braces are balanced`() {
        // Повтор имени в одном объекте или лишняя скобка — ошибка компиляции приложения, которую здесь не собрать.
        for (code in listOf(tokens, icons)) {
            val scopes = ArrayDeque<MutableSet<String>>().apply { addLast(mutableSetOf()) }
            for (line in code.lines()) {
                val name = Regex("""^\s*(?:object|val|const val|data class)\s+([A-Za-z0-9_]+)""").find(line)?.groupValues?.get(1)
                if (name != null) {
                    assertTrue(scopes.last().add(name), "имя $name повторяется: $line")
                }

                if (line.trimEnd().endsWith("{")) {
                    scopes.addLast(mutableSetOf())
                }

                if (line.trim() == "}") {
                    scopes.removeLast()
                }
            }

            assertEquals(1, scopes.size)
        }
    }
}

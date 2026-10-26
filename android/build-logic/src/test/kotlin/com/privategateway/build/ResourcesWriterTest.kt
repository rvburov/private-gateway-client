package com.privategateway.build

import org.gradle.api.GradleException
import org.junit.jupiter.api.Test
import org.w3c.dom.Element
import java.io.ByteArrayInputStream
import java.io.File
import javax.xml.parsers.DocumentBuilderFactory
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertTrue

class ResourcesWriterTest {
    private val repo = File(System.getProperty("repo"))
    private val localization = repo.resolve("shared/specifications/localization")
    private val russian = localization.resolve("ru.json").readText()
    private val english = localization.resolve("en.json").readText()
    private val files = ResourcesWriter.write(
        repo.resolve("shared/specifications/design-tokens.json").readText(),
        repo.resolve("desktop/src/Client.UI/Themes/Icons.axaml").readText(),
        russian,
        english,
    )

    @Test
    fun `every text comes back from strings xml as written`() {
        // Обратное чтение по правилам Android: экранирование, подстановки и «%» не портят ни одного текста RU и EN.
        for ((json, path) in listOf(russian to "values-ru/strings.xml", english to "values/strings.xml")) {
            val texts = SharedSources.texts(json)
            val strings = read(files.getValue(path))
            assertEquals(texts.size, strings.size)
            val names = ResourcesWriter.names(texts.keys)
            for ((key, text) in texts) {
                val (value, literal) = strings.getValue(names.getValue(key))
                assertEquals(text, android(value, !literal), "$path: $key")
            }
        }
    }

    @Test
    fun `placeholders become numbered arguments`() {
        assertEquals("Пинг %1\$s мс" to false, ResourcesWriter.text("Пинг {0} мс"))
        assertEquals("%2\$s из %1\$s, 50%%" to false, ResourcesWriter.text("{1} из {0}, 50%"))
        assertEquals("100%" to true, ResourcesWriter.text("100%"))
    }

    @Test
    fun `android and xml special characters are escaped`() {
        assertEquals("it\\'s \\\"ok\\\" \\\\ a\\nb" to false, ResourcesWriter.text("it's \"ok\" \\ a\nb"))
        assertEquals("a &amp; b &lt;c&gt;" to false, ResourcesWriter.text("a & b <c>"))
        assertEquals("\\@home" to false, ResourcesWriter.text("@home"))
        assertEquals("\\?x ?" to false, ResourcesWriter.text("?x ?"))
        assertEquals("\\u0020a\\u0020\\u0020b" to false, ResourcesWriter.text(" a  b"))
    }

    @Test
    fun `keys turn into resource names without collisions`() {
        val names = ResourcesWriter.names(listOf("status.connected.title", "error.PGC-SVC-001"))
        assertEquals("status_connected_title", names["status.connected.title"])
        assertEquals("error_PGC_SVC_001", names["error.PGC-SVC-001"])
        assertFailsWith<GradleException> { ResourcesWriter.names(listOf("a.b", "a-b")) }
        assertFailsWith<GradleException> { ResourcesWriter.names(listOf("1st.key")) }
    }

    @Test
    fun `different keys in russian and english stop the build`() {
        assertFailsWith<GradleException> {
            ResourcesWriter.write(
                repo.resolve("shared/specifications/design-tokens.json").readText(),
                repo.resolve("desktop/src/Client.UI/Themes/Icons.axaml").readText(),
                """{"a": "А", "b": "Б"}""",
                """{"a": "A"}""",
            )
        }
    }

    @Test
    fun `icons and colors are well formed resources`() {
        val shield = SharedSources.icons(repo.resolve("desktop/src/Client.UI/Themes/Icons.axaml").readText()).getValue("Shield")
        for (path in listOf("drawable/pg_launcher_foreground.xml", "drawable/pg_launcher_monochrome.xml", "drawable/pg_notification.xml")) {
            val root = parse(files.getValue(path))
            assertEquals("vector", root.tagName)
            assertTrue(files.getValue(path).contains("android:pathData=\"$shield\""), path)
        }

        assertTrue(files.getValue("drawable/pg_launcher_foreground.xml").contains("android:fillColor=\"#FF16A34A\""))
        assertEquals("adaptive-icon", parse(files.getValue("mipmap-anydpi/pg_launcher.xml")).tagName)
        assertEquals("locale-config", parse(files.getValue("xml/pg_locales.xml")).tagName)
        assertTrue(files.getValue("values/pg_colors.xml").contains("<color name=\"pg_window_background\">#FFEEF2F8</color>"))
    }

    /** strings.xml → имя → (текст как в файле, formatted="false"). */
    private fun read(xml: String): Map<String, Pair<String, Boolean>> {
        val nodes = parse(xml).getElementsByTagName("string")
        return (0 until nodes.length).associate {
            val element = nodes.item(it) as Element
            element.getAttribute("name") to (element.textContent to (element.getAttribute("formatted") == "false"))
        }
    }

    private fun parse(xml: String): Element =
        DocumentBuilderFactory.newInstance().newDocumentBuilder().parse(ByteArrayInputStream(xml.toByteArray())).documentElement

    /** Как Android читает строку: снимает экранирование, «%1$s» → «{0}», «%%» → «%» у строк с форматом. */
    private fun android(value: String, formatted: Boolean): String {
        val out = StringBuilder()
        var index = 0
        while (index < value.length) {
            val c = value[index]
            if (c == '\\' && index + 1 < value.length) {
                val next = value[index + 1]
                when (next) {
                    'n' -> out.append('\n')
                    't' -> out.append('\t')
                    'u' -> {
                        out.append(value.substring(index + 2, index + 6).toInt(16).toChar())
                        index += 4
                    }

                    else -> out.append(next)
                }

                index += 2
                continue
            }

            out.append(c)
            index++
        }

        if (!formatted) {
            return out.toString()
        }

        return Regex("""%(\d+)\${'$'}s|%%""").replace(out) { if (it.value == "%%") "%" else "{${it.groupValues[1].toInt() - 1}}" }
    }
}

package com.privategateway.build

import com.privategateway.build.SharedSources.argb
import com.privategateway.build.SharedSources.resourceColor
import org.gradle.api.GradleException

/**
 * Ресурсы Android из общих файлов:
 *   localization/en.json → values/strings.xml (язык по умолчанию: телефон на любом языке, кроме русского, — EN),
 *   localization/ru.json → values-ru/strings.xml, список языков для системных настроек (Android 13+) — xml/pg_locales.xml;
 *   ключ «status.connected.title» → R.string.status_connected_title («.» и «-» → «_»), подстановка «{0}» → «%1$s»;
 *   иконка запуска, значок уведомления и цвет фона иконки — из контура щита (Icons.axaml) и цветов токенов.
 */
internal object ResourcesWriter {
    private val placeholder = Regex("""\{(\d+)\}""")

    fun write(tokensJson: String, iconsXaml: String, russianJson: String, englishJson: String): Map<String, String> {
        val russian = SharedSources.texts(russianJson)
        val english = SharedSources.texts(englishJson)
        if (russian.keys != english.keys) {
            val missing = (russian.keys - english.keys).map { "en: $it" } + (english.keys - russian.keys).map { "ru: $it" }
            throw GradleException("Ключи RU и EN разные — нет ${missing.take(10).joinToString()}")
        }

        val names = names(russian.keys)
        val tokens = SharedSources.json(tokensJson)
        val icons = SharedSources.icons(iconsXaml)
        return linkedMapOf(
            "values/strings.xml" to strings(english, names),
            "values-ru/strings.xml" to strings(russian, names),
            "xml/pg_locales.xml" to locales(),
            "values/pg_colors.xml" to colors(tokens),
            "drawable/pg_launcher_foreground.xml" to launcherForeground(tokens, icons),
            "drawable/pg_launcher_monochrome.xml" to outline(icons, "108dp", 2.5, 24.0),
            "drawable/pg_notification.xml" to outline(icons, "24dp", 1.0, 0.0),
            "mipmap-anydpi/pg_launcher.xml" to launcher(),
        )
    }

    /** Имя ресурса из ключа; два ключа с одним именем — ошибка сборки, а не молча потерянный текст. */
    fun names(keys: Collection<String>): Map<String, String> {
        val names = LinkedHashMap<String, String>()
        val owners = HashMap<String, String>()
        for (key in keys) {
            val name = key.replace('.', '_').replace('-', '_')
            if (!Regex("^[A-Za-z][A-Za-z0-9_]*$").matches(name)) {
                throw GradleException("Ключ «$key» не превращается в имя ресурса Android")
            }

            owners.put(name, key)?.let { throw GradleException("Ключи «$it» и «$key» дают одно имя ресурса $name") }
            names[key] = name
        }

        return names
    }

    /** Текст для strings.xml: экранирование Android и XML; с подстановками — формат с номерами, «%» удваивается. */
    fun text(value: String): Pair<String, Boolean> {
        val formatted = placeholder.containsMatchIn(value)
        val source = if (formatted) value.replace("%", "%%") else value
        val keepSpaces = source != source.trim() || source.contains("  ")
        val out = StringBuilder()
        for ((index, c) in source.withIndex()) {
            when {
                c == '\\' -> out.append("\\\\")
                c == '"' -> out.append("\\\"")
                c == '\'' -> out.append("\\'")
                c == '\n' -> out.append("\\n")
                c == '\t' -> out.append("\\t")
                c == '&' -> out.append("&amp;")
                c == '<' -> out.append("&lt;")
                c == '>' -> out.append("&gt;")
                index == 0 && (c == '@' || c == '?') -> out.append('\\').append(c)
                keepSpaces && c == ' ' -> out.append("\\u0020")
                else -> out.append(c)
            }
        }

        val text = if (formatted) placeholder.replace(out) { "%${it.groupValues[1].toInt() + 1}\$s" } else out.toString()

        // Без подстановок «%» — просто знак: formatted="false", чтобы проверка формата Android его не трогала.
        return text to (!formatted && value.contains('%'))
    }

    private fun strings(texts: Map<String, String>, names: Map<String, String>): String {
        val out = StringBuilder()
        out.append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n")
        out.append("<!-- Сгенерировано из shared/specifications/localization при сборке — руками не править. -->\n")
        out.append("<resources>\n")
        for ((key, value) in texts) {
            val (text, literal) = text(value)
            val attributes = if (literal) " formatted=\"false\"" else ""
            out.append("    <string name=\"${names.getValue(key)}\"$attributes>$text</string>\n")
        }

        out.append("</resources>\n")
        return out.toString()
    }

    private fun locales() = """
        <?xml version="1.0" encoding="utf-8"?>
        <locale-config xmlns:android="http://schemas.android.com/apk/res/android">
            <locale android:name="en" />
            <locale android:name="ru" />
        </locale-config>
    """.trimIndent() + "\n"

    private fun colors(tokens: Map<String, Any?>): String {
        val window = resourceColor(argb(path(tokens, "color.bg.window")))
        return """
            <?xml version="1.0" encoding="utf-8"?>
            <resources>
                <color name="pg_launcher_background">$window</color>
                <color name="pg_window_background">$window</color>
            </resources>
        """.trimIndent() + "\n"
    }

    /** Цветная иконка запуска: зеленый щит с белой галочкой, как app.svg десктопа; в безопасной зоне 66 из 108 dp. */
    private fun launcherForeground(tokens: Map<String, Any?>, icons: Map<String, String>): String {
        val shield = resourceColor(argb(path(tokens, "state.connected.ring")))
        val mark = resourceColor(argb(path(tokens, "color.surface.solid")))
        return """
            <?xml version="1.0" encoding="utf-8"?>
            <vector xmlns:android="http://schemas.android.com/apk/res/android"
                android:width="108dp"
                android:height="108dp"
                android:viewportWidth="108"
                android:viewportHeight="108">
                <group android:scaleX="2.5" android:scaleY="2.5" android:translateX="24" android:translateY="24">
                    <path
                        android:pathData="${icons.getValue("Shield")}"
                        android:fillColor="$shield"
                        android:strokeColor="$shield"
                        android:strokeWidth="2"
                        android:strokeLineJoin="round"
                        android:strokeLineCap="round" />
                    <path
                        android:pathData="${icons.getValue("Mark.Ok")}"
                        android:strokeColor="$mark"
                        android:strokeWidth="2.6"
                        android:strokeLineJoin="round"
                        android:strokeLineCap="round" />
                </group>
            </vector>
        """.trimIndent() + "\n"
    }

    /** Одноцветный щит контуром — значок уведомления и иконка для тем Android 13+: цвет задает система. */
    private fun outline(icons: Map<String, String>, size: String, scale: Double, shift: Double): String {
        val viewport = if (size == "108dp") "108" else "24"
        return """
            <?xml version="1.0" encoding="utf-8"?>
            <vector xmlns:android="http://schemas.android.com/apk/res/android"
                android:width="$size"
                android:height="$size"
                android:viewportWidth="$viewport"
                android:viewportHeight="$viewport">
                <group android:scaleX="$scale" android:scaleY="$scale" android:translateX="$shift" android:translateY="$shift">
                    <path
                        android:pathData="${icons.getValue("Shield")}"
                        android:strokeColor="#FFFFFFFF"
                        android:strokeWidth="2"
                        android:strokeLineJoin="round"
                        android:strokeLineCap="round" />
                    <path
                        android:pathData="${icons.getValue("Mark.Ok")}"
                        android:strokeColor="#FFFFFFFF"
                        android:strokeWidth="2"
                        android:strokeLineJoin="round"
                        android:strokeLineCap="round" />
                </group>
            </vector>
        """.trimIndent() + "\n"
    }

    private fun launcher() = """
        <?xml version="1.0" encoding="utf-8"?>
        <adaptive-icon xmlns:android="http://schemas.android.com/apk/res/android">
            <background android:drawable="@color/pg_launcher_background" />
            <foreground android:drawable="@drawable/pg_launcher_foreground" />
            <monochrome android:drawable="@drawable/pg_launcher_monochrome" />
        </adaptive-icon>
    """.trimIndent() + "\n"

    /** Значение по пути «color.bg.window». */
    private fun path(tokens: Map<String, Any?>, path: String): String {
        var node: Any? = tokens
        for (part in path.split('.')) {
            node = (node as? Map<*, *>)?.get(part)
        }

        return node as? String ?: throw GradleException("В токенах нет $path")
    }
}

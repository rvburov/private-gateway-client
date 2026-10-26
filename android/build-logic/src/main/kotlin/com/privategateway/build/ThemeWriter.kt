package com.privategateway.build

import com.privategateway.build.SharedSources.argb
import com.privategateway.build.SharedSources.kotlinColor
import com.privategateway.build.SharedSources.number
import com.privategateway.build.SharedSources.pascal
import com.privategateway.build.SharedSources.quoted
import org.gradle.api.GradleException

/**
 * Тема Compose из токенов макета — как Tokens.axaml у десктопа (scripts/gen-tokens.cs), с теми же именами:
 *   color.bg.window        → PgColor.Bg.Window (Color);
 *   state.connected.ring   → PgState.Connected.Ring (подпись состояния — из текстов, не отсюда);
 *   type.h1                → PgType.H1 (TextStyle: размер, начертание, трекинг и высота строки в em);
 *   space.card             → PgSpace.Card, radius.card → PgRadius.Card, layout.island.height → PgLayout.Island.Height (Dp);
 *   shadow.card            → PgShadow.Card (слои PgShadowLayer).
 * Иконки — контуры SVG из Icons.axaml: Icon.Nav.Home → PgIcons.NavHome.
 */
internal object ThemeWriter {
    const val PACKAGE = "com.privategateway.client.ui.theme"

    private const val HEADER = "// Сгенерировано плагином privategateway.shared-resources (android/build-logic) при сборке — руками не\n" +
        "// править и в git не хранить.\n"

    fun write(tokensJson: String, iconsXaml: String): Map<String, String> {
        val tokens = SharedSources.json(tokensJson)
        val folder = PACKAGE.replace('.', '/')
        return linkedMapOf(
            "$folder/Tokens.kt" to tokens(tokens),
            "$folder/Icons.kt" to icons(SharedSources.icons(iconsXaml)),
        )
    }

    private fun tokens(tokens: Map<String, Any?>): String {
        val out = Code()
        out.line(HEADER.trimEnd())
        out.line("// Источник: shared/specifications/design-tokens.json, версия ${tokens["version"]}.")
        out.line("package $PACKAGE")
        out.line()
        listOf(
            "androidx.compose.ui.graphics.Color",
            "androidx.compose.ui.text.TextStyle",
            "androidx.compose.ui.text.font.FontFamily",
            "androidx.compose.ui.text.font.FontWeight",
            "androidx.compose.ui.unit.Dp",
            "androidx.compose.ui.unit.dp",
            "androidx.compose.ui.unit.em",
            "androidx.compose.ui.unit.sp",
        ).forEach { out.line("import $it") }
        out.line()
        out.line("/** Слой тени (shadow.*): смещение, размытие и цвет — как box-shadow макета. */")
        out.line("data class PgShadowLayer(val x: Dp, val y: Dp, val blur: Dp, val color: Color)")
        out.line()

        out.line("/** Цвета макета (color.*). */")
        out.block("object PgColor") { colors(out, section(tokens, "color"), "color") }
        out.line()

        out.line("/** Цвета состояний защиты (state.*): кольцо кнопки, иконка, свечение. */")
        out.block("object PgState") {
            for ((state, value) in section(tokens, "state")) {
                out.block("object ${pascal(state)}") {
                    for ((key, css) in value as Map<*, *>) {
                        if (key != "label") {
                            out.line("val ${pascal(key as String)} = Color(${kotlinColor(argb(css as String))})")
                        }
                    }
                }
            }
        }
        out.line()

        out.line("/** Шрифты Android (font.android): системные, в приложение не встраиваются. */")
        out.block("object PgFont") {
            val android = section(section(tokens, "font"), "android")
            out.line("const val UI = ${quoted(android["ui"] as String)}")
            out.line("const val MONO = ${quoted(android["mono"] as String)}")
        }
        out.line()

        out.line("/** Начертания текста (type.*). */")
        out.block("object PgType") { types(out, section(tokens, "type")) }
        out.line()

        out.line("/** Отступы (space.*). */")
        out.block("object PgSpace") { dps(out, section(tokens, "space"), "space") }
        out.line()

        out.line("/** Скругления (radius.*); round — круг, процент от стороны. */")
        out.block("object PgRadius") {
            for ((key, value) in section(tokens, "radius")) {
                if (value is String && value.endsWith("%")) {
                    out.line("const val ${key.uppercase()}_PERCENT = ${value.removeSuffix("%")}")
                } else {
                    out.line("val ${pascal(key)} = ${number(value, "radius.$key")}.dp")
                }
            }
        }
        out.line()

        out.line("/** Тени (shadow.*). */")
        out.block("object PgShadow") {
            for ((key, value) in section(tokens, "shadow")) {
                val layers = (value as List<*>).joinToString(", ") { shadow(it as String) }
                out.line("val ${pascal(key)} = listOf($layers)")
            }
        }
        out.line()

        out.line("/** Стекло (glass.*): заливка, размытие и запасной цвет без размытия. */")
        out.block("object PgGlass") {
            val glass = section(tokens, "glass")
            out.line("val Fill = Color(${kotlinColor(argb(glass["fill"] as String))})")
            out.line("val Blur = ${number(glass["blur"], "glass.blur")}.dp")
            out.line("const val SATURATE = ${number(glass["saturate"], "glass.saturate")}f")
            out.line("val Fallback = Color(${kotlinColor(argb(glass["fallback"] as String))})")
        }
        out.line()

        out.line("/** Размеры раскладки (layout.*); колонки сетки CSS — только в макете. */")
        out.block("object PgLayout") { dps(out, section(tokens, "layout"), "layout") }
        return out.toString()
    }

    private fun colors(out: Code, node: Map<String, Any?>, path: String) {
        for ((key, value) in node) {
            when (value) {
                is Map<*, *> -> out.block("object ${pascal(key)}") {
                    @Suppress("UNCHECKED_CAST")
                    colors(out, value as Map<String, Any?>, "$path.$key")
                }

                is String -> out.line("val ${pascal(key)} = Color(${kotlinColor(argb(value))})")
                else -> throw GradleException("Цвет $path.$key — не строка")
            }
        }
    }

    /** Числа — в Dp, вложенные словари — во вложенные объекты, строки (колонки CSS) пропускаются. */
    private fun dps(out: Code, node: Map<String, Any?>, path: String) {
        for ((key, value) in node) {
            when (value) {
                is Map<*, *> -> out.block("object ${pascal(key)}") {
                    @Suppress("UNCHECKED_CAST")
                    dps(out, value as Map<String, Any?>, "$path.$key")
                }

                is Number -> out.line("val ${pascal(key)} = ${number(value, "$path.$key")}.dp")
                is String -> Unit
                else -> throw GradleException("Токен $path.$key не разобран")
            }
        }
    }

    private fun types(out: Code, node: Map<String, Any?>) {
        for ((key, value) in node) {
            val style = value as Map<*, *>
            val name = pascal(key)
            val mono = style["family"] == "mono"
            out.line("val $name = ${textStyle(style, style["size"], style["weight"], mono, "type.$key")}")
            style["weightActive"]?.let { out.line("val ${name}Active = ${textStyle(style, style["size"], it, mono, "type.$key")}") }
            style["sizeMobile"]?.let {
                out.line("val ${name}Mobile = ${textStyle(style, it, style["weight"], mono, "type.$key")}")
                style["weightActive"]?.let { active ->
                    out.line("val ${name}MobileActive = ${textStyle(style, it, active, mono, "type.$key")}")
                }
            }

            if (style["uppercase"] == true) {
                out.line("const val ${name.uppercase()}_UPPERCASE = true")
            }
        }
    }

    private fun textStyle(style: Map<*, *>, size: Any?, weight: Any?, mono: Boolean, path: String): String {
        val parts = mutableListOf("fontSize = ${number(size, "$path.size")}.sp", "fontWeight = FontWeight(${number(weight, "$path.weight")})")
        style["tracking"]?.let { parts.add("letterSpacing = ${em(number(it, "$path.tracking"))}") }
        style["lineHeight"]?.let { parts.add("lineHeight = ${em(number(it, "$path.lineHeight"))}") }
        if (mono) {
            parts.add("fontFamily = FontFamily.Monospace")
        }

        return "TextStyle(${parts.joinToString(", ")})"
    }

    private fun em(value: String) = if (value.startsWith("-")) "($value).em" else "$value.em"

    /** «0 8 24 rgba(15, 23, 42, 0.06)» → PgShadowLayer(0.dp, 8.dp, 24.dp, Color(…)). */
    private fun shadow(css: String): String {
        val match = Regex("""^(-?\d+)\s+(-?\d+)\s+(\d+)\s+(rgba\(.+\))$""").find(css) ?: throw GradleException("Тень не разобрана: «$css»")
        val (x, y, blur, color) = match.destructured
        return "PgShadowLayer(${dp(x)}, ${dp(y)}, ${dp(blur)}, Color(${kotlinColor(argb(color))}))"
    }

    private fun dp(value: String) = if (value.startsWith("-")) "($value).dp" else "$value.dp"

    private fun icons(icons: Map<String, String>): String {
        val out = Code()
        out.line(HEADER.trimEnd())
        out.line("// Источник: desktop/src/Client.UI/Themes/Icons.axaml — контуры 24×24 из макета, рисуются линией.")
        out.line("package $PACKAGE")
        out.line()
        out.line("/** Контуры иконок (данные пути SVG): Icon.Nav.Home → NavHome. */")
        out.block("object PgIcons") {
            for ((key, path) in icons) {
                out.line("const val ${pascal(key)} = ${quoted(path)}")
            }
        }
        return out.toString()
    }

    @Suppress("UNCHECKED_CAST")
    private fun section(node: Map<String, Any?>, key: String): Map<String, Any?> =
        node[key] as? Map<String, Any?> ?: throw GradleException("В токенах нет раздела $key")

    /** Текст с отступами по вложенности. */
    private class Code {
        private val text = StringBuilder()
        private var depth = 0

        fun line(value: String = "") {
            if (value.isNotEmpty()) {
                text.append("    ".repeat(depth))
            }

            text.append(value).append('\n')
        }

        fun block(header: String, body: () -> Unit) {
            line("$header {")
            depth++
            body()
            depth--
            line("}")
        }

        override fun toString() = text.toString()
    }
}

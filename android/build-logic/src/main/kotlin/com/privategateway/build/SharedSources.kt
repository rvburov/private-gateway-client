package com.privategateway.build

import groovy.json.JsonSlurper
import org.gradle.api.GradleException
import org.w3c.dom.Element
import java.io.ByteArrayInputStream
import java.math.BigDecimal
import java.util.Locale
import javax.xml.XMLConstants
import javax.xml.parsers.DocumentBuilderFactory

/** Чтение общих файлов: JSON токенов и текстов, контуры иконок, цвета CSS. */
internal object SharedSources {
    /** JSON в дерево: словари с порядком ключей как в файле, числа — [BigDecimal] или [Int]. */
    fun json(text: String): Map<String, Any?> {
        @Suppress("UNCHECKED_CAST")
        return JsonSlurper().parseText(text) as? Map<String, Any?> ?: throw GradleException("Ожидался объект JSON")
    }

    /** Тексты: плоский словарь «ключ → строка». */
    fun texts(text: String): Map<String, String> = json(text).mapValues { (key, value) ->
        value as? String ?: throw GradleException("Текст «$key» — не строка")
    }

    /** Иконки макета: ключ без «Icon.» («Nav.Home») → контур SVG, в порядке файла. */
    fun icons(xaml: String): Map<String, String> {
        val factory = DocumentBuilderFactory.newInstance()
        factory.setFeature(XMLConstants.FEATURE_SECURE_PROCESSING, true)
        factory.setFeature("http://apache.org/xml/features/disallow-doctype-decl", true)
        factory.isNamespaceAware = true
        val document = factory.newDocumentBuilder().parse(ByteArrayInputStream(xaml.toByteArray(Charsets.UTF_8)))
        val nodes = document.getElementsByTagName("StreamGeometry")
        val icons = LinkedHashMap<String, String>()
        for (index in 0 until nodes.length) {
            val element = nodes.item(index) as Element
            val key = element.getAttributeNS("http://schemas.microsoft.com/winfx/2006/xaml", "Key")
            if (!key.startsWith("Icon.")) {
                throw GradleException("Иконка без префикса Icon.: «$key»")
            }

            icons[key.removePrefix("Icon.")] = element.textContent.trim()
        }

        return icons
    }

    /** «#RRGGBB» или «rgba(r, g, b, a)» → ARGB одним числом; альфа — с округлением как у десктопа (от нуля). */
    fun argb(css: String): Long {
        val hex = Regex("^#([0-9A-Fa-f]{6})$").find(css)
        if (hex != null) {
            return 0xFF000000L or hex.groupValues[1].toLong(16)
        }

        val rgba = Regex("""^rgba\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*([0-9.]+)\s*\)$""").find(css)
            ?: throw GradleException("Цвет не разобран: «$css»")
        val (red, green, blue, opacity) = rgba.destructured
        val alpha = BigDecimal(opacity).multiply(BigDecimal(255)).setScale(0, java.math.RoundingMode.HALF_UP).toLong()
        return (alpha shl 24) or (red.toLong() shl 16) or (green.toLong() shl 8) or blue.toLong()
    }

    /** ARGB → «0xAARRGGBB» для Kotlin и «#AARRGGBB» для ресурсов Android. */
    fun kotlinColor(argb: Long) = "0x%08X".format(Locale.ROOT, argb)

    fun resourceColor(argb: Long) = "#%08X".format(Locale.ROOT, argb)

    /** «windowSideMobile» → «WindowSideMobile», «Nav.Servers.Detail» → «NavServersDetail». */
    fun pascal(name: String): String = name.split('.', '-', '_').filter { it.isNotEmpty() }.joinToString("") {
        it.replaceFirstChar { c -> c.uppercaseChar() }
    }

    /** Число токена как текст Kotlin: «16», «10.5», «-0.02». */
    fun number(value: Any?, path: String): String = when (value) {
        is Int, is Long -> value.toString()
        is BigDecimal -> value.stripTrailingZeros().toPlainString()
        is Number -> BigDecimal(value.toString()).stripTrailingZeros().toPlainString()
        else -> throw GradleException("Токен $path — не число: $value")
    }

    /** Строка Kotlin в кавычках. */
    fun quoted(text: String) = "\"" + text.replace("\\", "\\\\").replace("\"", "\\\"").replace("$", "\\$") + "\""
}

package com.privategateway.client.core.links

/** Строка разобранного текста: номер (с 1) и результат разбора. */
data class ImportLine(val number: Int, val result: LinkResult)

/** Разобранный текст: формат ([ImportParser.formats]) и строки по порядку. */
data class ImportBatch(val format: String, val lines: List<ImportLine>) {
    val found: Int get() = lines.size

    val failed: Int get() = lines.count { !it.result.isOk }
}

/**
 * Текст из буфера, файла или подписки — как на десктопе (`ImportParser`): ссылки по одной в строке, такой же список в
 * base64 (обычная подписка), конфиг sing-box (JSON) или Clash/Mihomo (YAML со списком proxies). В списке ссылок пустые
 * строки и строки-комментарии («#», «//») пропускаются.
 */
object ImportParser {
    const val FORMAT_LINKS = "links"
    const val FORMAT_BASE64 = "base64"
    const val FORMAT_SINGBOX = "singbox"
    const val FORMAT_CLASH = "clash"

    /** Все форматы — для подписей на экране импорта. */
    val formats = listOf(FORMAT_LINKS, FORMAT_BASE64, FORMAT_SINGBOX, FORMAT_CLASH)

    /** Конфиг Clash: строка «proxies:» с начала строки. */
    private val clashProxies = Regex("^proxies[ \\t]*:", RegexOption.MULTILINE)

    fun parse(input: String): ImportBatch {
        var text = input.trimStart('﻿')
        val trimmed = text.trimStart()
        if (trimmed.startsWith('{') || trimmed.startsWith('[')) {
            return SingBoxImport.parse(text)
        }

        if (clashProxies.containsMatchIn(text)) {
            return ClashImport.parse(text)
        }

        var format = FORMAT_LINKS
        if (!text.contains("://")) {
            val decoded = LinkParser.decodeBase64(text)
            if (decoded != null && decoded.contains("://")) {
                format = FORMAT_BASE64
                text = decoded
            }
        }

        val lines = ArrayList<ImportLine>()
        for ((index, raw) in text.split('\n').withIndex()) {
            val line = raw.trim()
            if (line.isEmpty() || line.startsWith('#') || line.startsWith("//")) {
                continue
            }

            lines.add(ImportLine(index + 1, LinkParser.parse(line)))
        }

        return ImportBatch(format, lines)
    }
}

/**
 * Флаг страны в начале названия («🇳🇱 Нидерланды 01») — код страны для значка и название без флага: значок «NL» есть в
 * макете, а цветные флаги шрифт рисует не везде.
 */
object CountryFlag {
    private const val REGIONAL_A = 0x1F1E6
    private const val REGIONAL_Z = 0x1F1FF

    /** Код страны (пусто — флага нет) и название без флага. */
    fun split(name: String): Pair<String, String> {
        val trimmed = name.trim()
        val points = trimmed.codePoints().toArray()
        if (points.size >= 2 && isRegional(points[0]) && isRegional(points[1])) {
            val code = "${'A' + (points[0] - REGIONAL_A)}${'A' + (points[1] - REGIONAL_A)}"
            val rest = String(points, 2, points.size - 2)
            return code to rest.trim()
        }

        return "" to trimmed
    }

    private fun isRegional(point: Int) = point in REGIONAL_A..REGIONAL_Z
}

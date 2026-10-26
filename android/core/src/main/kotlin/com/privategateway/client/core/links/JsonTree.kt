package com.privategateway.client.core.links

import java.util.IdentityHashMap

/**
 * JSON в дерево: словарь — [Map] (порядок ключей сохраняется), список — [List], строка — [String], число — [JsonNumber]
 * (его текст как есть: порт в vmess и sing-box бывает числом), true/false — [Boolean], null — null. Разрешены
 * комментарии «//» и «/* */» и запятая перед закрывающей скобкой — как в конфигах sing-box. Для каждого списка
 * запоминаются строки начала его элементов: номер строки в подписке показывает, где сервер. Глубина — не больше
 * [MAX_DEPTH]: текст приходит из сети.
 */
internal object JsonTree {
    const val MAX_DEPTH = 64

    /** Число как в тексте: «443», «1.5». */
    data class JsonNumber(val text: String)

    /** Не JSON: строка ошибки — с 1. */
    class SyntaxError(val line: Int, message: String) : Exception(message)

    /** Дерево и строки начала элементов каждого списка (с 1). */
    class Parsed(val value: Any?, val itemLines: IdentityHashMap<List<*>, List<Int>>)

    fun parse(text: String): Parsed {
        val reader = Reader(text)
        reader.skip()
        val value = reader.value(0)
        reader.skip()
        if (!reader.end) {
            throw reader.error("Лишний текст после JSON")
        }

        return Parsed(value, reader.itemLines)
    }

    private class Reader(private val text: String) {
        val itemLines = IdentityHashMap<List<*>, List<Int>>()
        private var position = 0
        private var line = 1

        val end get() = position >= text.length

        fun error(message: String) = SyntaxError(line, message)

        /** Пробелы и комментарии. */
        fun skip() {
            while (position < text.length) {
                val c = text[position]
                when {
                    c == '\n' -> {
                        line++
                        position++
                    }

                    c.isWhitespace() || c == '﻿' -> position++
                    text.startsWith("//", position) -> {
                        while (position < text.length && text[position] != '\n') {
                            position++
                        }
                    }

                    text.startsWith("/*", position) -> {
                        val close = text.indexOf("*/", position + 2)
                        if (close < 0) {
                            throw error("Незакрытый комментарий")
                        }

                        line += text.substring(position, close).count { it == '\n' }
                        position = close + 2
                    }

                    else -> return
                }
            }
        }

        fun value(depth: Int): Any? {
            if (depth >= MAX_DEPTH) {
                throw error("Слишком глубокая вложенность")
            }

            if (end) {
                throw error("Нет значения")
            }

            return when (val c = text[position]) {
                '{' -> obj(depth)
                '[' -> list(depth)
                '"' -> string()
                't' -> literal("true", true)
                'f' -> literal("false", false)
                'n' -> literal("null", null)
                else -> if (c == '-' || c in '0'..'9') number() else throw error("Неожиданный символ «$c»")
            }
        }

        private fun obj(depth: Int): Map<String, Any?> {
            val result = LinkedHashMap<String, Any?>()
            position++
            while (true) {
                skip()
                if (end) {
                    throw error("Незакрытый «{»")
                }

                if (text[position] == '}') {
                    position++
                    return result
                }

                if (text[position] != '"') {
                    throw error("Нужно имя поля в кавычках")
                }

                val key = string()
                skip()
                if (end || text[position] != ':') {
                    throw error("Нужно «:»")
                }

                position++
                skip()
                result[key] = value(depth + 1)
                skip()
                if (end) {
                    throw error("Незакрытый «{»")
                }

                when (text[position]) {
                    ',' -> position++
                    '}' -> continue
                    else -> throw error("Нужна «,» или «}»")
                }
            }
        }

        private fun list(depth: Int): List<Any?> {
            val result = ArrayList<Any?>()
            val lines = ArrayList<Int>()
            itemLines[result] = lines
            position++
            while (true) {
                skip()
                if (end) {
                    throw error("Незакрытый «[»")
                }

                if (text[position] == ']') {
                    position++
                    return result
                }

                lines.add(line)
                result.add(value(depth + 1))
                skip()
                if (end) {
                    throw error("Незакрытый «[»")
                }

                when (text[position]) {
                    ',' -> position++
                    ']' -> continue
                    else -> throw error("Нужна «,» или «]»")
                }
            }
        }

        private fun string(): String {
            val result = StringBuilder()
            position++
            while (true) {
                if (end) {
                    throw error("Незакрытая строка")
                }

                when (val c = text[position++]) {
                    '"' -> return result.toString()
                    '\n' -> throw error("Перевод строки в строке")
                    '\\' -> {
                        if (end) {
                            throw error("Незакрытая строка")
                        }

                        when (val e = text[position++]) {
                            '"', '\\', '/' -> result.append(e)
                            'b' -> result.append('\b')
                            'f' -> result.append('\u000C')
                            'n' -> result.append('\n')
                            'r' -> result.append('\r')
                            't' -> result.append('\t')
                            'u' -> {
                                val code = text.substring(position, minOf(position + 4, text.length)).toIntOrNull(16)
                                if (code == null || position + 4 > text.length) {
                                    throw error("Неверный \\u")
                                }

                                result.append(code.toChar())
                                position += 4
                            }

                            else -> throw error("Неверная escape-последовательность")
                        }
                    }

                    else -> result.append(c)
                }
            }
        }

        private fun number(): JsonNumber {
            val start = position
            if (text[position] == '-') {
                position++
            }

            while (position < text.length && (text[position].isDigit() || text[position] in ".eE+-")) {
                position++
            }

            val raw = text.substring(start, position)
            if (raw.toDoubleOrNull() == null) {
                throw error("Неверное число")
            }

            return JsonNumber(raw)
        }

        private fun literal(word: String, value: Any?): Any? {
            if (!text.startsWith(word, position)) {
                throw error("Неожиданное слово")
            }

            position += word.length
            return value
        }
    }
}

/**
 * Общее для конфигов sing-box и Clash: поля узлов дерева ([JsonTree] или [YamlTree]) и сборка сервера через те же части
 * ссылки и проверки, что у ссылок ([LinkParser.fromParts]), — одинаковый сервер и одинаковые коды ошибок, откуда бы он
 * ни пришел.
 */
internal object ConfigTree {
    /** Весь текст не разобран: одна строка с ошибкой [ImportErrors.BAD_ENCODING]. */
    fun broken(format: String, line: Int) = ImportBatch(format, listOf(ImportLine(maxOf(1, line), LinkResult(null, ImportErrors.BAD_ENCODING))))

    fun fail(error: String) = LinkResult(null, error)

    /** Добавить параметр; пустое значение не пишется — как отсутствующее в ссылке. */
    fun put(query: Query, key: String, value: String?) {
        if (!value.isNullOrEmpty()) {
            query[key] = value
        }
    }

    /** Сервер из полей: секреты, адрес, порт, параметры и название — как части ссылки. */
    fun server(
        protocol: com.privategateway.client.core.servers.ProxyProtocol,
        secrets: List<String?>,
        host: String?,
        port: String?,
        query: Query,
        name: String?,
    ): LinkResult = LinkParser.fromParts(protocol, LinkParser.parts(secrets, host, port, query, name))

    /** Строковое поле; число и логическое значение — их текст. */
    fun text(node: Any?, key: String): String? = if (node is Map<*, *>) scalar(node[key]) else null

    private fun scalar(node: Any?): String? = when (node) {
        is String -> node
        is JsonTree.JsonNumber -> node.text
        is Boolean -> node.toString()
        else -> null
    }

    /** Логическое поле: true в JSON, «true», «yes», «on» или «1» в YAML. */
    fun flag(node: Any?, key: String): Boolean = text(node, key)?.lowercase() in setOf("true", "yes", "on", "1")

    fun obj(node: Any?, key: String): Map<*, *>? = (node as? Map<*, *>)?.get(key) as? Map<*, *>

    /** Список строк: массив или одна строка. */
    fun list(node: Any?, key: String): List<String> {
        val value = (node as? Map<*, *>)?.get(key)
        return if (value is List<*>) {
            value.mapNotNull { scalar(it) }
        } else {
            text(node, key)?.takeIf { it.isNotEmpty() }?.let { listOf(it) } ?: emptyList()
        }
    }

    /** Список через запятую, как в параметрах ссылки; пустой — null. */
    fun joined(node: Any?, key: String): String? = list(node, key).takeIf { it.isNotEmpty() }?.joinToString(",")

    /** Заголовок HTTP из словаря заголовков (значение — строка или список); регистр имени не важен. */
    fun header(headers: Map<*, *>?, name: String): String? {
        for ((key, value) in headers ?: return null) {
            if ((key as? String).equals(name, ignoreCase = true)) {
                return scalar(if (value is List<*>) value.firstOrNull() else value)
            }
        }

        return null
    }
}

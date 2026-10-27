package com.privategateway.build

import groovy.json.JsonOutput
import groovy.json.JsonSlurper
import org.gradle.api.GradleException

/**
 * Лицензии приложения Android (решение 449) из shared/licenses/android.json и shared/licenses/texts — в assets:
 *   licenses/components.json — список для экрана «Лицензии открытых компонентов»: имя, лицензия и ее подпись,
 *     авторство, ссылки, версия, модули Maven, тексты лицензий;
 *   licenses/<SPDX>.txt — полные тексты лицензий, которые нужны компонентам;
 *   THIRD-PARTY-NOTICES.txt — то же одним файлом, как у выпуска десктопа (scripts/package.cs).
 * Неполное не пишется: модуль из runtime-зависимостей выпуска без компонента, компонент без модулей, .aar без версии,
 * лицензия без текста — ошибка сборки со всем списком, а не пропуск.
 */
internal object LicensesWriter {
    const val LIST = "licenses/components.json"
    const val NOTICES = "THIRD-PARTY-NOTICES.txt"

    /** Строка android.json; приходит ровно одним путем: [maven], [aar] или [inside]. */
    class Component(
        val name: String,
        val license: String,
        val shown: String?,
        val copyright: String,
        val url: String,
        val maven: List<String>,
        val aar: String?,
        val inside: String?,
        val source: String?,
        val notice: String?,
    )

    /**
     * [modules] — «группа:имя:версия» из runtime-зависимостей выпуска; [versions] — содержимое android/<имя>.version по
     * имени файла без расширения; [texts] — тексты лицензий по SPDX; [appVersion] — версия приложения для заголовка.
     */
    fun write(componentsJson: String, texts: Map<String, String>, modules: List<String>, versions: Map<String, String>, appVersion: String): Map<String, String> {
        val components = parse(componentsJson)
        val problems = mutableListOf<String>()
        val names = components.map { it.name }.toSet()
        if (names.size != components.size) {
            problems += "Имена компонентов повторяются"
        }

        val owned = LinkedHashMap<String, MutableList<String>>()
        for (module in modules.distinct().sorted()) {
            val key = module.substringBeforeLast(':')
            val owner = owner(components, key)
            if (owner == null) {
                problems += "Модуль $module не покрыт ни одним компонентом shared/licenses/android.json"
            } else {
                owned.getOrPut(owner.name) { mutableListOf() } += module
            }
        }

        val entries = mutableListOf<Map<String, Any>>()
        val needed = LinkedHashSet<String>()
        for (component in components) {
            val shipped = owned[component.name].orEmpty()
            if (component.maven.isNotEmpty() && shipped.isEmpty()) {
                problems += "У компонента «${component.name}» нет модулей в зависимостях — строка устарела"
            }

            if (component.inside != null && (component.inside !in names || component.inside == component.name)) {
                problems += "Компонент «${component.name}» входит в неизвестный «${component.inside}»"
            }

            val version = when {
                component.aar != null -> versions[component.aar.lowercase()]?.trim().orEmpty().also {
                    if (it.isEmpty()) {
                        problems += "Нет версии .aar «${component.aar}»: android/${component.aar.lowercase()}.version"
                    }
                }

                else -> shipped.map { it.substringAfterLast(':') }.distinct().joinToString(", ")
            }

            val ids = textIds(component.license)
            for (id in ids) {
                if (texts[id].isNullOrBlank()) {
                    problems += "Нет полного текста лицензии $id (компонент «${component.name}»): shared/licenses/texts/$id.txt"
                }
            }

            needed += ids
            entries += linkedMapOf(
                "name" to component.name,
                "license" to component.license,
                "label" to (component.shown ?: component.license),
                "copyright" to component.copyright,
                "url" to component.url,
                "version" to version,
                "source" to component.source?.replace("{version}", version).orEmpty(),
                "notice" to component.notice.orEmpty(),
                "inside" to component.inside.orEmpty(),
                "modules" to shipped.map(::shown),
                "texts" to ids,
            )
        }

        if (problems.isNotEmpty()) {
            throw GradleException("Лицензии Android — неполный список:\n" + problems.joinToString("\n") { "  - $it" })
        }

        val files = linkedMapOf(LIST to JsonOutput.prettyPrint(JsonOutput.toJson(entries)) + "\n")
        for (id in needed) {
            files["licenses/$id.txt"] = texts.getValue(id).replace("\r\n", "\n").trimEnd() + "\n"
        }

        files[NOTICES] = notices(entries, needed, texts, appVersion)
        return files
    }

    /** Компонент модуля «группа:имя»: самый длинный подходящий шаблон — «androidx.compose.ui:ui» у Compose, а не AndroidX. */
    fun owner(components: List<Component>, key: String): Component? = components
        .flatMap { component -> component.maven.filter { covers(it, key) }.map { component to it.length } }
        .maxByOrNull { it.second }
        ?.first

    /** Шаблон «группа:имя»: целиком или «*» в конце — любой хвост. */
    fun covers(pattern: String, key: String) = if (pattern.endsWith("*")) key.startsWith(pattern.dropLast(1)) else key == pattern

    /** Выражение SPDX → тексты: «MIT OR Apache-2.0» → MIT, Apache-2.0; GPL-3.0-only и -or-later — один текст GPL-3.0. */
    fun textIds(expression: String): List<String> = expression.replace("(", " ").replace(")", " ")
        .split(' ')
        .filter { it.isNotEmpty() && it !in setOf("AND", "OR", "WITH") }
        .map { it.removeSuffix("-only").removeSuffix("-or-later") }
        .distinct()

    fun parse(json: String): List<Component> {
        // Комментарии — строки с «//» в начале, как в components.json десктопа.
        val text = json.lines().filterNot { it.trimStart().startsWith("//") }.joinToString("\n")
        val list = JsonSlurper().parseText(text) as? List<*> ?: throw GradleException("android.json: ожидался массив")
        return list.mapIndexed { index, item ->
            val map = item as? Map<*, *> ?: throw GradleException("android.json: строка ${index + 1} — не объект")
            fun string(key: String): String? = when (val value = map[key]) {
                null -> null
                is String -> value
                else -> throw GradleException("android.json: «$key» в строке ${index + 1} — не строка")
            }

            fun required(key: String) = string(key)?.takeIf { it.isNotBlank() } ?: throw GradleException("android.json: нет «$key» в строке ${index + 1}")

            val maven = when (val value = map["maven"]) {
                null -> emptyList()
                is List<*> -> value.map { it as? String ?: throw GradleException("android.json: «maven» в строке ${index + 1} — не строки") }
                else -> throw GradleException("android.json: «maven» в строке ${index + 1} — не массив")
            }

            val component = Component(
                name = required("name"),
                license = required("license"),
                shown = string("shown"),
                copyright = required("copyright"),
                url = required("url"),
                maven = maven,
                aar = string("aar"),
                inside = string("inside"),
                source = string("source"),
                notice = string("notice"),
            )
            val ways = listOf(component.maven.isNotEmpty(), component.aar != null, component.inside != null).count { it }
            if (ways != 1) {
                throw GradleException("android.json: «${component.name}» должен приходить одним путем — maven, aar или inside")
            }

            component
        }
    }

    /** «androidx.core:core:1.17.0» → «androidx.core:core 1.17.0». */
    private fun shown(module: String) = module.substringBeforeLast(':') + " " + module.substringAfterLast(':')

    private fun notices(entries: List<Map<String, Any>>, ids: Collection<String>, texts: Map<String, String>, appVersion: String): String {
        val rule = "=".repeat(80)
        val lines = mutableListOf(
            "Private Gateway Client $appVersion (Android)".replace("  ", " "),
            "Открытые компоненты программы и их лицензии / Open source components and their licenses",
        )
        for (entry in entries) {
            lines += listOf("", rule, "${entry["name"]} ${entry["version"]}".trimEnd(), "Лицензия / License: ${entry["license"]}")
            lines += listOf(entry["copyright"] as String, entry["url"] as String)
            (entry["source"] as String).takeIf { it.isNotEmpty() }?.let { lines += "Исходный код / Source code: $it" }
            (entry["notice"] as String).takeIf { it.isNotEmpty() }?.let { lines += it }
            (entry["inside"] as String).takeIf { it.isNotEmpty() }?.let { lines += "Входит в / Part of: $it" }
            val modules = entry["modules"] as List<*>
            if (modules.isNotEmpty()) {
                lines += "Модули / Modules: " + modules.joinToString(", ")
            }
        }

        lines += listOf("", rule, "Тексты лицензий / License texts")
        for (id in ids) {
            lines += listOf("", "-".repeat(40) + " " + id, texts.getValue(id).replace("\r\n", "\n").trimEnd())
        }

        return lines.joinToString("\n") + "\n"
    }
}

package com.privategateway.client.licenses

import android.content.res.AssetManager
import org.json.JSONArray

/**
 * Открытый компонент приложения — строка shared/licenses/android.json, которую сборка (build-logic, generatePgLicenses)
 * дополнила версией и модулями: name, лицензия SPDX и ее подпись для людей, авторство, ссылки, версия, обязательная
 * строка авторства, в какой компонент входит, модули Maven «группа:имя версия» и тексты лицензий по SPDX.
 */
data class OpenComponent(
    val name: String,
    val license: String,
    val label: String,
    val copyright: String,
    val url: String,
    val version: String,
    val source: String,
    val notice: String,
    val inside: String,
    val modules: List<String>,
    val texts: List<String>,
)

/** Список и тексты лицензий из assets (решение 449): то же, что в THIRD-PARTY-NOTICES.txt приложения. */
object OpenComponents {
    const val LIST = "licenses/components.json"
    const val NOTICES = "THIRD-PARTY-NOTICES.txt"

    fun read(assets: AssetManager): List<OpenComponent> {
        val json = JSONArray(assets.open(LIST).bufferedReader().use { it.readText() })
        return (0 until json.length()).map { index ->
            val item = json.getJSONObject(index)
            fun strings(key: String) = item.getJSONArray(key).let { array -> (0 until array.length()).map(array::getString) }
            OpenComponent(
                name = item.getString("name"),
                license = item.getString("license"),
                label = item.getString("label"),
                copyright = item.getString("copyright"),
                url = item.getString("url"),
                version = item.getString("version"),
                source = item.getString("source"),
                notice = item.getString("notice"),
                inside = item.getString("inside"),
                modules = strings("modules"),
                texts = strings("texts"),
            )
        }
    }

    /** Полный текст лицензии по SPDX (тексты GPL-3.0-only и -or-later — один, «GPL-3.0»). */
    fun text(assets: AssetManager, id: String): String = assets.open("licenses/$id.txt").bufferedReader().use { it.readText() }
}

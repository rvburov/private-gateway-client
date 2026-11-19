package com.privategateway.client.core.settings

import com.privategateway.client.core.links.JsonTree

/**
 * Сервис для «Все адреса сервиса» (решение 741): [id] — категория geosite десктопа, [name] — название (на всех языках одно),
 * адреса — домены с поддоменами ([domains]) и точные имена ([full]).
 */
data class ServiceSite(val id: String, val name: String, val domains: List<String>, val full: List<String>) {
    /** Шаблон правила «geosite:id» — тот же, что у десктопа: правило переносится копией настроек. */
    val pattern: String get() = SitePattern.GEOSITE + id

    /** Входит ли имя в адреса сервиса: домен списка, его поддомен или точное имя. */
    fun contains(host: String): Boolean = host in full || domains.any { host == it || host.endsWith(".$it") }

    /** Условия доменов для ядра: «domain:» — с поддоменами, «full:» — только имя. Базы geosite у ядра телефона нет. */
    val conditions: List<String> get() = domains.map { "domain:$it" } + full.map { "full:$it" }
}

/**
 * Сервисы «Все адреса сервиса» (решение 741) — shared/specifications/routing/services.json, ресурс модуля: тот же список у
 * десктопа и iOS. Видео и картинки сайта идут с других адресов, и правило для одного домена сайт не открывает целиком.
 */
object ServiceSites {
    val all: List<ServiceSite> by lazy { load() }

    /** Сервис правила «geosite:id» из списка; иначе null. */
    fun ofPattern(pattern: String): ServiceSite? =
        if (pattern.startsWith(SitePattern.GEOSITE)) all.firstOrNull { it.id == pattern.removePrefix(SitePattern.GEOSITE) } else null

    /** Сервис, в адреса которого входит обычный домен («youtube.com», «m.youtube.com»); иначе null. */
    fun find(pattern: String): ServiceSite? = if (SitePattern.isPlainDomain(pattern)) all.firstOrNull { it.contains(pattern) } else null

    private fun load(): List<ServiceSite> {
        val text = ServiceSites::class.java.getResourceAsStream("/routing/services.json")?.use { it.readBytes().toString(Charsets.UTF_8) }
            ?: error("Нет списка сервисов в ресурсах модуля")
        val services = (JsonTree.parse(text).value as Map<*, *>)["services"] as List<*>
        return services.map { item ->
            val service = item as Map<*, *>
            fun list(key: String) = (service[key] as List<*>).map { it as String }
            ServiceSite(service["id"] as String, service["name"] as String, list("domains"), list("full"))
        }
    }
}

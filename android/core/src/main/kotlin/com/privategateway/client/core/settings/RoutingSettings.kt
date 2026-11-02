package com.privategateway.client.core.settings

import com.privategateway.client.core.links.JsonText
import com.privategateway.client.core.links.JsonTree
import java.net.URI
import java.net.URISyntaxException
import java.util.Locale

/** Что идет через VPN («Маршрутизация», доска 13). */
enum class RoutingMode {
    /** Весь трафик — через VPN; по умолчанию. */
    AllTraffic,

    /** Все через VPN, кроме выбранных программ и правил «Напрямую». */
    Split,
    ;

    companion object {
        /** Из сохраненного имени: нет значения или имя из будущей версии — «Весь трафик». */
        fun read(name: String?): RoutingMode = entries.firstOrNull { it.name == name } ?: AllTraffic
    }
}

/** Что делать с сайтом из правила; порядок — как у сегментов листа правила. */
enum class RuleAction {
    Direct,
    Proxy,
    Block,
}

/** Программа мимо VPN: имя пакета Android, название для подписи, включена ли. */
data class BypassApp(val packageName: String, val name: String, val enabled: Boolean = true)

/** Правило для сайта: домен, «*.домен», адрес или подсеть — уже в виде [SitePattern.normalize]. */
data class SiteRule(val pattern: String, val action: RuleAction)

/**
 * «Маршрутизация» телефона (доска 13) — как `RoutingSettings` десктопа. По умолчанию весь трафик через VPN: раздельный режим —
 * только осознанным выбором. Программы мимо VPN — `VpnService.Builder.addDisallowedApplication`, только в раздельном
 * режиме; правила для сайтов — правила ядра Xray, «Напрямую» — тоже только в раздельном режиме (решение 110).
 */
data class RoutingSettings(
    val mode: RoutingMode = RoutingMode.AllTraffic,
    val apps: List<BypassApp> = emptyList(),
    val rules: List<SiteRule> = emptyList(),
) {
    val split: Boolean get() = mode == RoutingMode.Split

    /** Пакеты мимо карты туннеля: только в раздельном режиме и только включенные, без повторов. */
    val disallowedApps: List<String> get() = if (split) apps.filter { it.enabled }.map { it.packageName }.distinct() else emptyList()

    /** Правила, которые получает ядро, по порядку списка: «Напрямую» — только в раздельном режиме. */
    val coreRules: List<SiteRule> get() = rules.filter { split || it.action != RuleAction.Direct }

    /** Идут ли сейчас программы мимо VPN: только в раздельном режиме и только включенные (HasBypassApps десктопа). */
    val hasBypassApps: Boolean get() = disallowedApps.isNotEmpty()

    /** Идут ли сайты мимо VPN: правила «Напрямую» действуют только в раздельном режиме (HasBypassSites десктопа). */
    val hasBypassSites: Boolean get() = split && rules.any { it.action == RuleAction.Direct }

    /** В режиме «Весь трафик» правила «Напрямую» молчат — об этом подпись над списком. */
    val hasIdleRules: Boolean get() = !split && rules.any { it.action == RuleAction.Direct }

    /**
     * Новое правило ([original] — null) — в конец списка, измененное — на свое место. Правило для того же сайта заменяется:
     * два правила на один сайт противоречили бы друг другу (как `SaveRule` десктопа).
     */
    fun saveRule(original: SiteRule?, rule: SiteRule): RoutingSettings {
        val kept = rules.filter { it == original || it.pattern != rule.pattern }.toMutableList()
        val index = if (original == null) -1 else kept.indexOf(original)
        if (index >= 0) {
            kept[index] = rule
        } else {
            kept.add(rule)
        }

        return copy(rules = kept)
    }

    fun removeRule(rule: SiteRule): RoutingSettings = copy(rules = rules.filter { it != rule })

    /** Программа уже в списке — она включается, а не заводится второй строкой; иначе — в конец списка. */
    fun addApp(packageName: String, name: String): RoutingSettings =
        if (apps.any { it.packageName == packageName }) {
            copy(apps = apps.map { if (it.packageName == packageName) it.copy(enabled = true) else it })
        } else {
            copy(apps = apps + BypassApp(packageName, name))
        }

    /** Заменить программу в списке; [replacement] null — убрать. */
    fun setApp(app: BypassApp, replacement: BypassApp?): RoutingSettings =
        copy(apps = apps.mapNotNull { if (it == app) replacement else it })

    /** Список программ для хранилища — текст JSON `[{"package": …, "name": …, "enabled": …}]`. */
    fun appsJson(): String =
        JsonText.value(apps.map { linkedMapOf("package" to it.packageName, "name" to it.name, "enabled" to it.enabled) })

    /** Список правил для хранилища — текст JSON `[{"pattern": …, "action": …}]`. */
    fun rulesJson(): String = JsonText.value(rules.map { linkedMapOf("pattern" to it.pattern, "action" to it.action.name) })

    companion object {
        /** Имя пакета Android: части через точку, каждая с буквы, из букв, цифр и «_». */
        private val packageName = Regex("^[A-Za-z][A-Za-z0-9_]*(\\.[A-Za-z][A-Za-z0-9_]*)+$")

        /**
         * Из хранилища: режим, программы и правила текстом JSON. Не JSON, запись без нужных полей, неверное имя пакета,
         * неизвестное действие или шаблон, который телефон не берет, — пропускаются: ни аварии, ни правила, которое ядро
         * не примет.
         */
        fun read(mode: String?, apps: String?, rules: String?): RoutingSettings = RoutingSettings(
            RoutingMode.read(mode),
            items(apps).mapNotNull { item ->
                val name = item["package"] as? String ?: return@mapNotNull null
                if (!packageName.matches(name)) {
                    return@mapNotNull null
                }

                BypassApp(name, (item["name"] as? String)?.takeIf { it.isNotBlank() } ?: name, item["enabled"] as? Boolean ?: true)
            },
            items(rules).mapNotNull { item ->
                val pattern = SitePattern.normalize(item["pattern"] as? String) ?: return@mapNotNull null
                val action = RuleAction.entries.firstOrNull { it.name == item["action"] } ?: return@mapNotNull null
                SiteRule(pattern, action)
            },
        )

        private fun items(text: String?): List<Map<*, *>> {
            if (text.isNullOrEmpty()) {
                return emptyList()
            }

            val value = try {
                JsonTree.parse(text).value
            } catch (_: JsonTree.SyntaxError) {
                return emptyList()
            }

            return (value as? List<*>)?.filterIsInstance<Map<*, *>>().orEmpty()
        }
    }
}

/**
 * Шаблон сайта для правила — `SitePattern` десктопа: «example.com» и «*.example.com» строчными, адрес «203.0.113.5» или
 * подсеть «198.51.100.0/24», «2001:db8::/32» (подсеть приводится к адресу сети). Адрес с «https://» и путем сокращается до
 * домена — так его чаще всего и вставляют. «geosite:категория» телефон не берет: базы geosite в приложении нет, и ядро с таким
 * правилом не запустилось бы.
 */
object SitePattern {
    /** Длиннее имени в DNS не бывает; поле листа правила больше не принимает. */
    const val MAX_LENGTH = 253

    private const val GEOSITE = "geosite:"

    /** Шаблон в виде для ядра или null — не подходит. */
    fun normalize(text: String?): String? {
        var value = text?.trim()?.lowercase(Locale.ROOT) ?: return null
        if (value.isEmpty() || value.length > MAX_LENGTH || value.startsWith(GEOSITE)) {
            return null
        }

        address(value)?.let { return it }
        if (value.startsWith("http://") || value.startsWith("https://")) {
            value = try {
                URI(value).host
            } catch (_: URISyntaxException) {
                null
            } ?: return null
        }

        val wildcard = value.startsWith("*.")
        val host = if (wildcard) value.substring(2) else value
        val labels = host.split('.')
        // «*.local» — одна метка после звездочки допустима; без звездочки нужен домен с точкой.
        val fits = labels.size >= (if (wildcard) 1 else 2) &&
            labels.all { label -> label.length in 1..63 && label.first() != '-' && label.last() != '-' && label.all { it in 'a'..'z' || it in '0'..'9' || it == '-' } } &&
            !labels.last().all { it in '0'..'9' }
        return if (fits) (if (wildcard) "*." else "") + host else null
    }

    /** Шаблон — адрес или подсеть, а не имя: правило сравнивает IP соединения. */
    fun isAddress(pattern: String): Boolean = address(pattern) != null

    /** Условие домена Xray: «example.com» и «*.example.com» — домен с поддоменами («domain:»). */
    fun domainCondition(pattern: String): String = "domain:" + pattern.removePrefix("*.")

    /**
     * Адрес или подсеть в каноническом виде; иначе null. IPv4 — только четыре числа через точку («1.2» адресом не считается).
     * Подсеть /0 — весь интернет: это режим, а не правило.
     */
    private fun address(value: String): String? {
        val slash = value.indexOf('/')
        val text = if (slash < 0) value else value.substring(0, slash)
        val v4 = text.split('.').let { parts -> parts.size == 4 && parts.all { part -> part.length in 1..3 && part.all { it in '0'..'9' } } }
        if (!(v4 || text.contains(':')) || text.contains('%')) {
            return null
        }

        val bytes = IpAddress.parse(text) ?: return null
        if (slash < 0) {
            return format(bytes)
        }

        val digits = value.substring(slash + 1)
        val bits = bytes.size * 8
        val prefix = digits.takeIf { it.length in 1..3 && it.all { c -> c in '0'..'9' } }?.toInt() ?: return null
        if (prefix < 1 || prefix > bits) {
            return null
        }

        for (i in bytes.indices) {
            val keep = (prefix - i * 8).coerceIn(0, 8)
            bytes[i] = (bytes[i].toInt() and (0xFF shl (8 - keep))).toByte()
        }

        return "${format(bytes)}/$prefix"
    }

    /** Адрес текстом: IPv4 — четыре числа, IPv6 — строчными, самая длинная серия нулевых групп (от двух) — «::». */
    private fun format(bytes: ByteArray): String {
        if (bytes.size == 4) {
            return bytes.joinToString(".") { (it.toInt() and 0xFF).toString() }
        }

        val words = (0 until 8).map { ((bytes[it * 2].toInt() and 0xFF) shl 8) or (bytes[it * 2 + 1].toInt() and 0xFF) }
        var bestStart = -1
        var bestLength = 1
        var start = -1
        for (i in 0..8) {
            if (i < 8 && words[i] == 0) {
                if (start < 0) {
                    start = i
                }
            } else if (start >= 0) {
                if (i - start > bestLength) {
                    bestStart = start
                    bestLength = i - start
                }

                start = -1
            }
        }

        if (bestStart < 0) {
            return words.joinToString(":") { Integer.toHexString(it) }
        }

        val head = words.subList(0, bestStart).joinToString(":") { Integer.toHexString(it) }
        val tail = words.subList(bestStart + bestLength, 8).joinToString(":") { Integer.toHexString(it) }
        return "$head::$tail"
    }
}

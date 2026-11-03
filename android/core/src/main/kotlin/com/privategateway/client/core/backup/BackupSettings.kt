package com.privategateway.client.core.backup

import com.privategateway.client.core.notify.NotificationLevel
import com.privategateway.client.core.settings.CoreLogLevel
import com.privategateway.client.core.settings.DnsChoice
import com.privategateway.client.core.settings.RoutingMode
import com.privategateway.client.core.settings.TlsFingerprint
import com.privategateway.client.core.subscriptions.RefreshInterval
import com.privategateway.client.core.vpn.ReconnectPolicy

/**
 * Настройки из копии другой платформы (DECISIONS.md, п. 3): имена ключей у Android и iOS общие, а значения списков iPhone
 * пишет со строчной буквы (`custom`, `every6Hours`). Значение выбора сравнивается без учета регистра и приводится к имени
 * Android; неизвестное остается как есть и при чтении станет значением по умолчанию. Остальные ключи не трогаются.
 */
object BackupSettings {
    private val choices: Map<String, List<String>> = mapOf(
        "tunnel_dns" to DnsChoice.entries.map { it.name },
        "subs_refresh" to RefreshInterval.entries.map { it.name },
        "core_log" to CoreLogLevel.entries.map { it.name },
        "conn_fingerprint" to TlsFingerprint.entries.map { it.name },
        "routing_mode" to RoutingMode.entries.map { it.name },
        "notify_level" to NotificationLevel.entries.map { it.name },
        "reconnect_policy" to ReconnectPolicy.entries.map { it.name },
    )

    fun normalize(values: Map<String, String>): Map<String, String> = values.mapValues { (key, value) ->
        choices[key]?.firstOrNull { it.equals(value, ignoreCase = true) } ?: value
    }
}

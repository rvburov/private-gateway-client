package com.privategateway.client.core.inspect

import com.privategateway.client.core.settings.TunnelSettings

/** Части отчета «Проверки», общие с десктопом (`DnsReport`) и iOS: вид своего DNS для раздела DNS. */
object ReportParts {
    /**
     * Вид своего DNS для отчета — без самого адреса: путь DoH может нести токен доступа. Ключ текста —
     * diag.report.dns.custom.<вид>: missing, private, doh, address, invalid.
     */
    fun customKind(tunnel: TunnelSettings): String {
        val custom = tunnel.customDns?.trim().orEmpty()
        return when {
            custom.isEmpty() -> "missing"
            tunnel.lanDns != null -> "private"
            TunnelSettings.validCustomDns(custom) -> if (custom.lowercase().startsWith("https://")) "doh" else "address"
            else -> "invalid"
        }
    }
}

package com.privategateway.client.core.servers

/** Сведения о сервере для подписей интерфейса — как `ServerFacts` и `TrafficEncryption` десктопа. */
object ServerFacts {
    /**
     * Шифруется ли трафик до сервера: от этого зависит подпись «Трафик зашифрован», она не ставится всегда — VLESS без
     * TLS и REALITY передает данные открыто.
     */
    fun isEncrypted(protocol: ProxyProtocol, security: TransportSecurity): Boolean = when (protocol) {
        // Шифруют сами, независимо от транспорта.
        ProxyProtocol.Vmess, ProxyProtocol.Shadowsocks, ProxyProtocol.WireGuard -> true
        // Работают только поверх TLS (QUIC) — без него не подключатся.
        ProxyProtocol.Trojan, ProxyProtocol.Hysteria2, ProxyProtocol.Tuic -> true
        // VLESS собственного шифрования не имеет.
        ProxyProtocol.Vless -> security != TransportSecurity.None
    }

    /** Имя протокола так, как его пишут сами протоколы. */
    fun protocolName(protocol: ProxyProtocol): String = when (protocol) {
        ProxyProtocol.Vless -> "VLESS"
        ProxyProtocol.Vmess -> "VMess"
        ProxyProtocol.Trojan -> "Trojan"
        ProxyProtocol.Shadowsocks -> "Shadowsocks"
        ProxyProtocol.Hysteria2 -> "Hysteria2"
        ProxyProtocol.Tuic -> "TUIC"
        ProxyProtocol.WireGuard -> "WireGuard"
    }

    /** Имя защиты транспорта; без защиты — null (в подписи не пишется). */
    fun securityName(security: TransportSecurity): String? = when (security) {
        TransportSecurity.Reality -> "REALITY"
        TransportSecurity.Tls -> "TLS"
        TransportSecurity.None -> null
    }

    /** Имя транспорта для подписи; обычный TCP, UDP и QUIC — null (не пишутся). */
    fun transportName(kind: TransportKind): String? = when (kind) {
        TransportKind.WebSocket -> "WS"
        TransportKind.Grpc -> "gRPC"
        TransportKind.Xhttp -> "XHTTP"
        TransportKind.HttpUpgrade -> "HTTPUpgrade"
        TransportKind.Tcp, TransportKind.Udp, TransportKind.Quic -> null
    }

    /**
     * Короткая подпись протокола, как в списке серверов: «VLESS · REALITY», «VLESS · XHTTP», «Trojan · WS», «Hysteria2».
     * Второе слово — то, что отличает сервер: REALITY, иначе транспорт, иначе TLS у протоколов, которые бывают и без него.
     */
    fun summary(protocol: ProxyProtocol, security: TransportSecurity, transport: TransportKind): String {
        // TUIC поддерживается только пятой версии — версию пишем, как в макете.
        val name = if (protocol == ProxyProtocol.Tuic) "TUIC v5" else protocolName(protocol)
        val detail = if (security == TransportSecurity.Reality) {
            securityName(security)
        } else {
            transportName(transport)
                ?: if (security == TransportSecurity.Tls && (protocol == ProxyProtocol.Vless || protocol == ProxyProtocol.Vmess)) securityName(security) else null
        }

        return if (detail == null) name else "$name · $detail"
    }
}

package com.privategateway.client.core.servers

/** Протокол сервера — те же, что у десктопа (`ProxyProtocol`). */
enum class ProxyProtocol {
    Vless,
    Vmess,
    Trojan,
    Shadowsocks,
    Hysteria2,
    Tuic,
    WireGuard,
}

/** Транспорт поверх TCP или UDP. */
enum class TransportKind {
    Tcp,
    WebSocket,
    Grpc,
    HttpUpgrade,
    Xhttp,
    Quic,

    /** WireGuard — свой протокол поверх UDP. */
    Udp,
}

/** Защита транспорта. */
enum class TransportSecurity {
    None,
    Tls,
    Reality,
}

/** Транспорт: вид, путь, заголовок Host, имя сервиса gRPC. */
data class TransportOptions(
    val kind: TransportKind,
    val path: String? = null,
    val host: String? = null,
    val serviceName: String? = null,
) {
    companion object {
        val Tcp = TransportOptions(TransportKind.Tcp)
        val Quic = TransportOptions(TransportKind.Quic)
        val Udp = TransportOptions(TransportKind.Udp)
    }
}

/** Защита: TLS или REALITY с именем, отпечатком, ALPN и ключами REALITY. */
data class SecurityOptions(
    val kind: TransportSecurity,
    val sni: String? = null,
    val fingerprint: String? = null,
    val alpn: List<String>? = null,
    val allowInsecure: Boolean = false,
    val realityPublicKey: String? = null,
    val realityShortId: String? = null,
    val realitySpiderX: String? = null,
) {
    companion object {
        val None = SecurityOptions(TransportSecurity.None)
    }
}

/** Параметры протокола: flow VLESS, шифр, obfs Hysteria2, контроль перегрузки TUIC, поля WireGuard. */
data class ProtocolOptions(
    val flow: String? = null,
    val cipher: String? = null,
    val obfs: String? = null,
    val congestionControl: String? = null,
    val peerPublicKey: String? = null,
    val addresses: List<String>? = null,
    val mtu: Int? = null,
) {
    companion object {
        val Empty = ProtocolOptions()
    }
}

/** Секреты сервера; в тексте (журнал, отладка) не показываются. */
data class ServerCredentials(
    val uuid: String? = null,
    val password: String? = null,
    val obfsPassword: String? = null,
    val privateKey: String? = null,
    val presharedKey: String? = null,
) {
    override fun toString(): String = "ServerCredentials { *** }"
}

/** Описание сервера — как его передают ядру: то же, что `ServerDefinition` десктопа. */
data class ServerDefinition(
    val name: String,
    val countryCode: String,
    val protocol: ProxyProtocol,
    val host: String,
    val port: Int,
    val transport: TransportOptions,
    val security: SecurityOptions,
    val options: ProtocolOptions,
    val credentials: ServerCredentials,
)

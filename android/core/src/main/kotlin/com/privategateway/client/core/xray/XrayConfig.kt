package com.privategateway.client.core.xray

import com.privategateway.client.core.links.JsonText
import com.privategateway.client.core.servers.ProxyProtocol
import com.privategateway.client.core.servers.ServerDefinition
import com.privategateway.client.core.servers.TransportKind
import com.privategateway.client.core.servers.TransportSecurity
import java.util.Locale

/** Почему ядро Xray не возьмется за сервер — как у десктопа (`CoreRejection`). */
enum class CoreRejection {
    /** Hysteria2 и TUIC — для второго ядра, на Android его пока нет. */
    UnsupportedProtocol,

    /** Нет данных доступа: UUID, пароля или ключа. */
    MissingCredentials,

    /** Транспорт, которого у протокола в Xray нет (например, QUIC у VLESS). */
    UnsupportedTransport,
}

/** Конфиг Xray (текст JSON) или причина отказа. */
data class XrayConfigResult(val json: String?, val rejection: CoreRejection?) {
    val isOk: Boolean get() = json != null
}

/**
 * Настройки подключения на телефоне — то, что из настроек десктопа имеет смысл на Android.
 *
 * @property dns DNS ядра: адрес DoH или IP; запросы к нему идут через сервер.
 * @property allowLan локальная сеть мимо туннеля (принтер, телевизор); иначе — блок.
 * @property ipv6 IPv6 через сервер; иначе адреса IPv6 закрыты, а DNS отвечает только IPv4.
 * @property mux мультиплексирование соединений (не с XTLS Vision).
 * @property fingerprint отпечаток TLS (uTLS): null — из профиля сервера.
 * @property sniffing распознавание имени сайта в TLS, HTTP и QUIC — только для правил.
 * @property mtu MTU туннеля.
 * @property debugLog подробный журнал ядра (без адресов сайтов: журнал соединений всегда выключен).
 */
data class CoreSettings(
    val dns: String = XrayConfig.DEFAULT_DNS,
    val allowLan: Boolean = false,
    val ipv6: Boolean = false,
    val mux: Boolean = false,
    val fingerprint: String? = null,
    val sniffing: Boolean = true,
    val mtu: Int = 1500,
    val debugLog: Boolean = false,
)

/**
 * Конфиг Xray-core для телефона: приложение собирает его само — из описания сервера и настроек, а не из готового текста
 * (модель угроз, как у службы десктопа). Вход — TUN: дескриптор дает VpnService, Xray получает его через `env`
 * (`xray.tun.fd`); выход — выбранный сервер (тот же, что собирает десктоп, `XrayConfigBuilder`), прямой, блок и DNS.
 * Запросы DNS из туннеля отвечает DNS ядра через сервер. Журнал соединений выключен: адреса сайтов в журнал не попадают.
 */
object XrayConfig {
    const val TUN_TAG = "tun-in"
    const val PROXY_TAG = "proxy"
    const val DIRECT_TAG = "direct"
    const val BLOCK_TAG = "block"
    const val DNS_TAG = "dns-out"

    /** Адрес TUN телефона и адрес, на который ОС шлет DNS: тот же план, что у десктопа. */
    const val TUN_ADDRESS = "172.19.0.1"
    const val TUN_PREFIX = 30
    const val TUN_DNS = "172.19.0.2"

    /** DNS по умолчанию: DoH через туннель. */
    const val DEFAULT_DNS = "https://1.1.1.1/dns-query"

    /** Локальные и особые адреса — тот же список, что у десктопа (`LocalNetworks`). */
    val localNetworks = listOf(
        "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16", "172.16.0.0/12", "192.0.0.0/24", "192.0.2.0/24",
        "192.88.99.0/24", "192.168.0.0/16", "198.18.0.0/15", "198.51.100.0/24", "203.0.113.0/24", "224.0.0.0/4", "240.0.0.0/4",
        "255.255.255.255/32", "::/128", "::1/128", "fc00::/7", "fe80::/10", "ff00::/8",
    )

    /**
     * @param server выбранный сервер.
     * @param settings настройки подключения.
     * @param tunFd дескриптор TUN от VpnService.
     */
    fun build(server: ServerDefinition, settings: CoreSettings, tunFd: Int): XrayConfigResult {
        val outbound = outbound(server, settings)
        outbound.rejection?.let { return XrayConfigResult(null, it) }

        val config = linkedMapOf<String, Any?>(
            "log" to linkedMapOf("loglevel" to if (settings.debugLog) "debug" else "warning", "access" to "none", "dnsLog" to false),
            "env" to linkedMapOf("xray.tun.fd" to tunFd.toString()),
            "dns" to linkedMapOf("servers" to listOf(settings.dns), "queryStrategy" to if (settings.ipv6) "UseIP" else "UseIPv4"),
            "inbounds" to listOf(
                linkedMapOf(
                    "tag" to TUN_TAG,
                    "protocol" to "tun",
                    "settings" to linkedMapOf("name" to "xray0", "mtu" to settings.mtu),
                    // Домен из TLS, HTTP и QUIC — только для правил: соединение идет туда, куда просили.
                    "sniffing" to linkedMapOf(
                        "enabled" to settings.sniffing,
                        "destOverride" to listOf("http", "tls", "quic"),
                        "routeOnly" to true,
                    ),
                ),
            ),
            "outbounds" to listOf(
                outbound.json,
                linkedMapOf(
                    "tag" to DIRECT_TAG,
                    "protocol" to "freedom",
                    "settings" to linkedMapOf("domainStrategy" to if (settings.ipv6) "AsIs" else "UseIPv4"),
                ),
                linkedMapOf("tag" to BLOCK_TAG, "protocol" to "blackhole"),
                // Запросы не A и AAAA — сразу отказ: иначе Xray молча их бросает, и программа ждет тайм-аут на каждый.
                linkedMapOf("tag" to DNS_TAG, "protocol" to "dns", "settings" to linkedMapOf("nonIPQuery" to "reject")),
            ),
            "routing" to linkedMapOf("domainStrategy" to "IPIfNonMatch", "rules" to rules(settings)),
        )
        return XrayConfigResult(JsonText.value(config), null)
    }

    private fun rules(settings: CoreSettings): List<Map<String, Any?>> {
        val rules = mutableListOf<Map<String, Any?>>(
            // DNS из туннеля — к любому адресу: отвечает выход DNS по настройкам этого конфига, через сервер.
            linkedMapOf("type" to "field", "inboundTag" to listOf(TUN_TAG), "port" to "53", "outboundTag" to DNS_TAG),
            // Локальная сеть: разрешена — напрямую, иначе — блок (по умолчанию запрещена, модель угроз).
            linkedMapOf("type" to "field", "ip" to localNetworks, "outboundTag" to if (settings.allowLan) DIRECT_TAG else BLOCK_TAG),
        )
        if (!settings.ipv6) {
            // «Только IPv4»: адреса IPv6 из туннеля — в блок: мимо сервера им не уйти, а через сервер — не просили.
            rules.add(linkedMapOf("type" to "field", "inboundTag" to listOf(TUN_TAG), "ip" to listOf("::/0"), "outboundTag" to BLOCK_TAG))
        }

        return rules
    }

    /** Выход сервера (словарь для JSON) или причина отказа. */
    internal class Outbound(val json: Map<String, Any?>?, val rejection: CoreRejection?)

    /** Выход сервера — те же поля, что собирает десктоп; метка — [PROXY_TAG]. Его же меряет [PingBatch]. */
    internal fun outbound(server: ServerDefinition, settings: CoreSettings): Outbound {
        val credentials = server.credentials
        val protocolSettings: Map<String, Any?> = when {
            server.protocol == ProxyProtocol.Vless && !credentials.uuid.isNullOrEmpty() -> {
                val user = linkedMapOf<String, Any?>("id" to credentials.uuid, "encryption" to "none")
                server.options.flow?.takeIf { it.isNotEmpty() }?.let { user["flow"] = it }
                vnext(server, user)
            }

            server.protocol == ProxyProtocol.Vmess && !credentials.uuid.isNullOrEmpty() ->
                vnext(server, linkedMapOf("id" to credentials.uuid, "security" to (server.options.cipher ?: "auto")))

            server.protocol == ProxyProtocol.Trojan && !credentials.password.isNullOrEmpty() ->
                servers(linkedMapOf("address" to server.host, "port" to server.port, "password" to credentials.password))

            server.protocol == ProxyProtocol.Shadowsocks && !credentials.password.isNullOrEmpty() && !server.options.cipher.isNullOrEmpty() ->
                servers(linkedMapOf("address" to server.host, "port" to server.port, "method" to server.options.cipher, "password" to credentials.password))

            server.protocol == ProxyProtocol.WireGuard && !credentials.privateKey.isNullOrEmpty() && !server.options.peerPublicKey.isNullOrEmpty() ->
                return Outbound(wireGuard(server), null)

            server.protocol == ProxyProtocol.Hysteria2 || server.protocol == ProxyProtocol.Tuic ->
                return Outbound(null, CoreRejection.UnsupportedProtocol)

            else -> return Outbound(null, CoreRejection.MissingCredentials)
        }

        val stream = stream(server, settings) ?: return Outbound(null, CoreRejection.UnsupportedTransport)
        val outbound = linkedMapOf(
            "tag" to PROXY_TAG,
            "protocol" to when (server.protocol) {
                ProxyProtocol.Vless -> "vless"
                ProxyProtocol.Vmess -> "vmess"
                ProxyProtocol.Trojan -> "trojan"
                else -> "shadowsocks"
            },
            "settings" to protocolSettings,
            "streamSettings" to stream,
        )

        // Mux несовместим с XTLS Vision (flow) — с ним не включается.
        if (settings.mux && server.options.flow.isNullOrEmpty()) {
            outbound["mux"] = linkedMapOf("enabled" to true, "concurrency" to 8)
        }

        return Outbound(outbound, null)
    }

    private fun vnext(server: ServerDefinition, user: Map<String, Any?>) =
        linkedMapOf("vnext" to listOf(linkedMapOf("address" to server.host, "port" to server.port, "users" to listOf(user))))

    private fun servers(server: Map<String, Any?>) = linkedMapOf("servers" to listOf(server))

    private fun wireGuard(server: ServerDefinition): Map<String, Any?> {
        val endpoint = if (server.host.contains(':')) "[${server.host}]:${server.port}" else "${server.host}:${server.port}"
        val peer = linkedMapOf<String, Any?>("publicKey" to server.options.peerPublicKey, "endpoint" to endpoint)
        server.credentials.presharedKey?.takeIf { it.isNotEmpty() }?.let { peer["preSharedKey"] = it }
        val settings = linkedMapOf<String, Any?>("secretKey" to server.credentials.privateKey, "peers" to listOf(peer))
        server.options.addresses?.takeIf { it.isNotEmpty() }?.let { settings["address"] = it }
        server.options.mtu?.let { settings["mtu"] = it }
        return linkedMapOf("tag" to PROXY_TAG, "protocol" to "wireguard", "settings" to settings)
    }

    private fun stream(server: ServerDefinition, settings: CoreSettings): Map<String, Any?>? {
        val transport = server.transport
        val network = when (transport.kind) {
            TransportKind.Tcp -> "raw"
            TransportKind.WebSocket -> "ws"
            TransportKind.Grpc -> "grpc"
            TransportKind.HttpUpgrade -> "httpupgrade"
            TransportKind.Xhttp -> "xhttp"
            else -> return null
        }

        val stream = linkedMapOf<String, Any?>("network" to network)
        when (transport.kind) {
            TransportKind.WebSocket, TransportKind.HttpUpgrade, TransportKind.Xhttp -> {
                val path = linkedMapOf<String, Any?>("path" to (transport.path ?: "/"))
                transport.host?.takeIf { it.isNotEmpty() }?.let { path["host"] = it }
                stream[network + "Settings"] = path
            }

            TransportKind.Grpc -> stream["grpcSettings"] = linkedMapOf("serviceName" to (transport.serviceName ?: ""))
            else -> Unit
        }

        val security = server.security
        val fingerprint = settings.fingerprint ?: security.fingerprint
        when (security.kind) {
            TransportSecurity.Tls -> {
                val tls = linkedMapOf<String, Any?>("serverName" to (security.sni ?: server.host))
                fingerprint?.takeIf { it.isNotEmpty() }?.let { tls["fingerprint"] = it }
                security.alpn?.takeIf { it.isNotEmpty() }?.let { tls["alpn"] = it }
                if (security.allowInsecure) {
                    tls["allowInsecure"] = true
                }

                stream["security"] = "tls"
                stream["tlsSettings"] = tls
            }

            TransportSecurity.Reality -> {
                stream["security"] = "reality"
                stream["realitySettings"] = linkedMapOf(
                    "serverName" to (security.sni ?: ""),
                    // REALITY без отпечатка не работает: из профиля или настройки, иначе — chrome.
                    "fingerprint" to (fingerprint?.takeIf { it.isNotEmpty() } ?: "chrome"),
                    "publicKey" to (security.realityPublicKey ?: ""),
                    "shortId" to (security.realityShortId ?: ""),
                    "spiderX" to (security.realitySpiderX ?: ""),
                )
            }

            TransportSecurity.None -> stream["security"] = "none"
        }

        return stream
    }

    /** Запрос к libXray (`LibXray.invoke`): версия API 3, метод и данные. */
    fun invoke(method: String, payload: Map<String, Any?> = emptyMap()): String =
        JsonText.value(linkedMapOf("apiVersion" to 3, "method" to method, "payload" to payload))

    /** Запуск ядра: конфиг передается текстом, файла с секретами на диске нет. */
    fun runRequest(configJson: String): String = invoke("runXray", linkedMapOf("xrayJson" to configJson))

    /** Название протокола строчными — для подписей и журнала. */
    fun protocolName(protocol: ProxyProtocol): String = protocol.name.lowercase(Locale.ROOT)
}

/** Ответ libXray: `{"success": …, "data": …, "error": "…"}` — удалось ли и текст ошибки. */
data class XrayResponse(val success: Boolean, val error: String?) {
    companion object {
        fun parse(text: String?): XrayResponse {
            val root = try {
                com.privategateway.client.core.links.JsonTree.parse(text ?: "").value as? Map<*, *>
            } catch (_: com.privategateway.client.core.links.JsonTree.SyntaxError) {
                null
            } ?: return XrayResponse(false, "Ответ ядра — не JSON")

            return XrayResponse(root["success"] == true, (root["error"] as? String)?.takeIf { it.isNotEmpty() })
        }
    }
}

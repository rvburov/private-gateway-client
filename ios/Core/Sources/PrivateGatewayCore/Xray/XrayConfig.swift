/// Почему ядро Xray не возьмется за сервер — как у десктопа и Android (`CoreRejection`).
public enum CoreRejection: Sendable {
    /// Hysteria2 и TUIC — для второго ядра (sing-box); на телефоне его нет, а iOS не дает приложению запускать процессы.
    case unsupportedProtocol

    /// Нет данных доступа: UUID, пароля или ключа.
    case missingCredentials

    /// Транспорт, которого у протокола в Xray нет (например, QUIC у VLESS).
    case unsupportedTransport

    /// VLESS или Trojan без TLS и REALITY на открытом адресе (`XrayConfig.unencrypted`): трафик шел бы к серверу открытым.
    /// Ядро libXray такой выход не строит («vless without TLS … is prohibited»).
    case unencrypted
}

/// Конфиг Xray (текст JSON) или причина отказа.
public struct XrayConfigResult: Sendable {
    public let json: String?
    public let rejection: CoreRejection?

    public var isOk: Bool { json != nil }
}

/// Настройки подключения на телефоне — то же, что `CoreSettings` Android.
public struct CoreSettings: Equatable, Hashable, Sendable {
    /// DNS ядра: адрес DoH или IP; запросы к нему идут через сервер.
    public var dns: String
    /// Локальная сеть мимо туннеля (принтер, телевизор); иначе — блок.
    public var allowLan: Bool
    /// IPv6 через сервер; иначе адреса IPv6 закрыты, а DNS отвечает только IPv4.
    public var ipv6: Bool
    /// Мультиплексирование соединений (не с XTLS Vision).
    public var mux: Bool
    /// Отпечаток TLS (uTLS): nil — из профиля сервера.
    public var fingerprint: String?
    /// Распознавание имени сайта в TLS, HTTP и QUIC — только для правил.
    public var sniffing: Bool
    /// MTU туннеля.
    public var mtu: Int
    /// Подробный журнал ядра (без адресов сайтов: журнал соединений всегда выключен).
    public var debugLog: Bool
    /// Свой DNS в частной сети (`TunnelSettings.lanDns`): запросы самого Xray к нему идут напрямую.
    public var lanDns: String?

    public init(
        dns: String = XrayConfig.defaultDns, allowLan: Bool = false, ipv6: Bool = false, mux: Bool = false, fingerprint: String? = nil,
        sniffing: Bool = true, mtu: Int = 1500, debugLog: Bool = false, lanDns: String? = nil
    ) {
        self.dns = dns
        self.allowLan = allowLan
        self.ipv6 = ipv6
        self.mux = mux
        self.fingerprint = fingerprint
        self.sniffing = sniffing
        self.mtu = mtu
        self.debugLog = debugLog
        self.lanDns = lanDns
    }
}

/// Конфиг Xray-core для телефона — тот же, что собирает `XrayConfig` Android: расширение туннеля собирает его само — из
/// описания сервера и настроек, а не из готового текста (модель угроз, как у службы десктопа). Вход — TUN: дескриптор
/// интерфейса Xray получает через `env` (`xray.tun.fd`); выход — выбранный сервер (те же поля, что у десктопа), прямой,
/// блок и DNS. Запросы DNS из туннеля отвечает DNS ядра через сервер. Журнал соединений выключен: адреса сайтов в журнал не
/// попадают.
public enum XrayConfig {
    public static let tunTag = "tun-in"
    public static let proxyTag = "proxy"
    public static let directTag = "direct"
    public static let blockTag = "block"
    public static let dnsTag = "dns-out"
    public static let subscriptionTag = "subscription-in"

    /// Выход DNS: A и AAAA (типы 1 и 28) отвечает сам, остальное — отказ REFUSED (код 5). Правилами `rules`: прежний
    /// «nonIPQuery» Xray-core 26.x объявил к удалению. Как у десктопа и Android. Вычисляемое: хранимое статическое значение
    /// типа без Sendable Swift 6 не пропустит.
    static var dnsRules: Json {
        .array([
            .object([("action", .string("hijack")), ("qType", .string("1,28"))]),
            .object([("action", .string("return")), ("rCode", .int(5))]),
        ])
    }

    /// Адрес TUN телефона и адрес, на который ОС шлет DNS: тот же план, что у десктопа и Android.
    public static let tunAddress = "172.19.0.1"
    public static let tunPrefix = 30
    public static let tunDns = "172.19.0.2"

    /// DNS по умолчанию: DoH через туннель.
    public static let defaultDns = "https://1.1.1.1/dns-query"

    /// Локальные и особые адреса — тот же список, что у десктопа (`LocalNetworks`) и Android.
    public static let localNetworks = [
        "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16", "172.16.0.0/12", "192.0.0.0/24", "192.0.2.0/24",
        "192.88.99.0/24", "192.168.0.0/16", "198.18.0.0/15", "198.51.100.0/24", "203.0.113.0/24", "224.0.0.0/4", "240.0.0.0/4",
        "255.255.255.255/32", "::/128", "::1/128", "fc00::/7", "fe80::/10", "ff00::/8",
    ]

    typealias Json = JsonText.Value

    /// Конфиг для выбранного сервера, настроек и дескриптора TUN.
    public static func build(
        _ server: ServerDefinition, _ settings: CoreSettings, tunFd: Int32, subscriptionProxy: SubscriptionProxy? = nil
    ) -> XrayConfigResult {
        let proxy: Json
        switch outbound(server, settings) {
        case let .success(value):
            proxy = value
        case let .failure(rejection):
            return XrayConfigResult(json: nil, rejection: rejection.reason)
        }

        let log: Json = .object([
            ("loglevel", .string(settings.debugLog ? "debug" : "warning")), ("access", .string("none")), ("dnsLog", .bool(false)),
        ])
        let dns: Json = .object([("servers", .array([.string(settings.dns)])), ("queryStrategy", .string(settings.ipv6 ? "UseIP" : "UseIPv4"))])
        // Домен из TLS, HTTP и QUIC — только для правил: соединение идет туда, куда просили.
        let sniffing: Json = .object([
            ("enabled", .bool(settings.sniffing)),
            ("destOverride", .array([.string("http"), .string("tls"), .string("quic")])),
            ("routeOnly", .bool(true)),
        ])
        let tun: Json = .object([
            ("tag", .string(tunTag)),
            ("protocol", .string("tun")),
            ("settings", .object([("name", .string("xray0")), ("mtu", .int(settings.mtu))])),
            ("sniffing", sniffing),
        ])
        let direct: Json = .object([
            ("tag", .string(directTag)),
            ("protocol", .string("freedom")),
            ("settings", .object([("domainStrategy", .string(settings.ipv6 ? "AsIs" : "UseIPv4"))])),
        ])
        let block: Json = .object([("tag", .string(blockTag)), ("protocol", .string("blackhole"))])
        // Запросы не A и AAAA — сразу отказ REFUSED, как раньше «nonIPQuery: reject» (dnsRules); без правил Xray 26 — пустой NOERROR.
        let dnsOut: Json = .object([("tag", .string(dnsTag)), ("protocol", .string("dns")), ("settings", .object([("rules", dnsRules)]))])
        var inbounds = [tun]
        var routingRules = rules(settings)
        if let subscriptionProxy, subscriptionProxy.valid {
            inbounds.append(.object([
                ("tag", .string(subscriptionTag)), ("listen", .string(SubscriptionProxy.host)), ("port", .int(subscriptionProxy.port)),
                ("protocol", .string("http")),
                ("settings", .object([("accounts", .array([.object([
                    ("user", .string(subscriptionProxy.user)), ("pass", .string(subscriptionProxy.password)),
                ])]))])),
            ]))
            // Обновление «только через VPN» всегда через выбранный сервер: ни частная сеть, ни пользовательские правила
            // не могут отправить его напрямую. HTTP proxy передает Xray имя, DNS приложения не нужен.
            routingRules.insert(.object([
                ("type", .string("field")), ("inboundTag", .array([.string(subscriptionTag)])), ("outboundTag", .string(proxyTag)),
            ]), at: 0)
        }
        let routing: Json = .object([("domainStrategy", .string("IPIfNonMatch")), ("rules", .array(routingRules))])
        let config: Json = .object([
            ("log", log),
            ("env", .object([("xray.tun.fd", .string(String(tunFd)))])),
            ("dns", dns),
            ("inbounds", .array(inbounds)),
            ("outbounds", .array([proxy, direct, block, dnsOut])),
            ("routing", routing),
        ])
        return XrayConfigResult(json: JsonText.write(config), rejection: nil)
    }

    private static func rules(_ settings: CoreSettings) -> [Json] {
        var rules: [Json] = [
            // DNS из туннеля — к любому адресу: отвечает выход DNS по настройкам этого конфига, через сервер.
            .object([("type", .string("field")), ("inboundTag", .array([.string(tunTag)])), ("port", .string("53")), ("outboundTag", .string(dnsTag))]),
        ]

        // Свой DNS в частной сети (Pi-hole): запросы самого Xray к нему — напрямую, и при закрытой локальной сети: через
        // сервер частный адрес не достать. Раньше блока локальной сети, иначе DNS ядра уходит в блок целиком.
        if let lanDns = settings.lanDns, !lanDns.isEmpty {
            rules.append(.object([("type", .string("field")), ("ip", .array([.string(lanDns)])), ("port", .string("53")), ("outboundTag", .string(directTag))]))
        }

        // Локальная сеть: разрешена — напрямую, иначе — блок (по умолчанию запрещена, модель угроз).
        rules.append(.object([
            ("type", .string("field")), ("ip", .array(localNetworks.map { .string($0) })), ("outboundTag", .string(settings.allowLan ? directTag : blockTag)),
        ]))
        if !settings.ipv6 {
            // «Только IPv4»: адреса IPv6 из туннеля — в блок: мимо сервера им не уйти, а через сервер — не просили.
            rules.append(.object([
                ("type", .string("field")), ("inboundTag", .array([.string(tunTag)])), ("ip", .array([.string("::/0")])), ("outboundTag", .string(blockTag)),
            ]))
        }

        return rules
    }

    /// Отказ ядра как ошибка — для `Result`.
    struct Refusal: Error {
        let reason: CoreRejection
    }

    /// Выход сервера — те же поля, что собирает десктоп; метка — `proxyTag`.
    static func outbound(_ server: ServerDefinition, _ settings: CoreSettings) -> Result<Json, Refusal> {
        let credentials = server.credentials
        let protocolSettings: Json
        let name: String
        switch server.protocol {
        case .vless where !(credentials.uuid ?? "").isEmpty:
            var user: [(String, Json)] = [("id", .string(credentials.uuid ?? "")), ("encryption", .string("none"))]
            if let flow = server.options.flow, !flow.isEmpty {
                user.append(("flow", .string(flow)))
            }

            protocolSettings = vnext(server, user)
            name = "vless"
        case .vmess where !(credentials.uuid ?? "").isEmpty:
            protocolSettings = vnext(server, [("id", .string(credentials.uuid ?? "")), ("security", .string(server.options.cipher ?? "auto"))])
            name = "vmess"
        case .trojan where !(credentials.password ?? "").isEmpty:
            protocolSettings = servers([("address", .string(server.host)), ("port", .int(server.port)), ("password", .string(credentials.password ?? ""))])
            name = "trojan"
        case .shadowsocks where !(credentials.password ?? "").isEmpty && !(server.options.cipher ?? "").isEmpty:
            protocolSettings = servers([
                ("address", .string(server.host)), ("port", .int(server.port)), ("method", .string(server.options.cipher ?? "")),
                ("password", .string(credentials.password ?? "")),
            ])
            name = "shadowsocks"
        case .wireGuard where !(credentials.privateKey ?? "").isEmpty && !(server.options.peerPublicKey ?? "").isEmpty:
            return .success(wireGuard(server))
        case .hysteria2, .tuic:
            return .failure(Refusal(reason: .unsupportedProtocol))
        default:
            return .failure(Refusal(reason: .missingCredentials))
        }

        guard let streamSettings = Self.stream(server, settings) else {
            return .failure(Refusal(reason: .unsupportedTransport))
        }

        if unencrypted(server) {
            return .failure(Refusal(reason: .unencrypted))
        }

        var outbound: [(String, Json)] = [
            ("tag", .string(proxyTag)), ("protocol", .string(name)), ("settings", protocolSettings), ("streamSettings", streamSettings),
        ]

        // Mux несовместим с XTLS Vision (flow) — с ним не включается.
        if settings.mux, (server.options.flow ?? "").isEmpty {
            outbound.append(("mux", .object([("enabled", .bool(true)), ("concurrency", .int(8))])))
        }

        return .success(.object(outbound))
    }

    /// Частные зоны имен — те же, что у Xray-core (`geodata.GetPrivateDomainMatcher`).
    private static let privateZones = ["lan", "localdomain", "example", "invalid", "localhost", "test", "local", "home.arpa", "internal"]

    /// Сервер без шифрования до него: VLESS (шифрование «none») или Trojan без TLS и REALITY, а адрес сервера — не частный:
    /// ни адрес из `localNetworks`, ни имя в частной зоне (lan, local, internal, home.arpa и другие) или без точки. То же
    /// правило, что у Xray-core в libXray (`validateOutboundTransportSecurity`), у Android и десктопа.
    public static func unencrypted(_ server: ServerDefinition) -> Bool {
        guard server.protocol == .vless || server.protocol == .trojan, server.security.kind == .none else {
            return false
        }

        let address = server.host.hasPrefix("[") && server.host.hasSuffix("]") ? String(server.host.dropFirst().dropLast()) : server.host
        if var bytes = IpAddress.parse(address) {
            // IPv4 внутри IPv6 (::ffff:a.b.c.d) — как IPv4.
            if bytes.count == 16, bytes[0..<10].allSatisfy({ $0 == 0 }), bytes[10] == 0xFF, bytes[11] == 0xFF {
                bytes = Array(bytes[12..<16])
            }

            return !localNetworks.contains { inNetwork(bytes, $0) }
        }

        var name = address.lowercased()
        while name.hasSuffix(".") {
            name.removeLast()
        }

        return !privateZones.contains { name == $0 || name.hasSuffix("." + $0) } && !dotless(name)
    }

    /// Имя без точки — как у Xray: буква, затем до 62 букв, цифр и дефисов, в конце не дефис.
    private static func dotless(_ name: String) -> Bool {
        guard (1...63).contains(name.count), let first = name.first, first.isASCII, first.isLetter, name.last != "-" else {
            return false
        }

        return name.allSatisfy { $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "-") }
    }

    private static func inNetwork(_ address: [UInt8], _ cidr: String) -> Bool {
        let parts = cidr.split(separator: "/")
        guard parts.count == 2, let base = IpAddress.parse(String(parts[0])), base.count == address.count, let bits = Int(parts[1]) else {
            return false
        }

        return (0..<bits).allSatisfy { bit in
            let mask = UInt8(0x80) >> UInt8(bit % 8)
            return address[bit / 8] & mask == base[bit / 8] & mask
        }
    }

    private static func vnext(_ server: ServerDefinition, _ user: [(String, Json)]) -> Json {
        .object([("vnext", .array([.object([("address", .string(server.host)), ("port", .int(server.port)), ("users", .array([.object(user)]))])]))])
    }

    private static func servers(_ server: [(String, Json)]) -> Json {
        .object([("servers", .array([.object(server)]))])
    }

    private static func wireGuard(_ server: ServerDefinition) -> Json {
        let endpoint = server.host.contains(":") ? "[\(server.host)]:\(server.port)" : "\(server.host):\(server.port)"
        var peer: [(String, Json)] = [("publicKey", .string(server.options.peerPublicKey ?? "")), ("endpoint", .string(endpoint))]
        if let preshared = server.credentials.presharedKey, !preshared.isEmpty {
            peer.append(("preSharedKey", .string(preshared)))
        }

        // Свой стек TCP/IP Xray, а не адаптер wgN: у расширения прав на него нет, а так конфиг тот же, что у десктопа и Android.
        var settings: [(String, Json)] = [
            ("secretKey", .string(server.credentials.privateKey ?? "")), ("peers", .array([.object(peer)])), ("noKernelTun", .bool(true)),
        ]
        if let addresses = server.options.addresses, !addresses.isEmpty {
            settings.append(("address", .array(addresses.map { .string($0) })))
        }

        if let mtu = server.options.mtu {
            settings.append(("mtu", .int(mtu)))
        }

        return .object([("tag", .string(proxyTag)), ("protocol", .string("wireguard")), ("settings", .object(settings))])
    }

    private static func stream(_ server: ServerDefinition, _ settings: CoreSettings) -> Json? {
        let transport = server.transport
        let network: String
        switch transport.kind {
        case .tcp: network = "raw"
        case .webSocket: network = "ws"
        case .grpc: network = "grpc"
        case .httpUpgrade: network = "httpupgrade"
        case .xhttp: network = "xhttp"
        default: return nil
        }

        var stream: [(String, Json)] = [("network", .string(network))]
        switch transport.kind {
        case .webSocket, .httpUpgrade, .xhttp:
            var path: [(String, Json)] = [("path", .string(transport.path ?? "/"))]
            if let host = transport.host, !host.isEmpty {
                path.append(("host", .string(host)))
            }

            if transport.kind == .xhttp, let mode = transport.mode, !mode.isEmpty {
                path.append(("mode", .string(mode)))
            }

            stream.append((network + "Settings", .object(path)))
        case .grpc:
            stream.append(("grpcSettings", .object([("serviceName", .string(transport.serviceName ?? ""))])))
        default:
            break
        }

        let security = server.security
        let fingerprint = settings.fingerprint ?? security.fingerprint
        switch security.kind {
        case .tls:
            var tls: [(String, Json)] = [("serverName", .string(security.sni ?? server.host))]
            if let fingerprint, !fingerprint.isEmpty {
                tls.append(("fingerprint", .string(fingerprint)))
            }

            if let alpn = security.alpn, !alpn.isEmpty {
                tls.append(("alpn", .array(alpn.map { .string($0) })))
            }

            // Сертификат проверяется всегда: allowInsecure из ссылки ядро libXray (Xray-core 26.x) не принимает вовсе — конфиг
            // с ним не запускается, — а без проверки подключение можно перехватить (решение 477). Самоподписанный сертификат
            // сервера закрепляется в ссылке — «pcs» и «vcn» (решение 480).
            if let pinned = security.pinnedPeerCertSha256, !pinned.isEmpty {
                tls.append(("pinnedPeerCertSha256", .string(pinned.joined(separator: ","))))
            }

            if let names = security.verifyPeerCertByName, !names.isEmpty {
                tls.append(("verifyPeerCertByName", .string(names.joined(separator: ","))))
            }

            stream.append(("security", .string("tls")))
            stream.append(("tlsSettings", .object(tls)))
        case .reality:
            stream.append(("security", .string("reality")))
            stream.append(("realitySettings", .object([
                ("serverName", .string(security.sni ?? "")),
                // REALITY без отпечатка не работает: из профиля или настройки, иначе — chrome.
                ("fingerprint", .string(fingerprint.flatMap { $0.isEmpty ? nil : $0 } ?? "chrome")),
                ("publicKey", .string(security.realityPublicKey ?? "")),
                ("shortId", .string(security.realityShortId ?? "")),
                ("spiderX", .string(security.realitySpiderX ?? "")),
            ])))
        case .none:
            stream.append(("security", .string("none")))
        }

        return .object(stream)
    }

    /// Запрос к libXray: версия API 3, метод и данные.
    public static func invoke(_ method: String, _ payload: [(String, JsonText.Value)] = []) -> String {
        JsonText.write(.object([("apiVersion", .int(3)), ("method", .string(method)), ("payload", .object(payload))]))
    }

    /// Запуск ядра: конфиг передается текстом, файла с секретами на диске нет.
    public static func runRequest(_ configJson: String) -> String {
        invoke("runXray", [("xrayJson", .string(configJson))])
    }
}

/// Ответ libXray: `{"success": …, "data": …, "error": "…"}` — удалось ли и текст ошибки.
public struct XrayResponse: Equatable, Sendable {
    public let success: Bool
    public let error: String?

    public init(success: Bool, error: String?) {
        self.success = success
        self.error = error
    }

    public static func parse(_ text: String?) -> XrayResponse {
        guard let parsed = try? JsonTree.parse(text ?? ""), case let .object(root, _) = parsed.root else {
            return XrayResponse(success: false, error: "Ответ ядра — не JSON")
        }

        var error: String?
        if case let .string(message)? = root["error"], !message.isEmpty {
            error = message
        }

        if case .bool(true)? = root["success"] {
            return XrayResponse(success: true, error: error)
        }

        return XrayResponse(success: false, error: error)
    }

    /// Версия Xray-core из ответа на «xrayVersion» (`{"success": true, "data": {"version": "…"}}`); нет — nil.
    public static func version(_ text: String?) -> String? {
        guard let parsed = try? JsonTree.parse(text ?? ""), case let .object(root, _) = parsed.root, case .bool(true)? = root["success"],
              case let .object(data, _)? = root["data"], case let .string(version)? = data["version"], !version.isEmpty
        else {
            return nil
        }

        return version
    }
}

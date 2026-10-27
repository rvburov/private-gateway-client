/// Конфиг или подписка Clash/Mihomo — как на десктопе и Android (`ClashImport`): YAML со списком proxies. Группы, правила и
/// провайдеры не разбираются — только серверы. Служебные direct и dns пропускаются; номер строки — строка, где начинается
/// сервер.
enum ClashImport {
    static func parse(_ text: String) -> ImportBatch {
        let parsed = YamlTree.parse(text)
        if parsed.errorLine > 0 {
            return ConfigTree.broken(ImportParser.formatClash, line: parsed.errorLine)
        }

        guard case let .object(root, _) = parsed.root, let proxies = root["proxies"], case let .array(items, listLine, _) = proxies else {
            return ImportBatch(format: ImportParser.formatClash, lines: [])
        }

        var lines: [ImportLine] = []
        for item in items {
            if let result = proxy(item) {
                lines.append(ImportLine(number: item.line ?? listLine, result: result))
            }
        }

        return ImportBatch(format: ImportParser.formatClash, lines: lines)
    }

    /// Сервер из элемента proxies; nil — служебный, не сервер.
    private static func proxy(_ node: ConfigNode) -> LinkResult? {
        let type = ConfigTree.text(node, "type")?.lowercased()
        let name = ConfigTree.text(node, "name")
        let host = ConfigTree.text(node, "server")
        let port = ConfigTree.text(node, "port")
        var query = Query()
        ConfigTree.put(&query, "sni", ConfigTree.text(node, "sni") ?? ConfigTree.text(node, "servername"))
        ConfigTree.put(&query, "alpn", ConfigTree.joined(node, "alpn"))
        ConfigTree.put(&query, "allowInsecure", ConfigTree.flag(node, "skip-cert-verify") ? "1" : nil)
        ConfigTree.put(&query, "fp", ConfigTree.text(node, "client-fingerprint"))
        let reality = ConfigTree.object(node, "reality-opts")
        ConfigTree.put(&query, "pbk", ConfigTree.text(reality, "public-key"))
        ConfigTree.put(&query, "sid", ConfigTree.text(reality, "short-id"))
        transport(node, &query)
        switch type {
        case "direct", "dns":
            return nil
        case "vless":
            query["security"] = security(node, reality, byDefault: "none")
            ConfigTree.put(&query, "flow", ConfigTree.text(node, "flow"))
            ConfigTree.put(&query, "encryption", ConfigTree.text(node, "encryption"))
            return ConfigTree.server(.vless, [ConfigTree.text(node, "uuid")], host: host, port: port, query: query, name: name)
        case "vmess":
            query["security"] = security(node, reality, byDefault: "none")
            return LinkParser.vmessFrom(
                name: name, host: host, port: port, alterId: ConfigTree.text(node, "alterId"), cipher: ConfigTree.text(node, "cipher"), header: nil,
                query: query, uuid: ConfigTree.text(node, "uuid")
            )
        case "trojan":
            // Trojan в Clash — всегда TLS; trojan-go с шифрованием Shadowsocks ядро не умеет.
            query["security"] = security(node, reality, byDefault: "tls")
            if ConfigTree.flag(ConfigTree.object(node, "ss-opts"), "enabled") {
                return LinkParser.fail(ImportErrors.unsupportedParameter)
            }

            return ConfigTree.server(.trojan, [ConfigTree.text(node, "password")], host: host, port: port, query: query, name: name)
        case "ss":
            ConfigTree.put(&query, "plugin", ConfigTree.text(node, "plugin"))
            return ConfigTree.server(
                .shadowsocks, [ConfigTree.text(node, "cipher"), ConfigTree.text(node, "password")], host: host, port: port, query: query, name: name
            )
        case "hysteria2":
            query["security"] = "tls"
            ConfigTree.put(&query, "obfs", ConfigTree.text(node, "obfs"))
            ConfigTree.put(&query, "obfs-password", ConfigTree.text(node, "obfs-password"))
            return ConfigTree.server(.hysteria2, [ConfigTree.text(node, "password")], host: host, port: port, query: query, name: name)
        case "tuic":
            query["security"] = "tls"
            ConfigTree.put(&query, "congestion_control", ConfigTree.text(node, "congestion-controller"))
            return ConfigTree.server(
                .tuic, [ConfigTree.text(node, "uuid"), ConfigTree.text(node, "password")], host: host, port: port, query: query, name: name
            )
        case "wireguard":
            return wireGuard(node, name)
        case nil:
            return LinkParser.fail(ImportErrors.badLink)
        default:
            return LinkParser.fail(ImportErrors.unknownScheme)
        }
    }

    /// tls: true — TLS, с reality-opts — REALITY; без tls — защита протокола по умолчанию.
    private static func security(_ node: ConfigNode, _ reality: ConfigNode?, byDefault: String) -> String {
        if reality != nil {
            return "reality"
        }

        if ConfigTree.text(node, "tls") == nil {
            return byDefault
        }

        return ConfigTree.flag(node, "tls") ? "tls" : "none"
    }

    /// network и *-opts → параметры ссылки; ws с v2ray-http-upgrade — это HTTPUpgrade.
    private static func transport(_ node: ConfigNode, _ query: inout Query) {
        let network = ConfigTree.text(node, "network")?.lowercased()
        let ws = ConfigTree.object(node, "ws-opts")
        let xhttp = ConfigTree.object(node, "xhttp-opts")
        ConfigTree.put(&query, "type", network == "ws" && ConfigTree.flag(ws, "v2ray-http-upgrade") ? "httpupgrade" : network)
        ConfigTree.put(&query, "path", ConfigTree.text(ws, "path") ?? ConfigTree.text(xhttp, "path"))
        ConfigTree.put(&query, "host", ConfigTree.header(ConfigTree.object(ws, "headers"), "Host") ?? ConfigTree.text(xhttp, "host"))
        ConfigTree.put(&query, "serviceName", ConfigTree.text(ConfigTree.object(node, "grpc-opts"), "grpc-service-name"))
    }

    /// WireGuard: поля сервера или один peers; AmneziaWG ядро не поддерживает.
    private static func wireGuard(_ node: ConfigNode, _ name: String?) -> LinkResult {
        let peers = ConfigTree.array(node, "peers")
        if let peers, peers.count > 1 {
            return LinkParser.fail(ImportErrors.unsupportedParameter)
        }

        if ConfigTree.object(node, "amnezia-wg-option") != nil {
            return LinkParser.fail(ImportErrors.unsupportedParameter)
        }

        let peer: ConfigNode = peers?.count == 1 ? peers![0] : node
        var query = Query()
        ConfigTree.put(&query, "publickey", ConfigTree.text(peer, "public-key"))
        ConfigTree.put(&query, "presharedkey", ConfigTree.text(peer, "pre-shared-key"))
        ConfigTree.put(&query, "address", [ConfigTree.text(node, "ip"), ConfigTree.text(node, "ipv6")].compactMap { $0 }.joined(separator: ","))
        ConfigTree.put(&query, "mtu", ConfigTree.text(node, "mtu"))
        return ConfigTree.server(
            .wireGuard, [ConfigTree.text(node, "private-key")], host: ConfigTree.text(peer, "server"), port: ConfigTree.text(peer, "port"),
            query: query, name: name
        )
    }
}

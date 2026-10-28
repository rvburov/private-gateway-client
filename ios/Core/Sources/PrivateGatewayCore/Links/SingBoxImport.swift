/// Конфиг или подписка sing-box — как на десктопе и Android (`SingBoxImport`): JSON с массивами outbounds и endpoints
/// (WireGuard с версии 1.11) или просто массив выходов. Служебные выходы (direct, block, dns, selector, urltest)
/// пропускаются; номер строки — строка, где начинается выход.
enum SingBoxImport {
    private static let lists = ["outbounds", "endpoints"]

    static func parse(_ text: String) -> ImportBatch {
        let root: ConfigNode
        do {
            root = try JsonTree.parse(text).root
        } catch let error as JsonTree.SyntaxError {
            return ConfigTree.broken(ImportParser.formatSingBox, line: error.line)
        } catch {
            return ConfigTree.broken(ImportParser.formatSingBox, line: 1)
        }

        var lines: [ImportLine] = []
        let sources: [ConfigNode?]
        if case .array(_, _, _) = root {
            sources = [root]
        } else if case let .object(map, _) = root {
            sources = lists.map { map[$0] }
        } else {
            sources = []
        }

        for case let .array(items, _, itemLines)? in sources {
            for (i, item) in items.enumerated() {
                if let result = outbound(item) {
                    lines.append(ImportLine(number: i < itemLines.count ? itemLines[i] : 1, result: result))
                }
            }
        }

        // Сортировка устойчивая: выходы на одной строке остаются в своем порядке.
        let sorted = lines.enumerated().sorted { ($0.element.number, $0.offset) < ($1.element.number, $1.offset) }.map(\.element)
        return ImportBatch(format: ImportParser.formatSingBox, lines: sorted)
    }

    /// Сервер из выхода; nil — служебный выход, не сервер.
    private static func outbound(_ node: ConfigNode) -> LinkResult? {
        let type = ConfigTree.text(node, "type")?.lowercased()
        let name = ConfigTree.text(node, "tag")
        let host = ConfigTree.text(node, "server")
        let port = ConfigTree.text(node, "server_port")
        var query = Query()
        if let type, ["vless", "vmess", "trojan", "hysteria2", "tuic"].contains(type), ownCertificate(ConfigTree.object(node, "tls")) {
            return LinkParser.fail(ImportErrors.unsupportedParameter)
        }

        tls(ConfigTree.object(node, "tls"), &query)
        transport(ConfigTree.object(node, "transport"), &query)
        switch type {
        case "direct", "block", "dns", "selector", "urltest":
            return nil
        case "vless":
            ConfigTree.put(&query, "flow", ConfigTree.text(node, "flow"))
            return ConfigTree.server(.vless, [ConfigTree.text(node, "uuid")], host: host, port: port, query: query, name: name)
        case "vmess":
            return LinkParser.vmessFrom(
                name: name, host: host, port: port, alterId: ConfigTree.text(node, "alter_id"), cipher: ConfigTree.text(node, "security"),
                header: nil, query: query, uuid: ConfigTree.text(node, "uuid")
            )
        case "trojan":
            return ConfigTree.server(.trojan, [ConfigTree.text(node, "password")], host: host, port: port, query: query, name: name)
        case "shadowsocks":
            ConfigTree.put(&query, "plugin", ConfigTree.text(node, "plugin"))
            return ConfigTree.server(
                .shadowsocks, [ConfigTree.text(node, "method"), ConfigTree.text(node, "password")], host: host, port: port, query: query, name: name
            )
        case "hysteria2":
            // У Hysteria2 и TUIC TLS всегда: поля tls действуют и без enabled.
            query["security"] = "tls"
            let obfs = ConfigTree.object(node, "obfs")
            ConfigTree.put(&query, "obfs", ConfigTree.text(obfs, "type"))
            ConfigTree.put(&query, "obfs-password", ConfigTree.text(obfs, "password"))
            return ConfigTree.server(.hysteria2, [ConfigTree.text(node, "password")], host: host, port: port, query: query, name: name)
        case "tuic":
            query["security"] = "tls"
            ConfigTree.put(&query, "congestion_control", ConfigTree.text(node, "congestion_control"))
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

    /// WireGuard: выход до 1.11 (server, peer_public_key) или endpoint с одним peers.
    private static func wireGuard(_ node: ConfigNode, _ name: String?) -> LinkResult {
        let peers = ConfigTree.array(node, "peers")
        if let peers, peers.count > 1 {
            return LinkParser.fail(ImportErrors.unsupportedParameter)
        }

        let peer = peers?.count == 1 ? peers?[0] : nil
        var query = Query()
        ConfigTree.put(&query, "publickey", peer == nil ? ConfigTree.text(node, "peer_public_key") : ConfigTree.text(peer, "public_key"))
        ConfigTree.put(&query, "presharedkey", peer == nil ? ConfigTree.text(node, "pre_shared_key") : ConfigTree.text(peer, "pre_shared_key"))
        ConfigTree.put(&query, "address", ConfigTree.joined(node, "address") ?? ConfigTree.joined(node, "local_address"))
        ConfigTree.put(&query, "mtu", ConfigTree.text(node, "mtu"))
        if let peer {
            return ConfigTree.server(
                .wireGuard, [ConfigTree.text(node, "private_key")], host: ConfigTree.text(peer, "address"), port: ConfigTree.text(peer, "port"),
                query: query, name: name
            )
        }

        return ConfigTree.server(
            .wireGuard, [ConfigTree.text(node, "private_key")], host: ConfigTree.text(node, "server"), port: ConfigTree.text(node, "server_port"),
            query: query, name: name
        )
    }

    /// tls → параметры ссылки: без tls или enabled=false — явно без защиты.
    /// Свой сертификат сервера или закрепленный ключ (certificate, certificate_path, certificate_public_key_sha256) ссылкой не
    /// передать: «pcs» — SHA-256 всего сертификата, а не PEM и не ключа. Подключить с обычной проверкой вместо закрепленной — молча
    /// ослабить защиту, поэтому такой выход — ошибка, как у десктопа (решение 483).
    private static func ownCertificate(_ tls: ConfigNode?) -> Bool {
        ["certificate", "certificate_path", "certificate_public_key_sha256"].contains { !ConfigTree.list(tls, $0).isEmpty }
    }

    private static func tls(_ tls: ConfigNode?, _ query: inout Query) {
        let reality = ConfigTree.object(tls, "reality")
        query["security"] = ConfigTree.flag(reality, "enabled") ? "reality" : ConfigTree.flag(tls, "enabled") ? "tls" : "none"
        ConfigTree.put(&query, "sni", ConfigTree.text(tls, "server_name"))
        ConfigTree.put(&query, "alpn", ConfigTree.joined(tls, "alpn"))
        ConfigTree.put(&query, "allowInsecure", ConfigTree.flag(tls, "insecure") ? "1" : nil)
        let utls = ConfigTree.object(tls, "utls")
        ConfigTree.put(&query, "fp", ConfigTree.flag(utls, "enabled") ? ConfigTree.text(utls, "fingerprint") ?? "chrome" : nil)
        ConfigTree.put(&query, "pbk", ConfigTree.text(reality, "public_key"))
        ConfigTree.put(&query, "sid", ConfigTree.text(reality, "short_id"))
    }

    /// transport → параметры ссылки; «http» и «quic» разбор ссылок не принимает — так и задумано.
    private static func transport(_ transport: ConfigNode?, _ query: inout Query) {
        guard let transport else {
            return
        }

        ConfigTree.put(&query, "type", ConfigTree.text(transport, "type"))
        ConfigTree.put(&query, "path", ConfigTree.text(transport, "path"))
        ConfigTree.put(&query, "host", ConfigTree.header(ConfigTree.object(transport, "headers"), "Host") ?? ConfigTree.text(transport, "host"))
        ConfigTree.put(&query, "serviceName", ConfigTree.text(transport, "service_name"))
    }
}

import Foundation

/// Ограниченные параметры Xray из подписки: без готового конфига, файлов и управления сокетами.
public enum TransportParameterRules {
    public static let maxExtraBytes = 32768

    /// Заголовки extra — учетные данные, включая отдельную загрузку XHTTP. Только проверенный ограниченный объект.
    public static func sensitiveExtraValues(_ server: ServerDefinition) -> [String] {
        guard let text = normalizeExtra(server.transport.extraJson), let parsed = try? JsonTree.parse(text) else { return [] }
        var values = Set<String>()
        func visit(_ node: ConfigNode) {
            guard case let .object(fields, _) = node else { return }
            if case let .object(headers, _)? = fields["headers"] {
                for value in headers.values {
                    if case let .string(text) = value, !text.isEmpty { values.insert(text) }
                }
            }
            for (key, value) in fields where key != "headers" { visit(value) }
        }
        visit(parsed.root)
        return values.sorted()
    }

    private static let xhttpFields: Set<String> = [
        "headers", "xPaddingBytes", "xPaddingObfsMode", "xPaddingKey", "xPaddingHeader", "xPaddingPlacement", "xPaddingMethod",
        "uplinkHTTPMethod", "sessionIDPlacement", "sessionIDKey", "sessionIDTable", "sessionIDLength", "seqPlacement", "seqKey",
        "uplinkDataPlacement", "uplinkDataKey", "uplinkChunkSize", "noGRPCHeader", "noSSEHeader", "scMaxEachPostBytes",
        "scMinPostsIntervalMs", "scMaxBufferedPosts", "scStreamUpServerSecs", "serverMaxHeaderBytes", "xmux", "downloadSettings",
    ]
    private static let streamFields: Set<String> = ["address", "port", "network", "security", "tlsSettings", "realitySettings", "xhttpSettings", "splithttpSettings"]
    private static let tlsFields: Set<String> = ["serverName", "fingerprint", "alpn", "pinnedPeerCertSha256", "verifyPeerCertByName", "minVersion", "maxVersion"]
    private static let realityFields: Set<String> = ["serverName", "fingerprint", "publicKey", "password", "shortId", "spiderX", "mldsa65Verify"]
    private static let xmuxFields: Set<String> = ["maxConcurrency", "maxConnections", "cMaxReuseTimes", "hMaxRequestTimes", "hMaxReusableSecs", "hKeepAlivePeriod"]

    /// Шифрование VLESS закрепленного Xray 26.10.21; неизвестное значение не заменяется none.
    public static func validEncryption(_ text: String?) -> Bool {
        guard let text, text != "none" else { return true }
        guard text.utf8.count <= 4096 else { return false }
        let parts = text.components(separatedBy: ".")
        guard (4...12).contains(parts.count), parts[0] == "mlkem768x25519plus",
              ["native", "xorpub", "random"].contains(parts[1]), ["1rtt", "0rtt"].contains(parts[2]) else { return false }
        var key = false
        for part in parts.dropFirst(3) {
            if part.utf8.count < 20 {
                if key || part.isEmpty || !part.utf8.allSatisfy({ (48...57).contains($0) || $0 == 45 }) { return false }
                continue
            }
            guard part.utf8.allSatisfy({ (48...57).contains($0) || (65...90).contains($0) || (97...122).contains($0) || $0 == 45 || $0 == 95 }) else { return false }
            let base64 = part.replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
                + String(repeating: "=", count: (4 - part.utf8.count % 4) % 4)
            guard let bytes = Data(base64Encoded: base64), bytes.count == 32 || bytes.count == 1184 else { return false }
            key = true
        }
        return key
    }

    /// nil — параметр отсутствует или отклонен. Указанный неверный extra обязан завершить импорт ошибкой.
    public static func normalizeExtra(_ text: String?) -> String? {
        extraValue(text).map(JsonText.write)
    }

    static func extraQuery(_ node: ConfigNode?, into query: inout Query, yamlScalars: Bool = false) {
        guard case let .object(fields, _) = node, let input = fields["extra"] else { return }
        if case let .string(text) = input { query["extra"] = text; return }
        func coerce(_ value: ConfigNode, key: String? = nil) -> ConfigNode {
            if key == "headers" { return value }
            if case let .object(fields, line) = value {
                return .object(fields.mapValues { $0 }.reduce(into: [String: ConfigNode]()) { $0[$1.key] = coerce($1.value, key: $1.key) }, line: line)
            }
            if case let .string(text) = value, let key {
                if ["noGRPCHeader", "noSSEHeader", "xPaddingObfsMode"].contains(key), let flag = Bool(text.lowercased()) { return .bool(flag) }
                if ["port", "scMaxBufferedPosts", "serverMaxHeaderBytes", "hKeepAlivePeriod"].contains(key), let number = Int(text) { return .number(String(number)) }
            }
            return value
        }
        let extra = yamlScalars ? coerce(input) : input
        query["extra"] = validObject(extra, fields: xhttpFields, depth: 0) ? JsonText.write(canonical(extra)) : "null"
    }

    static func extraValue(_ text: String?) -> JsonText.Value? {
        guard let text, text.utf8.count <= maxExtraBytes, let parsed = try? JsonTree.parse(text),
              validObject(parsed.root, fields: xhttpFields, depth: 0) else { return nil }
        return canonical(parsed.root)
    }

    /// UDP к серверу нельзя проверить через вход SOCKS, разрешающий только TCP. REALITY всегда выбирает HTTP/2.
    public static func requiresUdpProbe(_ server: ServerDefinition) -> Bool {
        if [.hysteria2, .tuic, .wireGuard].contains(server.protocol) || server.transport.kind == .quic { return true }
        guard server.transport.kind == .xhttp else { return false }
        if server.security.kind == .tls, server.security.alpn == ["h3"] { return true }
        func downloadUsesUdp(_ node: ConfigNode) -> Bool {
            guard case let .object(fields, _) = node, case let .object(download, _)? = fields["downloadSettings"] else { return false }
            if case .string("tls")? = download["security"], case let .object(tls, _)? = download["tlsSettings"],
               case let .array(alpn, _, _)? = tls["alpn"], alpn.count == 1, case .string("h3") = alpn[0] { return true }
            for key in ["xhttpSettings", "splithttpSettings"] {
                guard case let .object(nested, line)? = download[key] else { continue }
                if downloadUsesUdp(nested["extra"] ?? .object(nested, line: line)) { return true }
            }
            return false
        }
        guard let text = normalizeExtra(server.transport.extraJson), let parsed = try? JsonTree.parse(text) else { return false }
        return downloadUsesUdp(parsed.root)
    }

    /// Ядру нужен полный адрес отдельной загрузки XHTTP; пустые поля наследуют основной узел.
    static func extraForServer(_ server: ServerDefinition) -> JsonText.Value? {
        guard let text = normalizeExtra(server.transport.extraJson), let root = try? JsonTree.parse(text) else { return nil }
        return canonical(prepare(root.root, host: server.host, port: server.port))
    }

    private static func prepare(_ node: ConfigNode, host: String, port: Int) -> ConfigNode {
        guard case let .object(original, line) = node, case let .object(originalDownload, downloadLine)? = original["downloadSettings"] else { return node }
        var fields = original
        var download = originalDownload
        let address: String
        if case let .string(value)? = download["address"], !value.isEmpty { address = value } else { address = host }
        let targetPort: Int
        if case let .number(value)? = download["port"], let number = Int(value) { targetPort = number } else { targetPort = port }
        download["address"] = .string(address)
        download["port"] = .number(String(targetPort))
        for key in ["xhttpSettings", "splithttpSettings"] {
            guard case let .object(originalChild, childLine)? = download[key] else { continue }
            var child = originalChild
            if let extra = child["extra"] {
                child["extra"] = prepare(extra, host: address, port: targetPort)
                download[key] = .object(child, line: childLine)
            } else {
                download[key] = prepare(.object(child, line: childLine), host: address, port: targetPort)
            }
        }
        fields["downloadSettings"] = .object(download, line: downloadLine)
        return .object(fields, line: line)
    }

    private static func canonical(_ node: ConfigNode) -> JsonText.Value {
        switch node {
        case let .object(fields, _): return .object(fields.keys.sorted().map { ($0, canonical(fields[$0]!)) })
        case let .array(values, _, _): return .array(values.map(canonical))
        case let .string(text): return .string(text)
        case let .number(text): return .int(Int(text)!)
        case let .bool(flag): return .bool(flag)
        case .null: return .null
        }
    }

    private static func validObject(_ node: ConfigNode, fields: Set<String>, depth: Int) -> Bool {
        guard depth <= 8, case let .object(values, _) = node else { return false }
        for (name, value) in values {
            guard fields.contains(name) else { return false }
            let valid: Bool
            switch name {
            case "downloadSettings": valid = validObject(value, fields: streamFields, depth: depth + 1)
            case "xhttpSettings", "splithttpSettings":
                guard case let .object(nested, _) = value else { return false }
                valid = nested.allSatisfy { key, item in
                    key == "extra" ? validObject(item, fields: xhttpFields, depth: depth + 2)
                        : validObject(.object([key: item], line: 1), fields: xhttpFields.union(["path", "host", "mode"]), depth: depth + 1)
                }
            case "tlsSettings": valid = validObject(value, fields: tlsFields, depth: depth + 1)
            case "realitySettings": valid = validObject(value, fields: realityFields, depth: depth + 1)
            case "xmux": valid = validObject(value, fields: xmuxFields, depth: depth + 1)
            case "headers":
                if case let .object(headers, _) = value {
                    valid = headers.allSatisfy { key, item in
                        if case .string = item { return key.lowercased() != "host" }
                        return false
                    }
                } else { valid = false }
            case "alpn":
                if case let .array(items, _, _) = value { valid = items.allSatisfy { if case .string = $0 { return true }; return false } }
                else { valid = false }
            case "network":
                if case let .string(text) = value { valid = ["xhttp", "splithttp"].contains(text) } else { valid = false }
            case "security":
                if case let .string(text) = value { valid = ["none", "tls", "reality"].contains(text) } else { valid = false }
            case "address":
                if case let .string(text) = value {
                    valid = !text.isEmpty && text.utf8.count <= 253 && !text.unicodeScalars.contains { CharacterSet.whitespacesAndNewlines.contains($0) || CharacterSet.controlCharacters.contains($0) || "/\\@#?".unicodeScalars.contains($0) }
                } else { valid = false }
            case "port":
                if case let .number(text) = value, let port = Int(text) { valid = (1...65535).contains(port) } else { valid = false }
            default:
                switch value {
                case .string, .bool: valid = true
                case let .number(text): valid = Int(text) != nil
                default: valid = false
                }
            }
            if !valid { return false }
        }
        return true
    }
}

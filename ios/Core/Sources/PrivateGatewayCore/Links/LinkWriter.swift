import Foundation

/// Ссылка из описания сервера — для «Поделиться» (QR-код и текст), как на десктопе и Android (`LinkWriter`). Обратная к
/// [LinkParser]: разбор записанной ссылки дает тот же сервер (тест — на всех эталонах shared/testdata). В ссылке — секрет
/// сервера: показывается только по явному действию пользователя.
public enum LinkWriter {
    public static func write(_ server: ServerDefinition) -> String {
        let credentials = server.credentials
        let address = (server.host.contains(":") ? "[\(server.host)]" : server.host) + ":\(server.port)"
        let name = "#" + escape(nameWithFlag(server))
        switch server.protocol {
        case .vless:
            let vless: [(String, String?)] = [("encryption", "none"), ("flow", server.options.flow)] + stream(server)
            return "vless://\(escape(credentials.uuid))@\(address)\(query(vless))\(name)"
        case .vmess:
            return "vmess://" + Data(vmess(server).utf8).base64EncodedString()
        case .trojan:
            return "trojan://\(escape(credentials.password))@\(address)\(query(stream(server)))\(name)"
        case .shadowsocks:
            let method = server.options.cipher ?? ""

            // SIP002: обычные шифры — base64url(метод:пароль), Shadowsocks 2022 — открытым текстом.
            let userInfo: String
            if method.hasPrefix("2022-") {
                userInfo = escape(method) + ":" + escape(credentials.password)
            } else {
                userInfo = Data("\(method):\(credentials.password ?? "")".utf8).base64EncodedString()
                    .replacingOccurrences(of: "+", with: "-")
                    .replacingOccurrences(of: "/", with: "_")
                    .replacingOccurrences(of: "=", with: "")
            }

            return "ss://\(userInfo)@\(address)\(name)"
        case .hysteria2:
            let hysteria = tls(server) + [("obfs", server.options.obfs), ("obfs-password", credentials.obfsPassword)]
            return "hysteria2://\(escape(credentials.password))@\(address)\(query(hysteria))\(name)"
        case .tuic:
            let tuic = tls(server) + [("congestion_control", server.options.congestionControl)]
            return "tuic://\(escape(credentials.uuid)):\(escape(credentials.password))@\(address)\(query(tuic))\(name)"
        case .wireGuard:
            let addresses = server.options.addresses.flatMap { $0.isEmpty ? nil : $0.joined(separator: ",") }
            let wireguard: [(String, String?)] = [
                ("publickey", server.options.peerPublicKey),
                ("presharedkey", credentials.presharedKey),
                ("address", addresses),
                ("mtu", server.options.mtu.map { String($0) }),
            ]
            return "wireguard://\(escape(credentials.privateKey))@\(address)\(query(wireguard))\(name)"
        }
    }

    /// Название с флагом страны впереди — так его снова разберет [CountryFlag].
    private static func nameWithFlag(_ server: ServerDefinition) -> String {
        let code = Array(server.countryCode.unicodeScalars)
        guard code.count == 2, code.allSatisfy({ ("A"..."Z").contains($0) }),
              let first = Unicode.Scalar(0x1F1E6 + code[0].value - 65), let second = Unicode.Scalar(0x1F1E6 + code[1].value - 65)
        else {
            return server.name
        }

        var flag = String.UnicodeScalarView()
        flag.append(first)
        flag.append(second)
        return server.name.isEmpty ? String(flag) : "\(String(flag)) \(server.name)"
    }

    private static func kind(_ kind: TransportKind) -> String {
        switch kind {
        case .webSocket: return "ws"
        case .grpc: return "grpc"
        case .httpUpgrade: return "httpupgrade"
        case .xhttp: return "xhttp"
        default: return "tcp"
        }
    }

    /// Транспорт и защита — параметры ссылки VLESS и Trojan.
    private static func stream(_ server: ServerDefinition) -> [(String, String?)] {
        let transport = server.transport
        return [
            ("type", kind(transport.kind)),
            ("path", transport.path),
            ("host", transport.host),
            ("serviceName", transport.serviceName),
            ("security", server.security.kind.rawValue),
        ] + tls(server)
    }

    /// Параметры TLS и REALITY; у Hysteria2 и TUIC защита — всегда TLS, ее имя не пишется.
    private static func tls(_ server: ServerDefinition) -> [(String, String?)] {
        let security = server.security
        return [
            ("sni", security.sni),
            ("fp", security.fingerprint),
            ("alpn", security.alpn.flatMap { $0.isEmpty ? nil : $0.joined(separator: ",") }),
            ("allowInsecure", security.allowInsecure ? "1" : nil),
            ("pbk", security.realityPublicKey),
            ("sid", security.realityShortId),
            ("spx", security.realitySpiderX),
        ]
    }

    /// vmess:// — base64 JSON в виде v2rayN: поля, которые читает [LinkParser].
    private static func vmess(_ server: ServerDefinition) -> String {
        let transport = server.transport
        let security = server.security
        return JsonText.object([
            ("v", "2"),
            ("ps", nameWithFlag(server)),
            ("add", server.host),
            ("port", String(server.port)),
            ("id", server.credentials.uuid ?? ""),
            ("aid", "0"),
            ("scy", server.options.cipher ?? "auto"),
            ("net", kind(transport.kind)),
            ("type", "none"),
            ("host", transport.host ?? ""),
            // В vmess-ссылке имя сервиса gRPC лежит в path.
            ("path", (transport.kind == .grpc ? transport.serviceName : transport.path) ?? ""),
            ("tls", security.kind == .none ? "" : security.kind.rawValue),
            ("sni", security.sni ?? ""),
            ("fp", security.fingerprint ?? ""),
            ("alpn", security.alpn?.joined(separator: ",") ?? ""),
        ])
    }

    private static func query(_ parameters: [(String, String?)]) -> String {
        let pairs = parameters.compactMap { key, value -> String? in
            guard let value, !value.isEmpty else {
                return nil
            }

            return key + "=" + escape(value)
        }

        return pairs.isEmpty ? "" : "?" + pairs.joined(separator: "&")
    }

    private static func escape(_ text: String?) -> String {
        LinkParser.escape(text ?? "")
    }
}

/// Запись JSON для конфигов и ссылок: строки экранируются, числа и логические значения — как есть; порядок ключей — как
/// задан.
public enum JsonText {
    /// Значение: строка, Int, Bool, массив значений, объект из пар или nil.
    public indirect enum Value {
        case string(String)
        case int(Int)
        case bool(Bool)
        case array([Value])
        case object([(String, Value)])
        case null
    }

    /// Объект из строковых пар.
    static func object(_ pairs: [(String, String)]) -> String {
        write(.object(pairs.map { ($0.0, .string($0.1)) }))
    }

    public static func write(_ value: Value) -> String {
        var out = ""
        write(&out, value)
        return out
    }

    private static func write(_ out: inout String, _ value: Value) {
        switch value {
        case .null:
            out += "null"
        case let .string(text):
            string(&out, text)
        case let .int(number):
            out += String(number)
        case let .bool(flag):
            out += flag ? "true" : "false"
        case let .array(items):
            out += "["
            for (i, item) in items.enumerated() {
                if i > 0 {
                    out += ","
                }

                write(&out, item)
            }

            out += "]"
        case let .object(pairs):
            out += "{"
            for (i, (key, item)) in pairs.enumerated() {
                if i > 0 {
                    out += ","
                }

                string(&out, key)
                out += ":"
                write(&out, item)
            }

            out += "}"
        }
    }

    private static func string(_ out: inout String, _ text: String) {
        out += "\""
        for scalar in text.unicodeScalars {
            switch scalar {
            case "\"": out += "\\\""
            case "\\": out += "\\\\"
            case "\n": out += "\\n"
            case "\r": out += "\\r"
            case "\t": out += "\\t"
            default:
                if scalar.value < 0x20 {
                    let hex = String(scalar.value, radix: 16)
                    out += "\\u" + String(repeating: "0", count: 4 - hex.count) + hex
                } else {
                    out.unicodeScalars.append(scalar)
                }
            }
        }

        out += "\""
    }
}

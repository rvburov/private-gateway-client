import Foundation

/// Коды ошибок разбора. Одинаковые на всех платформах: эталоны в shared/testdata проверяют их и на десктопе, и на
/// телефонах.
public enum ImportErrors {
    /// Строка не похожа на ссылку: нет «схема://».
    public static let badLink = "bad_link"

    /// Протокол ссылки программа не знает.
    public static let unknownScheme = "unknown_scheme"

    public static let badHost = "bad_host"

    /// Порт не число, вне 1–65535 или диапазон портов (не поддерживается).
    public static let badPort = "bad_port"

    /// Нет UUID, пароля или ключа — подключиться нечем.
    public static let noCredentials = "no_credentials"

    public static let badEncoding = "bad_encoding"

    public static let unsupportedTransport = "unsupported_transport"

    public static let unsupportedMethod = "unsupported_method"

    /// Параметр, без которого сервер не заработает так, как задумано (plugin, alterId…).
    public static let unsupportedParameter = "unsupported_parameter"

    /// Все коды — тест следит, чтобы у каждого был эталон.
    public static let all = [
        badLink, unknownScheme, badHost, badPort, noCredentials, badEncoding, unsupportedTransport, unsupportedMethod, unsupportedParameter,
    ]
}

/// Результат разбора одной ссылки: сервер или код ошибки из [ImportErrors].
public struct LinkResult: Equatable, Sendable {
    public let server: ServerDefinition?
    public let error: String?

    public var isOk: Bool { server != nil }
}

/// Параметры ссылки: регистр ключей не важен; разбор оставляет первое значение ключа, явная запись — заменяет.
struct Query {
    private var values: [String: String] = [:]

    subscript(key: String) -> String? {
        get { values[key.lowercased()] }
        set { values[key.lowercased()] = newValue }
    }

    mutating func putIfAbsent(_ key: String, _ value: String) {
        let lower = key.lowercased()
        if values[lower] == nil {
            values[lower] = value
        }
    }
}

/// Разбор ссылок vless://, vmess://, trojan://, ss://, hysteria2:// (hy2://), tuic://, wireguard:// (wg://) — так же, как на
/// десктопе (`LinkParser`) и Android: ссылка разбирается вручную, без URL, base64 и «+» в данных не портятся. Неизвестные
/// параметры пропускаются; параметры, без которых сервер работал бы не так, — ошибка.
public enum LinkParser {
    static let shadowsocksMethods: Set<String> = [
        "aes-128-gcm", "aes-256-gcm", "chacha20-ietf-poly1305", "xchacha20-ietf-poly1305",
        "2022-blake3-aes-128-gcm", "2022-blake3-aes-256-gcm", "2022-blake3-chacha20-poly1305",
    ]

    static let vmessCiphers: Set<String> = ["auto", "aes-128-gcm", "chacha20-poly1305", "none", "zero"]

    public static func parse(_ link: String?) -> LinkResult {
        let text = Scalars(TextKit.trim(link ?? ""))
        guard let schemeEnd = text.index(ofText: "://"), schemeEnd > 0 else {
            return fail(ImportErrors.badLink)
        }

        let rest = text.text(from: schemeEnd + 3)
        let proto: ProxyProtocol
        switch text.text(0, schemeEnd).lowercased() {
        case "vless": proto = .vless
        case "vmess": proto = .vmess
        case "trojan": proto = .trojan
        case "ss": proto = .shadowsocks
        case "hysteria2", "hy2": proto = .hysteria2
        case "tuic": proto = .tuic
        case "wireguard", "wg": proto = .wireGuard
        default: return fail(ImportErrors.unknownScheme)
        }

        if proto == .vmess {
            return vmess(rest)
        }

        guard let parts = split(rest) else {
            return fail(ImportErrors.badLink)
        }

        return fromParts(proto, parts)
    }

    /// Сервер из частей ссылки — общий путь для ссылок и для форматов sing-box и Clash: те же проверки и те же коды
    /// ошибок. VMess — через [vmessFrom].
    static func fromParts(_ proto: ProxyProtocol, _ parts: LinkParts) -> LinkResult {
        switch proto {
        case .vless: return vless(parts)
        case .trojan: return trojan(parts)
        case .shadowsocks: return shadowsocks(parts)
        case .hysteria2: return hysteria2(parts)
        case .tuic: return tuic(parts)
        case .wireGuard: return wireGuard(parts)
        case .vmess: return fail(ImportErrors.unknownScheme)
        }
    }

    /// Части ссылки из полей конфига: секреты (через «:», каждый экранируется как в ссылке), адрес и порт (IPv6 — и без
    /// скобок: порт отделяется по последнему двоеточию), параметры и название — как после разбора ссылки.
    static func parts(_ secrets: [String?], host: String?, port: String?, query: Query, name: String?) -> LinkParts {
        LinkParts(
            userInfo: secrets.map { escape($0 ?? "") }.joined(separator: ":"),
            authority: (host ?? "") + ":" + (port ?? ""),
            query: query,
            name: TextKit.trim(name ?? "")
        )
    }

    // MARK: Протоколы

    private static func vless(_ parts: LinkParts) -> LinkResult {
        let endpoint = Self.endpoint(parts)
        let transport = Self.transport(parts.query)
        let security = Self.security(parts.query, byDefault: .none)
        if let error = firstError(endpoint.error, transport.error, security.error) {
            return fail(error)
        }

        if let encryption = value(parts.query, "encryption"), encryption != "none" {
            return fail(ImportErrors.unsupportedParameter)
        }

        let uuid = unescape(parts.userInfo)
        if uuid.isEmpty {
            return fail(ImportErrors.noCredentials)
        }

        return ok(
            parts.name, .vless, endpoint.host, endpoint.port, transport.value, security.value,
            ProtocolOptions(flow: value(parts.query, "flow")), ServerCredentials(uuid: uuid)
        )
    }

    private static func vmess(_ rest: String) -> LinkResult {
        let scalars = Scalars(rest)
        let body = TextKit.trim(scalars.index(of: "#").map { scalars.text(0, $0) } ?? rest)
        guard let json = decodeBase64(body) else {
            return fail(ImportErrors.badEncoding)
        }

        guard let root = try? JsonTree.parse(json).root, case .object(_, _) = root else {
            return fail(ImportErrors.badEncoding)
        }

        var query = Query()
        query["type"] = ConfigTree.text(root, "net") ?? "tcp"
        query["path"] = ConfigTree.text(root, "path") ?? ""
        query["host"] = ConfigTree.text(root, "host") ?? ""
        query["security"] = ConfigTree.text(root, "tls") ?? ""
        query["sni"] = ConfigTree.text(root, "sni") ?? ""
        query["fp"] = ConfigTree.text(root, "fp") ?? ""
        query["alpn"] = ConfigTree.text(root, "alpn") ?? ""
        // В vmess-ссылке имя сервиса gRPC лежит в path.
        query["serviceName"] = query["path"]
        return vmessFrom(
            name: ConfigTree.text(root, "ps"), host: ConfigTree.text(root, "add"), port: ConfigTree.text(root, "port"),
            alterId: ConfigTree.text(root, "aid"), cipher: ConfigTree.text(root, "scy"), header: ConfigTree.text(root, "type"), query: query,
            uuid: ConfigTree.text(root, "id")
        )
    }

    /// VMess из полей: alterId, шифр и тип заголовка проверяются так же, как в vmess-ссылке.
    static func vmessFrom(
        name: String?,
        host hostText: String?,
        port portText: String?,
        alterId: String?,
        cipher cipherText: String?,
        header: String?,
        query: Query,
        uuid: String?
    ) -> LinkResult {
        let host = hostText ?? ""
        if !isHost(host) {
            return fail(ImportErrors.badHost)
        }

        guard let port = Self.port(portText) else {
            return fail(ImportErrors.badPort)
        }

        // alterId больше нуля — старый VMess без AEAD, ядро его не поддерживает.
        if let alterId, !alterId.isEmpty, alterId != "0" {
            return fail(ImportErrors.unsupportedParameter)
        }

        let cipher = (cipherText ?? "auto").lowercased()
        if !vmessCiphers.contains(cipher) {
            return fail(ImportErrors.unsupportedMethod)
        }

        if let header, header != "none", header != "" {
            return fail(ImportErrors.unsupportedParameter)
        }

        let transport = Self.transport(query)
        let security = Self.security(query, byDefault: .none)
        if let error = firstError(transport.error, security.error) {
            return fail(error)
        }

        guard let uuid, !uuid.isEmpty else {
            return fail(ImportErrors.noCredentials)
        }

        return ok(name ?? "", .vmess, host, port, transport.value, security.value, ProtocolOptions(cipher: cipher), ServerCredentials(uuid: uuid))
    }

    private static func trojan(_ parts: LinkParts) -> LinkResult {
        // Trojan без TLS не работает — защита по умолчанию TLS.
        let endpoint = Self.endpoint(parts)
        let transport = Self.transport(parts.query)
        let security = Self.security(parts.query, byDefault: .tls)
        if let error = firstError(endpoint.error, transport.error, security.error) {
            return fail(error)
        }

        let password = unescape(parts.userInfo)
        if password.isEmpty {
            return fail(ImportErrors.noCredentials)
        }

        return ok(parts.name, .trojan, endpoint.host, endpoint.port, transport.value, security.value, .empty, ServerCredentials(password: password))
    }

    private static func shadowsocks(_ original: LinkParts) -> LinkResult {
        var parts = original

        // Старый вид: ss://base64(метод:пароль@адрес:порт)#имя.
        if parts.userInfo.isEmpty {
            guard let legacy = decodeBase64(parts.authority) else {
                return fail(ImportErrors.badEncoding)
            }

            let scalars = Scalars(legacy)
            guard let at = scalars.lastIndex(of: "@") else {
                return fail(ImportErrors.badEncoding)
            }

            parts = LinkParts(userInfo: scalars.text(0, at), authority: scalars.text(from: at + 1), query: parts.query, name: parts.name)
        }

        let endpoint = Self.endpoint(parts)
        if let error = endpoint.error {
            return fail(error)
        }

        if value(parts.query, "plugin") != nil {
            return fail(ImportErrors.unsupportedParameter)
        }

        // SIP002: base64url(метод:пароль) или метод:пароль в процентной кодировке (Shadowsocks 2022).
        var userInfo = unescape(parts.userInfo)
        if !userInfo.unicodeScalars.contains(":") {
            userInfo = decodeBase64(userInfo) ?? ""
        }

        let scalars = Scalars(userInfo)
        guard let colon = scalars.index(of: ":"), colon > 0, colon != scalars.count - 1 else {
            return fail(ImportErrors.noCredentials)
        }

        let method = scalars.text(0, colon).lowercased()
        if !shadowsocksMethods.contains(method) {
            return fail(ImportErrors.unsupportedMethod)
        }

        return ok(
            parts.name, .shadowsocks, endpoint.host, endpoint.port, .tcp, .none, ProtocolOptions(cipher: method),
            ServerCredentials(password: scalars.text(from: colon + 1))
        )
    }

    private static func hysteria2(_ parts: LinkParts) -> LinkResult {
        let endpoint = Self.endpoint(parts)
        let security = Self.security(parts.query, byDefault: .tls)
        if let error = firstError(endpoint.error, security.error) {
            return fail(error)
        }

        let obfs = value(parts.query, "obfs")
        let obfsPassword = value(parts.query, "obfs-password")
        if obfs != nil && obfs != "salamander" || obfs != nil && obfsPassword == nil {
            return fail(ImportErrors.unsupportedParameter)
        }

        let password = unescape(parts.userInfo)
        if password.isEmpty {
            return fail(ImportErrors.noCredentials)
        }

        var tls = security.value
        tls.kind = .tls
        return ok(
            parts.name, .hysteria2, endpoint.host, endpoint.port, .quic, tls, ProtocolOptions(obfs: obfs),
            ServerCredentials(password: password, obfsPassword: obfsPassword)
        )
    }

    private static func tuic(_ parts: LinkParts) -> LinkResult {
        let endpoint = Self.endpoint(parts)
        let security = Self.security(parts.query, byDefault: .tls)
        if let error = firstError(endpoint.error, security.error) {
            return fail(error)
        }

        let userInfo = Scalars(parts.userInfo)
        let colon = userInfo.index(of: ":") ?? -1
        let uuid = colon > 0 ? unescape(userInfo.text(0, colon)) : ""
        let password = colon > 0 ? unescape(userInfo.text(from: colon + 1)) : ""
        if uuid.isEmpty || password.isEmpty {
            return fail(ImportErrors.noCredentials)
        }

        var tls = security.value
        tls.kind = .tls
        return ok(
            parts.name, .tuic, endpoint.host, endpoint.port, .quic, tls, ProtocolOptions(congestionControl: value(parts.query, "congestion_control")),
            ServerCredentials(uuid: uuid, password: password)
        )
    }

    private static func wireGuard(_ parts: LinkParts) -> LinkResult {
        let endpoint = Self.endpoint(parts)
        if let error = endpoint.error {
            return fail(error)
        }

        let privateKey = unescape(parts.userInfo)
        let peerKey = value(parts.query, "publickey") ?? value(parts.query, "public_key") ?? value(parts.query, "peer_public_key")
        guard !privateKey.isEmpty, let peerKey else {
            return fail(ImportErrors.noCredentials)
        }

        let addresses = (value(parts.query, "address") ?? value(parts.query, "ip"))
            .map { $0.split(separator: ",", omittingEmptySubsequences: false).map { TextKit.trim(String($0)) }.filter { !$0.isEmpty } }
        var mtu: Int?
        if let mtuText = value(parts.query, "mtu") {
            guard let number = digits(mtuText), number >= 576, number <= 65535 else {
                return fail(ImportErrors.unsupportedParameter)
            }

            mtu = number
        }

        return ok(
            parts.name, .wireGuard, endpoint.host, endpoint.port, .udp, .none,
            ProtocolOptions(peerPublicKey: peerKey, addresses: addresses, mtu: mtu),
            ServerCredentials(privateKey: privateKey, presharedKey: value(parts.query, "presharedkey") ?? value(parts.query, "preshared_key"))
        )
    }

    // MARK: Общие части ссылки

    /// Части ссылки «данные@адрес:порт?параметры#имя»; имя и параметры уже раскодированы.
    struct LinkParts {
        var userInfo: String
        var authority: String
        var query: Query
        var name: String
    }

    private static func split(_ original: String) -> LinkParts? {
        var rest = Scalars(original)
        var name = ""
        if let hash = rest.index(of: "#") {
            name = TextKit.trim(unescape(rest.text(from: hash + 1)))
            rest = Scalars(rest.text(0, hash))
        }

        var query = Query()
        if let question = rest.index(of: "?") {
            for pair in rest.text(from: question + 1).split(separator: "&", omittingEmptySubsequences: true) {
                let scalars = Scalars(String(pair))
                if let eq = scalars.index(of: "=") {
                    query.putIfAbsent(unescape(scalars.text(0, eq)), unescape(scalars.text(from: eq + 1)))
                } else {
                    query.putIfAbsent(unescape(scalars.text(from: 0)), "")
                }
            }

            rest = Scalars(rest.text(0, question))
        }

        var end = rest.count
        while end > 0, rest[end - 1] == "/" {
            end -= 1
        }

        let trimmed = Scalars(rest.text(0, end))
        if trimmed.isEmpty {
            return nil
        }

        if let at = trimmed.lastIndex(of: "@") {
            return LinkParts(userInfo: trimmed.text(0, at), authority: trimmed.text(from: at + 1), query: query, name: name)
        }

        return LinkParts(userInfo: "", authority: trimmed.text(from: 0), query: query, name: name)
    }

    private struct Endpoint {
        let host: String
        let port: Int
        let error: String?
    }

    /// Адрес и порт из «адрес:порт» или «[IPv6]:порт».
    private static func endpoint(_ parts: LinkParts) -> Endpoint {
        let authority = Scalars(parts.authority)
        let host: String
        let portText: String
        if !authority.isEmpty, authority[0] == "[" {
            guard let close = authority.index(of: "]"), close + 1 < authority.count, authority[close + 1] == ":" else {
                return Endpoint(host: "", port: 0, error: ImportErrors.badHost)
            }

            host = authority.text(1, close)
            portText = authority.text(from: close + 2)
        } else {
            guard let colon = authority.lastIndex(of: ":"), colon > 0 else {
                let colon = authority.lastIndex(of: ":")
                return Endpoint(host: "", port: 0, error: colon == 0 ? ImportErrors.badHost : ImportErrors.badPort)
            }

            host = authority.text(0, colon)
            portText = authority.text(from: colon + 1)
        }

        if !isHost(host) {
            return Endpoint(host: host, port: 0, error: ImportErrors.badHost)
        }

        guard let port = Self.port(portText) else {
            return Endpoint(host: host, port: 0, error: ImportErrors.badPort)
        }

        return Endpoint(host: host, port: port, error: nil)
    }

    private struct Checked<T> {
        let value: T
        let error: String?
    }

    private static func transport(_ query: Query) -> Checked<TransportOptions> {
        let kind: TransportKind
        switch (value(query, "type") ?? "tcp").lowercased() {
        case "tcp", "raw": kind = .tcp
        case "ws", "websocket": kind = .webSocket
        case "grpc": kind = .grpc
        case "httpupgrade": kind = .httpUpgrade
        case "xhttp", "splithttp": kind = .xhttp
        default: return Checked(value: .tcp, error: ImportErrors.unsupportedTransport)
        }

        if kind == .tcp {
            return Checked(value: .tcp, error: nil)
        }

        let grpc = kind == .grpc
        return Checked(
            value: TransportOptions(
                kind,
                path: grpc ? nil : value(query, "path"),
                host: grpc ? nil : value(query, "host"),
                serviceName: grpc ? value(query, "serviceName") : nil
            ),
            error: nil
        )
    }

    private static func security(_ query: Query, byDefault: TransportSecurity) -> Checked<SecurityOptions> {
        let kind: TransportSecurity
        switch value(query, "security")?.lowercased() {
        case nil: kind = byDefault
        case "none": kind = .none
        case "tls": kind = .tls
        case "reality": kind = .reality
        default: return Checked(value: .none, error: ImportErrors.unsupportedParameter)
        }

        let alpn = value(query, "alpn").map { $0.split(separator: ",", omittingEmptySubsequences: false).map { TextKit.trim(String($0)) }.filter { !$0.isEmpty } }
        let reality = kind == .reality
        let security = kind == .none
            ? SecurityOptions.none
            : SecurityOptions(
                kind,
                sni: value(query, "sni") ?? value(query, "peer"),
                fingerprint: value(query, "fp"),
                alpn: (alpn?.isEmpty ?? true) ? nil : alpn,
                allowInsecure: isTrue(value(query, "allowInsecure") ?? value(query, "insecure") ?? value(query, "allow_insecure")),
                realityPublicKey: reality ? value(query, "pbk") : nil,
                realityShortId: reality ? value(query, "sid") : nil,
                realitySpiderX: reality ? value(query, "spx") : nil
            )

        // REALITY без открытого ключа сервера не подключится.
        return Checked(value: security, error: reality && security.realityPublicKey == nil ? ImportErrors.noCredentials : nil)
    }

    // MARK: Мелочи

    private static func ok(
        _ name: String,
        _ proto: ProxyProtocol,
        _ host: String,
        _ port: Int,
        _ transport: TransportOptions,
        _ security: SecurityOptions,
        _ options: ProtocolOptions,
        _ credentials: ServerCredentials
    ) -> LinkResult {
        let (country, cleanName) = CountryFlag.split(name)
        return LinkResult(
            server: ServerDefinition(
                name: cleanName.isEmpty ? host : cleanName, countryCode: country, protocol: proto, host: host, port: port, transport: transport,
                security: security, options: options, credentials: credentials
            ),
            error: nil
        )
    }

    static func fail(_ error: String) -> LinkResult {
        LinkResult(server: nil, error: error)
    }

    /// Первая ошибка из проверок по порядку; проверки уже выполнены все.
    private static func firstError(_ errors: String?...) -> String? {
        errors.first { $0 != nil } ?? nil
    }

    /// Значение параметра; пустое — как отсутствующее.
    private static func value(_ query: Query, _ key: String) -> String? {
        guard let text = query[key], !text.isEmpty else {
            return nil
        }

        return text
    }

    private static func isTrue(_ value: String?) -> Bool {
        value == "1" || value?.lowercased() == "true"
    }

    private static func isHost(_ host: String) -> Bool {
        let scalars = host.unicodeScalars
        return (1...253).contains(host.utf16.count)
            && !scalars.contains { TextKit.isWhitespace($0) || $0 == "/" || $0 == "@" || $0 == "?" || $0 == "#" || $0 == "[" || $0 == "]" }
    }

    /// Порт: только цифры (без знака и пробелов), 1–65535.
    private static func port(_ text: String?) -> Int? {
        guard let number = digits(text), (1...65535).contains(number) else {
            return nil
        }

        return number
    }

    /// Число из одних цифр, как `int.TryParse(…, NumberStyles.None)` десктопа; иначе nil.
    private static func digits(_ text: String?) -> Int? {
        guard let text, !text.isEmpty, text.unicodeScalars.allSatisfy({ ("0"..."9").contains($0) }) else {
            return nil
        }

        return Int(text)
    }

    /// «%XX» — в символы UTF-8, как `Uri.UnescapeDataString` десктопа: «+» не превращается в пробел (в base64 ключах он —
    /// часть данных), неверная последовательность остается как есть.
    static func unescape(_ text: String) -> String {
        if !text.unicodeScalars.contains("%") {
            return text
        }

        let scalars = Scalars(text)
        var result = String.UnicodeScalarView()
        var i = 0
        while i < scalars.count {
            if scalars[i] != "%" {
                result.append(scalars[i])
                i += 1
                continue
            }

            // Подряд идущие «%XX» — одна последовательность байтов UTF-8.
            let start = i
            var bytes: [UInt8] = []
            while i + 2 < scalars.count, scalars[i] == "%", let high = hex(scalars[i + 1]), let low = hex(scalars[i + 2]) {
                bytes.append(UInt8(high * 16 + low))
                i += 3
            }

            if i == start {
                result.append(scalars[i])
                i += 1
                continue
            }

            result.append(contentsOf: (TextKit.utf8(bytes) ?? scalars.text(start, i)).unicodeScalars)
        }

        return String(result)
    }

    /// Экранирование, как `Uri.EscapeDataString`: остаются только A–Z, a–z, 0–9 и «-._~».
    static func escape(_ text: String) -> String {
        var result = ""
        for byte in text.utf8 {
            let scalar = Unicode.Scalar(byte)
            if ("A"..."Z").contains(scalar) || ("a"..."z").contains(scalar) || ("0"..."9").contains(scalar) || "-._~".unicodeScalars.contains(scalar) {
                result.unicodeScalars.append(scalar)
            } else {
                result += (byte < 16 ? "%0" : "%") + String(byte, radix: 16, uppercase: true)
            }
        }

        return result
    }

    private static func hex(_ scalar: Unicode.Scalar) -> Int? {
        switch scalar {
        case "0"..."9": return Int(scalar.value) - 48
        case "a"..."f": return Int(scalar.value) - 97 + 10
        case "A"..."F": return Int(scalar.value) - 65 + 10
        default: return nil
        }
    }

    /// base64 обычный или url-safe, с выравниванием «=» или без; nil — не base64 или не UTF-8.
    static func decodeBase64(_ text: String) -> String? {
        var compact = ""
        for scalar in text.unicodeScalars where !TextKit.isWhitespace(scalar) {
            switch scalar {
            case "-": compact.unicodeScalars.append("+")
            case "_": compact.unicodeScalars.append("/")
            default: compact.unicodeScalars.append(scalar)
            }
        }

        while compact.utf8.count % 4 != 0 {
            compact += "="
        }

        guard let data = Data(base64Encoded: compact) else {
            return nil
        }

        return TextKit.utf8([UInt8](data))
    }
}

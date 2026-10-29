import Foundation

/// Одноразовый вход HTTP-прокси ядра туннеля. Живет только в памяти до его остановки; приложение получает его через
/// sendProviderMessage. Адрес всегда петля: ни настройки системы, ни адрес от внешнего источника сюда не попадают.
public struct SubscriptionProxy: Codable, Equatable, Sendable {
    public static let host = "127.0.0.1"
    public let port: Int
    public let user: String
    public let password: String

    public init(port: Int, user: String, password: String) {
        self.port = port
        self.user = user
        self.password = password
    }

    public var valid: Bool { (1...65535).contains(port) && !user.isEmpty && !password.isEmpty }

    /// CFNetwork обходит прокси для некоторых локальных адресов даже при allowFailover=false. Такие и неоднозначные
    /// URL отклоняются до URLSession и на каждом redirect. Только синтаксис/байты, без DNS lookup ради проверки.
    public static func supportsTarget(_ url: URLComponents) -> Bool {
        guard let raw = url.host, let canonical = url.url?.host, !raw.contains("%") else { return false }
        let host = unbracketed(canonical.lowercased())
        var original = unbracketed(raw.lowercased())
        guard !original.hasSuffix(".."), !host.hasSuffix("..") else { return false }
        if original.hasSuffix(".") { original.removeLast() }
        var name = host
        if name.hasSuffix(".") { name.removeLast() }
        if let address = IpAddress.parse(name) {
            return PublicAddress.isPublic(name) && !XrayConfig.localNetworks.contains { contains(normalized(address), network: $0) }
                && !ambiguousV4(original)
        }
        guard !name.contains(":"), !ambiguousV4(original) else { return false }
        let labels = name.split(separator: ".", omittingEmptySubsequences: false)
        guard labels.count >= 2, labels.allSatisfy({ label in
            !label.isEmpty && label.count <= 63 && label.first != "-" && label.last != "-"
                && label.allSatisfy { $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "-") }
        }), labels.last?.allSatisfy({ $0.isNumber }) == false else { return false }
        let localNames = ["localhost", "local", "localdomain", "lan", "home.arpa"]
        return !localNames.contains { name == $0 || name.hasSuffix("." + $0) }
    }

    private static func unbracketed(_ host: String) -> String {
        host.hasPrefix("[") && host.hasSuffix("]") ? String(host.dropFirst().dropLast()) : host
    }

    /// inet_aton-формы (127.1, decimal DWORD, hex, octal) разные стеки понимают по-разному. Строгий публичный dotted quad
    /// разрешен; все остальные полностью числовые/hex последовательности не становятся именем для URLSession.
    private static func ambiguousV4(_ host: String) -> Bool {
        if let parsed = IpAddress.parse(host), parsed.count == 4 { return false }
        let parts = host.split(separator: ".", omittingEmptySubsequences: false)
        return !parts.isEmpty && parts.allSatisfy { part in
            if part.hasPrefix("0x") {
                let digits = part.dropFirst(2)
                return !digits.isEmpty && digits.allSatisfy { $0.isASCII && $0.isHexDigit }
            }
            return !part.isEmpty && part.allSatisfy { $0.isNumber }
        }
    }

    /// WireGuard сам разрешает имя назначения. Явное исключение lanDns или разрешенная локальная сеть могут направить
    /// этот DNS напрямую, даже если HTTP-вход принудительно идет в proxy. Сравниваем с теми же CIDR, что у правил Xray,
    /// а не только с RFC1918: там есть петля, CGN, link-local, тестовые и групповые сети. Имя DNS не разрешаем ради
    /// проверки: неизвестный адрес/формат не доказывает защищенный путь и потому отказывает.
    public static func isAllowed(protocol proto: ProxyProtocol, settings: CoreSettings) -> Bool {
        guard proto == .wireGuard else { return true }
        guard settings.lanDns == nil else { return false }
        // Без AllowLan прямого исключения нет: запрос в локальную сеть Xray блокирует. Не запрещаем такой путь только
        // из-за имени публичного DoH; это та же политика, что у десктопа и Android.
        guard settings.allowLan else { return true }
        guard let address = dnsAddress(settings.dns) else { return false }
        return !XrayConfig.localNetworks.contains { contains(address, network: $0) }
    }

    private static func dnsAddress(_ dns: String) -> [UInt8]? {
        let text = dns.trimmingCharacters(in: .whitespacesAndNewlines)
        if let literal = IpAddress.parse(text) { return normalized(literal) }
        guard let url = URLComponents(string: text), url.scheme?.lowercased() == "https", let host = url.host,
              url.user == nil, url.password == nil else { return nil }
        let literal = host.hasPrefix("[") && host.hasSuffix("]") ? String(host.dropFirst().dropLast()) : host
        return IpAddress.parse(literal).map(normalized)
    }

    /// Go net.IPNet.Contains, как у Xray: IPv4-mapped IPv6 сравнивается с сетями IPv4.
    private static func normalized(_ address: [UInt8]) -> [UInt8] {
        if address.count == 16, address[0..<10].allSatisfy({ $0 == 0 }), address[10] == 0xFF, address[11] == 0xFF {
            return Array(address[12..<16])
        }
        return address
    }

    private static func contains(_ address: [UInt8], network: String) -> Bool {
        let parts = network.split(separator: "/", maxSplits: 1)
        guard parts.count == 2, let bytes = IpAddress.parse(String(parts[0])), bytes.count == address.count,
              let bits = Int(parts[1]), (0...bytes.count * 8).contains(bits) else { return false }
        let whole = bits / 8
        let remaining = bits % 8
        guard address.prefix(whole) == bytes.prefix(whole) else { return false }
        if remaining == 0 { return true }
        let mask = UInt8.max << (8 - remaining)
        return address[whole] & mask == bytes[whole] & mask
    }

}

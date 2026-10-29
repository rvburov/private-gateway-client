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

    /// Явный прокси для HTTP и HTTPS (CONNECT). Пустые исключения: даже локальная подписка идет через выбранный сервер.
    /// PAC/WPAD не используются; отсутствие или отказ этого входа — ошибка, а не возврат к системному/прямому пути.
    public var configuration: [String: Any] {
        [
            "HTTPEnable": 1, "HTTPProxy": Self.host, "HTTPPort": port,
            "HTTPSEnable": 1, "HTTPSProxy": Self.host, "HTTPSPort": port,
            "ExceptionsList": [String](), "ExcludeSimpleHostnames": false,
            "ProxyAutoConfigEnable": 0, "ProxyAutoDiscoveryEnable": 0,
        ]
    }
}

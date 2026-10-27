/// Адрес с длиной префикса: «172.19.0.1/30».
public struct TunPrefix: Equatable, Sendable, CustomStringConvertible {
    public let address: String
    public let length: Int

    public init(_ address: String, _ length: Int) {
        self.address = address
        self.length = length
    }

    public var description: String { "\(address)/\(length)" }
}

/// Что получает iOS перед подъемом туннеля (`NEPacketTunnelNetworkSettings`): адреса туннеля, маршруты, DNS и MTU — план
/// `TunPlan` Android. Весь интернет — в туннель (0.0.0.0/0 и ::/0); DNS — адрес внутри туннеля для всех имен, его запросы ядро
/// отвечает через сервер (`XrayConfig`: порт 53 → выход DNS).
///
/// IPv6 в туннель идет всегда, и при выключенной настройке «IPv6 через сервер»: Android сам закрывает IPv6, когда у туннеля нет
/// адреса IPv6, а iOS пустила бы его мимо туннеля — утечка. Без настройки ядро отправляет такие соединения в блок, а DNS
/// отвечает только адресами IPv4 — программы уходят на IPv4 через сервер (решение 459).
public struct TunPlan: Equatable, Sendable {
    /// Свой адрес IPv6 туннеля — из частного диапазона fd00::/8 (ULA), в интернете его нет; тот же, что у Android.
    public static let address6 = "fd66:7067:6763::1"
    public static let prefix6 = 126

    /// «Адрес сервера туннеля», который iOS показывает в настройках VPN. Не адрес сервера: тот запечатан в Связке ключей и
    /// в настройки системы не попадает.
    public static let remoteAddress = "127.0.0.1"

    public let ipv4: TunPrefix
    public let ipv6: TunPrefix
    /// Маршруты в туннель: весь IPv4 и весь IPv6.
    public let routes: [TunPrefix]
    public let dnsServers: [String]
    /// Имена, которые идут к DNS туннеля: пустое имя — все.
    public let dnsMatchDomains: [String]
    public let mtu: Int

    public static func of(_ settings: CoreSettings) -> TunPlan {
        TunPlan(
            ipv4: TunPrefix(XrayConfig.tunAddress, XrayConfig.tunPrefix), ipv6: TunPrefix(address6, prefix6),
            routes: [TunPrefix("0.0.0.0", 0), TunPrefix("::", 0)], dnsServers: [XrayConfig.tunDns], dnsMatchDomains: [""], mtu: settings.mtu
        )
    }

    /// Маска сети IPv4 по длине префикса — `NEIPv4Settings` берет маску, а не длину: 30 → «255.255.255.252».
    public static func subnetMask(_ length: Int) -> String {
        precondition((0...32).contains(length), "Длина префикса IPv4 — от 0 до 32")
        let mask: UInt32 = length == 0 ? 0 : UInt32.max << (32 - length)
        return [24, 16, 8, 0].map { String((mask >> UInt32($0)) & 0xFF) }.joined(separator: ".")
    }
}

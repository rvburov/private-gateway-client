import Foundation

/// Куда уходят DNS-запросы в туннеле — выбор десктопа и Android («Защита и утечки», доска 5).
public enum DnsChoice: String, CaseIterable, Sendable {
    /// DNS по умолчанию ядра — DoH Cloudflare по адресу.
    case profile
    case cloudflareDoh
    case quad9Doh

    /// Свой адрес: IP сервера DNS или DoH по IP (https://IP/…).
    case custom
}

/// Настройки туннеля телефона: DNS, IPv6 через сервер, локальная сеть — как `TunnelSettings` Android. По умолчанию — как
/// решено для защиты: локальная сеть закрыта, IPv6 закрыт, DNS — DoH по адресу.
public struct TunnelSettings: Equatable, Hashable, Sendable {
    public static let quad9Doh = "https://9.9.9.9/dns-query"

    public var dns: DnsChoice
    public var customDns: String?
    public var ipv6: Bool
    public var allowLan: Bool

    public init(dns: DnsChoice = .profile, customDns: String? = nil, ipv6: Bool = false, allowLan: Bool = false) {
        self.dns = dns
        self.customDns = customDns
        self.ipv6 = ipv6
        self.allowLan = allowLan
    }

    /// Сервер DNS для ядра; свой адрес не годится — DNS по умолчанию (экран настроек такой адрес не сохраняет).
    public var dnsServer: String {
        switch dns {
        case .profile, .cloudflareDoh:
            return XrayConfig.defaultDns
        case .quad9Doh:
            return Self.quad9Doh
        case .custom:
            guard let custom = customDns?.trimmingCharacters(in: .whitespacesAndNewlines), Self.validCustomDns(custom) else {
                return XrayConfig.defaultDns
            }

            return custom
        }
    }

    /// Свой DNS в частной сети (адрес, как у Pi-hole на 192.168.x.x): Xray спрашивает его напрямую, мимо сервера — через
    /// сервер частный адрес не достать. Только свой адрес IP: DoH идет по порту 443, а не 53. Иначе nil.
    public var lanDns: String? {
        guard dns == .custom, let custom = customDns?.trimmingCharacters(in: .whitespacesAndNewlines), PrivateNetwork.contains(custom) else {
            return nil
        }

        return custom
    }

    public func core() -> CoreSettings {
        CoreSettings(dns: dnsServer, allowLan: allowLan, ipv6: ipv6, lanDns: lanDns)
    }

    /// Нужно ли переподключиться, чтобы `next` вошли в работу, — только то, что получает ядро. Неверный свой адрес равен
    /// «Из профиля» (ядро получает DNS по умолчанию), и ради него переподключаться не нужно.
    public func needsReconnect(_ next: TunnelSettings) -> Bool {
        core() != next.core()
    }

    /// Свой DNS: IPv4 или IPv6 адрес либо DoH по адресу («https://IP/путь»). Имя хоста не годится: чтобы найти сам
    /// DNS-сервер, ядру пришлось бы спросить имя мимо туннеля.
    public static func validCustomDns(_ value: String) -> Bool {
        let text = value.trimmingCharacters(in: .whitespacesAndNewlines)
        if IpAddress.parse(text) != nil {
            return true
        }

        guard text.lowercased().hasPrefix("https://") else {
            return false
        }

        // IPv6 — в скобках («https://[2001:db8::1]/…»), за адресом — разве что порт.
        let authority = Array(text.dropFirst("https://".count).prefix { $0 != "/" })
        let bracket = authority.first == "["
        let end: Int
        if bracket {
            guard let close = authority.firstIndex(of: "]") else {
                return false
            }

            end = close
        } else {
            end = authority.firstIndex(of: ":") ?? authority.count
        }

        let host = String(authority[(bracket ? 1 : 0)..<end])
        let rest = String(authority[(bracket ? end + 1 : end)...])
        guard port(rest), let address = IpAddress.parse(host) else {
            return false
        }

        return bracket == (address.count == 16)
    }

    /// За адресом — ничего или «:порт» от 1 до 65535.
    private static func port(_ rest: String) -> Bool {
        if rest.isEmpty {
            return true
        }

        let digits = rest.dropFirst()
        guard rest.hasPrefix(":"), (1...5).contains(digits.count), digits.allSatisfy({ $0.isASCII && $0.isNumber }), let number = Int(digits) else {
            return false
        }

        return (1...65535).contains(number)
    }
}

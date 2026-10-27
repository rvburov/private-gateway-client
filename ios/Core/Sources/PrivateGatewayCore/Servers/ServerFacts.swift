/// Сведения о сервере для подписей интерфейса — как `ServerFacts` десктопа и Android.
public enum ServerFacts {
    /// Шифруется ли трафик до сервера: от этого зависит подпись «Трафик зашифрован», она не ставится всегда — VLESS без TLS
    /// и REALITY передает данные открыто.
    public static func isEncrypted(_ proto: ProxyProtocol, _ security: TransportSecurity) -> Bool {
        switch proto {
        // Шифруют сами, независимо от транспорта.
        case .vmess, .shadowsocks, .wireGuard: return true
        // Работают только поверх TLS (QUIC) — без него не подключатся.
        case .trojan, .hysteria2, .tuic: return true
        // VLESS собственного шифрования не имеет.
        case .vless: return security != .none
        }
    }

    /// Имя протокола так, как его пишут сами протоколы.
    public static func protocolName(_ proto: ProxyProtocol) -> String {
        switch proto {
        case .vless: return "VLESS"
        case .vmess: return "VMess"
        case .trojan: return "Trojan"
        case .shadowsocks: return "Shadowsocks"
        case .hysteria2: return "Hysteria2"
        case .tuic: return "TUIC"
        case .wireGuard: return "WireGuard"
        }
    }

    /// Имя защиты транспорта; без защиты — nil (в подписи не пишется).
    public static func securityName(_ security: TransportSecurity) -> String? {
        switch security {
        case .reality: return "REALITY"
        case .tls: return "TLS"
        case .none: return nil
        }
    }

    /// Имя транспорта для подписи; обычный TCP, UDP и QUIC — nil (не пишутся).
    public static func transportName(_ kind: TransportKind) -> String? {
        switch kind {
        case .webSocket: return "WS"
        case .grpc: return "gRPC"
        case .xhttp: return "XHTTP"
        case .httpUpgrade: return "HTTPUpgrade"
        case .tcp, .udp, .quic: return nil
        }
    }

    /// Каким ядром телефон подключается к серверу (строка «Ядро» карточки): на iOS, как на Android, ядро одно — Xray-core,
    /// WireGuard тоже через него; Hysteria2 и TUIC ядру Xray не по силам — nil (подключение к ним — PGC-SVC-005, как и отказ
    /// `XrayConfig`).
    public static func coreName(_ proto: ProxyProtocol) -> String? {
        switch proto {
        case .vless, .vmess, .trojan, .shadowsocks, .wireGuard: return "Xray-core"
        case .hysteria2, .tuic: return nil
        }
    }

    /// Адрес для карточки: «адрес:порт», IPv6 — в квадратных скобках, как в ссылке («[2001:db8::1]:443»).
    public static func address(_ host: String, _ port: Int) -> String {
        (host.contains(":") ? "[\(host)]" : host) + ":" + String(port)
    }

    /// Короткая подпись протокола, как в списке серверов: «VLESS · REALITY», «VLESS · XHTTP», «Trojan · WS», «Hysteria2».
    /// Второе слово — то, что отличает сервер: REALITY, иначе транспорт, иначе TLS у протоколов, которые бывают и без него.
    public static func summary(_ proto: ProxyProtocol, _ security: TransportSecurity, _ transport: TransportKind) -> String {
        // TUIC поддерживается только пятой версии — версию пишем, как в макете.
        let name = proto == .tuic ? "TUIC v5" : protocolName(proto)
        let detail: String?
        if security == .reality {
            detail = securityName(security)
        } else if let shown = transportName(transport) {
            detail = shown
        } else if security == .tls, proto == .vless || proto == .vmess {
            detail = securityName(security)
        } else {
            detail = nil
        }

        return detail.map { "\(name) · \($0)" } ?? name
    }
}

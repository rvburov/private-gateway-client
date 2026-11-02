import CFNetwork
import Foundation
import PrivateGatewayCore

/// Что iOS говорит о прокси и VPN других приложений — для строки «Другие VPN и прокси» и блока «Что может мешать» на
/// «Проверке»: прокси HTTP или HTTPS сети (ручной в настройках Wi-Fi или из профиля), скрипт автонастройки прокси (PAC) и
/// интерфейсы VPN в настройках сетей системы. Только чтение.
struct ProxyFindings: Equatable {
    /// Прокси HTTP или HTTPS: «адрес:порт».
    var http: String?
    /// Адрес скрипта автонастройки прокси (PAC) — без секретов (`SecretMasker`).
    var pac: String?
    /// Интерфейсы VPN в настройках сетей системы: «utun3», «ipsec0». Пока свой туннель поднят, среди них и он.
    var vpn: [String] = []

    /// Найден прокси сети: HTTP или PAC.
    var proxy: Bool { http != nil || pac != nil }
}

/// Настройки прокси системы (`CFNetworkCopySystemProxySettings`) — как `IMachineSettings` десктопа, в пределах того, что iOS
/// дает приложению: переменных среды, WinHTTP и чужих программ по признакам на телефоне нет.
enum SystemProxy {
    /// Начала имен интерфейсов VPN: туннели utun и tun, IPsec, PPP, TAP.
    static let vpnPrefixes = ["utun", "ipsec", "ppp", "tap", "tun"]

    static func read() -> ProxyFindings {
        guard let settings = CFNetworkCopySystemProxySettings()?.takeRetainedValue() as? [String: Any] else {
            return ProxyFindings()
        }

        return parse(settings)
    }

    /// Словарь настроек → что найдено. Ключи — значения констант kCFNetworkProxies* (часть из них в SDK iOS не объявлена).
    static func parse(_ settings: [String: Any]) -> ProxyFindings {
        var found = ProxyFindings()
        found.http = endpoint(settings, "HTTP") ?? endpoint(settings, "HTTPS")
        if flag(settings["ProxyAutoConfigEnable"]), let url = settings["ProxyAutoConfigURLString"] as? String, !url.isEmpty {
            found.pac = SecretMasker.mask(url)
        }

        if let scoped = settings["__SCOPED__"] as? [String: Any] {
            found.vpn = scoped.keys.filter { name in vpnPrefixes.contains { name.hasPrefix($0) } }.sorted()
        }

        return found
    }

    /// «адрес:порт» прокси `prefix` (HTTP, HTTPS), если он включен; порта нет — только адрес.
    private static func endpoint(_ settings: [String: Any], _ prefix: String) -> String? {
        guard flag(settings[prefix + "Enable"]), let host = settings[prefix + "Proxy"] as? String, !host.isEmpty else {
            return nil
        }

        guard let port = (settings[prefix + "Port"] as? NSNumber)?.intValue, port > 0 else {
            return host
        }

        return host.contains(":") ? "[\(host)]:\(port)" : "\(host):\(port)"
    }

    private static func flag(_ value: Any?) -> Bool {
        (value as? NSNumber)?.boolValue ?? false
    }
}

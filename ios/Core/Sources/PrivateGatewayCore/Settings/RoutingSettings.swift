import Foundation

/// Что идет через VPN («Маршрутизация», доска 13) — как `RoutingMode` десктопа; по умолчанию — весь трафик.
public enum RoutingMode: String, CaseIterable, Sendable {
    case allTraffic = "AllTraffic"
    /// Все через VPN, кроме правил «Напрямую».
    case split = "Split"
}

/// Что делать с сайтом из правила.
public enum RuleAction: String, CaseIterable, Sendable {
    case direct = "Direct"
    case proxy = "Proxy"
    case block = "Block"
}

/// Правило для сайта: домен, «*.домен», адрес или подсеть (`SitePattern`) — как `DomainRule` десктопа.
public struct SiteRule: Equatable, Hashable, Sendable {
    public var pattern: String
    public var action: RuleAction

    public init(pattern: String, action: RuleAction) {
        self.pattern = pattern
        self.action = action
    }
}

/// «Маршрутизация» телефона — `RoutingSettings` десктопа без программ мимо VPN: обычному приложению iOS выбирать, какие
/// программы идут мимо туннеля, не дано (только профилям MDM). По умолчанию весь трафик через VPN; раздельный режим — только
/// осознанным выбором. «Напрямую» действует только в раздельном режиме, «Через VPN» и «Блокировать» — всегда. Правила идут
/// в конфиг Xray по порядку списка: «Напрямую» — прямым выходом ядра из расширения туннеля, мимо сервера.
public struct RoutingSettings: Equatable, Hashable, Sendable {
    public var mode: RoutingMode
    public var rules: [SiteRule]

    public init(mode: RoutingMode = .allTraffic, rules: [SiteRule] = []) {
        self.mode = mode
        self.rules = rules
    }

    public var isSplit: Bool {
        mode == .split
    }

    /// Идут ли сайты мимо VPN: правила «Напрямую» в раздельном режиме.
    public var hasBypassSites: Bool {
        isSplit && rules.contains { $0.action == .direct }
    }

    /// В режиме «Весь трафик» правила «Напрямую» молчат — об этом подпись над списком.
    public var hasIdleRules: Bool {
        !isSplit && rules.contains { $0.action == .direct }
    }

    /// Правила, которые получает ядро, по порядку списка: «Напрямую» — только в раздельном режиме.
    public var activeRules: [SiteRule] {
        rules.filter { isSplit || $0.action != .direct }
    }

    /// Новое правило — в конец списка, измененное — на свое место. Правило для того же сайта заменяется: два правила на один
    /// сайт противоречили бы друг другу (SaveRule десктопа).
    public func saving(_ rule: SiteRule, replacing original: SiteRule? = nil) -> RoutingSettings {
        var kept = rules.filter { $0 == original || $0.pattern != rule.pattern }
        if let original, let index = kept.firstIndex(of: original) {
            kept[index] = rule
        } else {
            kept.append(rule)
        }

        return RoutingSettings(mode: mode, rules: kept)
    }

    public func removing(_ rule: SiteRule) -> RoutingSettings {
        RoutingSettings(mode: mode, rules: rules.filter { $0 != rule })
    }

    /// Только шаблоны, которые примет ядро телефона, в обычном виде (`SitePattern.normalize`); правило для того же сайта —
    /// одно, первое по списку.
    public var normalized: RoutingSettings {
        var seen = Set<String>()
        var kept: [SiteRule] = []
        for rule in rules {
            guard let pattern = SitePattern.normalize(rule.pattern), seen.insert(pattern).inserted else {
                continue
            }

            kept.append(SiteRule(pattern: pattern, action: rule.action))
        }

        return RoutingSettings(mode: mode, rules: kept)
    }
}

/// Шаблон сайта для правила — `SitePattern` десктопа без «geosite:»: базы geosite у ядра телефона нет, и с таким правилом
/// ядро не запустилось бы, поэтому шаблон не принимается.
public enum SitePattern {
    /// Длиннее имени в DNS не бывает.
    public static let maxLength = 253

    /// «example.com», «*.example.com» — строчными; адрес «203.0.113.5» или подсеть «198.51.100.0/24», «2001:db8::/32» —
    /// подсеть приводится к адресу сети. Адрес с «https://» и путем сокращается до домена: так его чаще всего и вставляют.
    /// nil — не подходит.
    public static func normalize(_ text: String) -> String? {
        let value = text.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        if value.isEmpty || value.hasPrefix("geosite:") {
            return nil
        }

        if let address = address(value) {
            return address
        }

        var host = value
        if let url = URLComponents(string: value), let scheme = url.scheme, scheme == "http" || scheme == "https", let name = url.host, !name.isEmpty {
            host = name
        }

        let wildcard = host.hasPrefix("*.")
        let name = wildcard ? String(host.dropFirst(2)) : host
        let labels = name.split(separator: ".", omittingEmptySubsequences: false)
        // «*.local» — одна метка после звездочки допустима; без звездочки нужен домен с точкой. Последняя метка — не число.
        guard name.count <= maxLength, labels.count >= (wildcard ? 1 : 2), labels.allSatisfy(validLabel), let last = labels.last,
              !last.allSatisfy({ $0.isASCII && $0.isNumber })
        else {
            return nil
        }

        return (wildcard ? "*." : "") + name
    }

    /// Шаблон — адрес или подсеть, а не имя: правило сравнивает IP соединения.
    public static func isAddress(_ pattern: String) -> Bool {
        address(pattern) != nil
    }

    /// Шаблон имени — в условие Xray: «example.com» и «*.example.com» — домен с поддоменами.
    public static func domainCondition(_ pattern: String) -> String {
        "domain:" + (pattern.hasPrefix("*.") ? String(pattern.dropFirst(2)) : pattern)
    }

    /// Метка имени: 1–63 латинские буквы, цифры и дефисы, дефис не по краям.
    private static func validLabel(_ label: Substring) -> Bool {
        (1...63).contains(label.count) && label.first != "-" && label.last != "-"
            && label.allSatisfy { $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "-") }
    }

    /// Адрес или подсеть в обычном виде (`IpAddress.format`); иначе nil. IPv4 — только четыре числа через точку, без зоны.
    /// Подсеть /0 — весь интернет: это режим, а не правило.
    private static func address(_ value: String) -> String? {
        let parts = value.split(separator: "/", maxSplits: 1, omittingEmptySubsequences: false)
        guard let first = parts.first, !first.contains("%"), let bytes = IpAddress.parse(String(first)) else {
            return nil
        }

        guard parts.count == 2 else {
            return IpAddress.format(bytes)
        }

        let digits = parts[1]
        guard (1...3).contains(digits.count), digits.allSatisfy({ $0.isASCII && $0.isNumber }), let prefix = Int(digits), (1...bytes.count * 8).contains(prefix) else {
            return nil
        }

        var network = bytes
        for index in network.indices {
            let keep = min(max(prefix - index * 8, 0), 8)
            network[index] &= UInt8(truncatingIfNeeded: 0xFF << (8 - keep))
        }

        return IpAddress.format(network) + "/\(prefix)"
    }
}

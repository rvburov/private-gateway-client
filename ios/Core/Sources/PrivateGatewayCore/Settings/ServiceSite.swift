/// Сервис для «Все адреса сервиса» (решение 741): `id` — категория geosite десктопа, `name` — название (на всех языках одно),
/// адреса — домены с поддоменами (`domains`) и точные имена (`full`). Список — `ServiceSites` (shared/specifications/routing/
/// services.json, код пишет scripts/gen-services.cs): тот же у десктопа и Android.
public struct ServiceSite: Equatable, Hashable, Sendable {
    public let id: String
    public let name: String
    public let domains: [String]
    public let full: [String]

    public init(id: String, name: String, domains: [String], full: [String]) {
        self.id = id
        self.name = name
        self.domains = domains
        self.full = full
    }

    /// Шаблон правила «geosite:id» — тот же, что у десктопа: правило переносится копией настроек.
    public var pattern: String {
        SitePattern.geosite + id
    }

    /// Входит ли имя в адреса сервиса: домен списка, его поддомен или точное имя.
    public func contains(_ host: String) -> Bool {
        full.contains(host) || domains.contains { host == $0 || host.hasSuffix("." + $0) }
    }

    /// Условия доменов для ядра: «domain:» — с поддоменами, «full:» — только имя. Базы geosite у ядра телефона нет.
    public var conditions: [String] {
        domains.map { "domain:" + $0 } + full.map { "full:" + $0 }
    }
}

public extension ServiceSites {
    /// Сервис правила «geosite:id» из списка; иначе nil.
    static func of(pattern: String) -> ServiceSite? {
        guard pattern.hasPrefix(SitePattern.geosite) else { return nil }
        let id = String(pattern.dropFirst(SitePattern.geosite.count))
        return all.first { $0.id == id }
    }

    /// Сервис, в адреса которого входит обычный домен («youtube.com», «m.youtube.com»); иначе nil.
    static func find(_ pattern: String) -> ServiceSite? {
        SitePattern.isPlainDomain(pattern) ? all.first { $0.contains(pattern) } : nil
    }
}

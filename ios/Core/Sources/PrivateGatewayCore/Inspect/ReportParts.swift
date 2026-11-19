import Foundation

/// Части отчета «Проверки», общие с десктопом (`DnsReport`, `ProblemLines`) и Android: раздел DNS и ошибки журнала.
public enum ReportParts {
    /// Вид своего DNS для отчета — без самого адреса: путь DoH может нести токен доступа. Ключ текста —
    /// diag.report.dns.custom.<вид>: missing, private, doh, address, invalid.
    public static func customKind(_ tunnel: TunnelSettings) -> String {
        let custom = tunnel.customDns?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        if custom.isEmpty {
            return "missing"
        }

        if tunnel.lanDns != nil {
            return "private"
        }

        if TunnelSettings.validCustomDns(custom) {
            return custom.lowercased().hasPrefix("https://") ? "doh" : "address"
        }

        return "invalid"
    }

    /// Ошибка или предупреждение журнала и сколько раз оно было: первая запись, текст без секретов, число и последняя запись.
    public struct Problem: Equatable, Sendable {
        public let first: TunnelLogEntry
        public let message: String
        public let count: Int
        public let last: TunnelLogEntry
    }

    /// Ошибки и предупреждения журнала — одинаковые (уровень и текст после `SecretMasker`) одной строкой с числом повторов, по
    /// порядку первого появления. Так ошибка первой попытки не теряется среди обычных событий подробного журнала.
    public static func problems(_ entries: [TunnelLogEntry]) -> [Problem] {
        var order: [String] = []
        var groups: [String: Problem] = [:]
        for entry in entries where entry.level == .warning || entry.level == .error {
            let message = SecretMasker.mask(entry.message)
            let key = entry.level.rawValue + "|" + message
            if let known = groups[key] {
                groups[key] = Problem(first: known.first, message: message, count: known.count + 1, last: entry)
            } else {
                order.append(key)
                groups[key] = Problem(first: entry, message: message, count: 1, last: entry)
            }
        }

        return order.compactMap { groups[$0] }
    }
}

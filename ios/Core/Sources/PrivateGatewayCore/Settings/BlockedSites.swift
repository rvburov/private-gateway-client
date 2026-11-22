import Foundation

/// Только факт блокировки: адрес назначения без URL, исходного приложения, учетных данных и содержимого запросов.
public struct BlockedSiteEvent: Codable, Equatable, Identifiable, Sendable {
    public let target: String
    public var count: Int
    public var lastSeen: Date
    public var id: String { target }
    /// IP может принадлежать CDN или общему узлу: одной кнопкой его не разрешаем.
    public var canAllow: Bool { SitePattern.isPlainDomain(target) }

    public init(target: String, count: Int, lastSeen: Date) {
        self.target = target
        self.count = count
        self.lastSeen = lastSeen
    }
}

public struct BlockedSitesSnapshot: Codable, Sendable {
    public let available: Bool
    public let events: [BlockedSiteEvent]
    public init(available: Bool, events: [BlockedSiteEvent]) {
        self.available = available
        self.events = events
    }
}

/// Ограниченный журнал текущего процесса расширения. Ничего не пишет на диск и не попадает в отчет поддержки.
public final class BlockedSiteHistory: @unchecked Sendable {
    public static let limit = 250
    private let lock = NSLock()
    private var items: [String: BlockedSiteEvent] = [:]

    public init() {}

    /// Принимаются только настоящие сообщения Xray о направлении в наш выход block.
    public func receive(_ line: String, now: Date = Date(), calendar: Calendar = .current) {
        guard let target = Self.target(line) else { return }
        lock.lock()
        defer { lock.unlock() }
        prune(now, calendar)
        if var item = items[target] {
            item.count = min(item.count + 1, 1_000_000)
            item.lastSeen = now
            items[target] = item
        } else {
            if items.count >= Self.limit, let oldest = items.values.min(by: { $0.lastSeen < $1.lastSeen }) {
                items.removeValue(forKey: oldest.target)
            }
            items[target] = BlockedSiteEvent(target: target, count: 1, lastSeen: now)
        }
    }

    public func snapshot(now: Date = Date(), calendar: Calendar = .current) -> [BlockedSiteEvent] {
        lock.lock()
        defer { lock.unlock() }
        prune(now, calendar)
        return items.values.sorted { $0.lastSeen == $1.lastSeen ? $0.target < $1.target : $0.lastSeen > $1.lastSeen }
    }

    public func clear() {
        lock.lock()
        items = [:]
        lock.unlock()
    }

    private func prune(_ now: Date, _ calendar: Calendar) {
        items = items.filter { calendar.isDate($0.value.lastSeen, inSameDayAs: now) }
    }

    /// Только целая Info-строка dispatcher закрепленного Xray после sniff/FakeDNS. Предупреждение, цитирующее detour,
    /// не является событием маршрутизации. Access выключен и не используется как источник фактов.
    public static func target(_ line: String) -> String? {
        guard line.utf8.count <= 16_384 else { return nil }
        guard let match = detourLine.firstMatch(in: line, range: NSRange(line.startIndex..., in: line)),
              let captured = Range(match.range(at: 1), in: line) else { return nil }
        let endpoint = String(line[captured])
        let address = String(endpoint.dropFirst(4))
        guard let url = URLComponents(string: "http://" + address), url.user == nil, url.password == nil,
              url.path.isEmpty, url.query == nil, url.fragment == nil, let port = url.port, (1...65535).contains(port),
              let host = url.host, let normalized = ProbeTarget.normalize(host) else { return nil }
        // Синтетический адрес FakeDNS не сообщает, какой сайт был назначением: его нельзя показывать как адрес сайта.
        if let bytes = IpAddress.parse(normalized), bytes.count == 4, bytes[0] == 198, bytes[1] == 18 || bytes[1] == 19 { return nil }
        return normalized.lowercased()
    }

    nonisolated(unsafe) private static let detourLine = try! NSRegularExpression(
        pattern: #"\A(?:[0-9]{4}/[0-9]{2}/[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]+)? )?\[Info\] (?:\[[0-9]+\] )?app/dispatcher: (?:Hit route rule: \[[^\r\n\]]+\] so )?taking detour \[block\] for \[((?:tcp|udp):[^\r\n]+)\]\z"#
    )
}

extension TunnelMessages {
    public static let blockedSites = Data("blocked-sites".utf8)
}

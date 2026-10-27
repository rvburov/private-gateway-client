import Foundation

/// Вкладки списка серверов.
public enum ServerListTab: Sendable {
    case all
    case favorites
    case recent
}

/// Порядок строк в списке серверов.
public enum ServerSort: Sendable {
    /// Сначала самые быстрые; без ответа и не измеренные — в конце.
    case latency
    case name
}

/// Что показать в списке серверов: поиск, вкладка, скрытые протоколы и порядок.
public struct ServerListQuery: Equatable, Sendable {
    public var search: String
    public var tab: ServerListTab
    public var hiddenProtocols: Set<ProxyProtocol>
    public var sort: ServerSort

    public init(search: String = "", tab: ServerListTab = .all, hiddenProtocols: Set<ProxyProtocol> = [], sort: ServerSort = .latency) {
        self.search = search
        self.tab = tab
        self.hiddenProtocols = hiddenProtocols
        self.sort = sort
    }
}

/// Подписка для заголовка группы: «{name} · {n}», «обновлено HH:mm» (`updatedAt`; nil — ни разу).
public struct SubscriptionRef: Equatable, Hashable, Sendable {
    public let id: String
    public let name: String
    public let updatedAt: Date?

    public init(id: String, name: String, updatedAt: Date?) {
        self.id = id
        self.name = name
        self.updatedAt = updatedAt
    }
}

/// Группа строк под одним заголовком: подписка или «Мои серверы» (subscription = nil).
public struct ServerGroup: Equatable, Sendable {
    public let subscription: SubscriptionRef?
    public let servers: [ServerItem]
}

/// Почему список пуст: серверов нет вовсе, нет избранных, ни разу не выбирали или ничего не нашлось.
public enum ServerListEmpty: Sendable {
    case noServers
    case noFavorites
    case noRecent
    case nothingFound
}

/// Поиск, вкладки, фильтр протоколов и сортировка списка серверов (доска 2) — `ServerList` Android: только отбирает и
/// упорядочивает, сам выбранный сервер не меняет. Поиск не видит адреса — он запечатан; «Недавние» — в порядке выбора, свежие
/// сверху, сортировка их не трогает (решения 420 и 421).
public enum ServerList {
    /// Сколько последних выбранных серверов показывать во вкладке «Недавние».
    public static let recentLimit = 10

    /// Порядок протоколов в фильтре: сначала основные, как в макете.
    public static let protocolOrder: [ProxyProtocol] = [.vless, .vmess, .hysteria2, .tuic, .wireGuard, .trojan, .shadowsocks]

    /// Протоколы, которые есть среди серверов, — только они попадают в фильтр.
    public static func protocolsIn(_ servers: [ServerItem]) -> [ProxyProtocol] {
        let present = Set(servers.map(\.protocol))
        return protocolOrder.filter { present.contains($0) }
    }

    /// Число на кнопке фильтра: скрытые протоколы из тех, что есть в списке, и порядок не по задержке.
    public static func filterCount(_ servers: [ServerItem], _ query: ServerListQuery) -> Int {
        protocolsIn(servers).filter { query.hiddenProtocols.contains($0) }.count + (query.sort == .latency ? 0 : 1)
    }

    /// Серверы вкладки по запросу, разложенные по группам. На вкладке «Все» — по подпискам в их порядке, серверы без подписки
    /// (или из неизвестной) последней группой; на остальных — одна группа.
    ///
    /// - Parameter locale: язык интерфейса: по нему сравниваются названия, без учета регистра.
    public static func build(_ servers: [ServerItem], _ subscriptions: [SubscriptionRef], _ query: ServerListQuery, locale: Locale) -> [ServerGroup] {
        let rows = filter(servers, query)
        if query.tab != .all {
            let shown = query.tab == .recent ? rows : sorted(rows, query.sort, locale)
            return shown.isEmpty ? [] : [ServerGroup(subscription: nil, servers: shown)]
        }

        var groups: [ServerGroup] = []
        for subscription in subscriptions {
            let own = rows.filter { $0.subscriptionId == subscription.id }
            if !own.isEmpty {
                groups.append(ServerGroup(subscription: subscription, servers: sorted(own, query.sort, locale)))
            }
        }

        // Без подписки или из удаленной — в «Мои серверы».
        let known = Set(subscriptions.map(\.id))
        let manual = rows.filter { $0.subscriptionId.map { !known.contains($0) } ?? true }
        if !manual.isEmpty {
            groups.append(ServerGroup(subscription: nil, servers: sorted(manual, query.sort, locale)))
        }

        return groups
    }

    /// Подходит ли сервер под строку поиска: название, код страны, протокол.
    public static func matches(_ server: ServerItem, _ search: String) -> Bool {
        let text = search.trimmingCharacters(in: .whitespacesAndNewlines)
        if text.isEmpty {
            return true
        }

        return [server.displayName, server.countryCode, ServerFacts.protocolName(server.protocol)]
            .contains { $0.range(of: text, options: .caseInsensitive) != nil }
    }

    /// Чем объяснить пустой список; nil — список не пуст. «Нет избранных» и «не выбирали» — только когда поиск и фильтр
    /// ничего не скрывают; порядок по названию строк не скрывает (решение 422).
    public static func empty(_ servers: [ServerItem], _ groups: [ServerGroup], _ query: ServerListQuery) -> ServerListEmpty? {
        if !groups.isEmpty {
            return nil
        }

        if servers.isEmpty {
            return .noServers
        }

        let searching = !query.search.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        if searching || protocolsIn(servers).contains(where: { query.hiddenProtocols.contains($0) }) {
            return .nothingFound
        }

        switch query.tab {
        case .favorites: return .noFavorites
        case .recent: return .noRecent
        case .all: return .nothingFound
        }
    }

    private static func filter(_ servers: [ServerItem], _ query: ServerListQuery) -> [ServerItem] {
        let rows = servers.filter { !query.hiddenProtocols.contains($0.protocol) && matches($0, query.search) }
        switch query.tab {
        case .all:
            return rows
        case .favorites:
            return rows.filter(\.favorite)
        case .recent:
            let used = rows.compactMap { row in row.lastUsedAt.map { (row, $0) } }
            return Array(used.sorted { $0.1 > $1.1 }.prefix(recentLimit).map(\.0))
        }
    }

    /// Порядок без перестановки равных: названия, различающиеся только регистром, остаются в порядке списка, как у десктопа.
    private static func sorted(_ rows: [ServerItem], _ sort: ServerSort, _ locale: Locale) -> [ServerItem] {
        rows.enumerated().sorted { left, right in
            let order = compare(left.element, right.element, sort, locale)
            return order == .orderedSame ? left.offset < right.offset : order == .orderedAscending
        }.map(\.element)
    }

    private static func compare(_ left: ServerItem, _ right: ServerItem, _ sort: ServerSort, _ locale: Locale) -> ComparisonResult {
        if sort == .latency {
            switch (left.latency?.ms, right.latency?.ms) {
            case let (l?, r?) where l != r: return l < r ? .orderedAscending : .orderedDescending
            case (.some, nil): return .orderedAscending
            case (nil, .some): return .orderedDescending
            default: break
            }
        }

        // По алфавиту языка и без учета регистра: «бета» между «Альфа» и «Яблоко», «е» с точками — рядом с «е».
        return left.displayName.compare(right.displayName, options: .caseInsensitive, range: nil, locale: locale)
    }
}

/// Быстрая смена на главной («Сменить ▾», доска 25) — как `QuickSwitch` Android: сервер в работе, за ним измеренные с лучшей
/// задержкой. Не измеренные и без ответа не попадают: выбирать среди них вслепую — дело полного списка.
public enum QuickSwitch {
    /// Строк в списке вместе с сервером в работе.
    public static let size = 3

    /// - Parameter inUse: сервер, через который идет защита, иначе выбранный; nil — ни того ни другого.
    public static func pick(_ servers: [ServerItem], inUse: ServerItem?) -> [ServerItem] {
        let fastest = servers.enumerated()
            .filter { $0.element.id != inUse?.id && $0.element.latency?.ms != nil }
            .sorted { ($0.element.latency?.ms ?? 0, $0.offset) < ($1.element.latency?.ms ?? 0, $1.offset) }
            .map(\.element)
        return Array(((inUse.map { [$0] } ?? []) + fastest).prefix(size))
    }
}

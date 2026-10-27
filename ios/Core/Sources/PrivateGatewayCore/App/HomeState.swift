/// Что делает кнопка включения, когда подключаться не к чему.
public enum PowerAction: Sendable {
    /// Включить или выключить защиту.
    case toggle

    /// Серверов нет — открыть добавление.
    case add

    /// Серверы есть, ни один не выбран — открыть список: сервер выбирает только пользователь.
    case choose
}

/// Состояние окна — как `MainUi` Android: туннель, серверы и их группы, выбранный и рабочий сервер. Рабочий — тот, через
/// который идет защита (его назвало расширение туннеля); защита выключена — выбранный.
public struct HomeState: Equatable, Sendable {
    public var status: TunnelStatus
    public var servers: [ServerItem]
    public var selectedId: String?
    public var subscriptions: [SubscriptionRef]
    public var measuring: Bool

    public init(
        status: TunnelStatus = TunnelStatus(), servers: [ServerItem] = [], selectedId: String? = nil, subscriptions: [SubscriptionRef] = [],
        measuring: Bool = false
    ) {
        self.status = status
        self.servers = servers
        self.selectedId = selectedId
        self.subscriptions = subscriptions
        self.measuring = measuring
    }

    /// Защита выключена или не включилась.
    public var off: Bool { status.state == .off || status.state == .error }

    /// Защита идет или поднимается через этот сервер («активен», «Используется сейчас» в карточке).
    public func active(_ id: String) -> Bool {
        !off && status.serverId == id
    }

    public var inUse: ServerItem? {
        if status.state != .off, let id = status.serverId, let server = servers.first(where: { $0.id == id }) {
            return server
        }

        return servers.first { $0.id == selectedId }
    }

    public var power: PowerAction {
        if !off || inUse != nil {
            return .toggle
        }

        return servers.isEmpty ? .add : .choose
    }
}

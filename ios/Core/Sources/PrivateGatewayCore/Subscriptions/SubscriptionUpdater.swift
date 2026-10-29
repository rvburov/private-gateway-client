import Foundation

/// Итог обновления подписки: новых и удаленных серверов, неудача с причиной или почему не начиналось.
public enum UpdateOutcome: Equatable, Sendable {
    case updated(added: Int, removed: Int)
    case failed(FailureReason)
    case blocked(UpdateBlock)
    /// Подписку удалили, пока шло обновление.
    case gone
    /// Файлы не прочитать — данные под защитой до первой разблокировки iPhone; ничего не делалось.
    case unavailable
}

/// Что сообщить пользователю после обновления: выбранный сервер пропал или изменился; подписка по расписанию не обновилась.
public enum SubscriptionNotice: Equatable, Sendable {
    case gone(server: String, subscription: String)
    case changed(server: String, subscription: String)
    case stale(subscription: String, reason: FailureReason)
}

/// Итог обновления и сообщения для окна или уведомлений.
public struct UpdateReport: Equatable, Sendable {
    public let outcome: UpdateOutcome
    public let notices: [SubscriptionNotice]

    public init(_ outcome: UpdateOutcome, notices: [SubscriptionNotice] = []) {
        self.outcome = outcome
        self.notices = notices
    }
}

/// Обновление подписок — `SubscriptionUpdater` Android. Загрузка — тем же разбором, что импорт; список меняет `applyUpdate`
/// (свое пользователя остается, выбранный пропавший — с пометкой); неудача оставляет прошлый список и запоминает причину.
/// С «только через VPN» используется только защищенный метод источника — явный локальный прокси ядра с выходом через
/// выбранный сервер. Снимок connected нужен для сообщения в окне, но не служит гарантией сетевого пути.
///
/// Когда — решает приложение (окно открыто, переход в «Защищено», раз в 10 минут, пока окно на экране, фоновое обновление
/// iOS): `updateDue` обновляет только те, которым пора. Отказ по расписанию — сообщение «не обновились»; кнопкой — только
/// итог на экране. Адреса подписки в итогах нет.
public actor SubscriptionUpdater {
    /// Как часто проверять, не пора ли, пока окно на экране.
    public static let checkEvery: TimeInterval = 10 * 60

    private let source: any SubscriptionSource
    private let servers: ServerStore
    private let settings: SettingsStore
    private let clock: @Sendable () -> Date

    /// Подписки, которые обновляются сейчас: вторая загрузка той же не начинается.
    public private(set) var busy: Set<String> = []

    public init(source: any SubscriptionSource, servers: ServerStore, settings: SettingsStore, clock: @escaping @Sendable () -> Date = { Date() }) {
        self.source = source
        self.servers = servers
        self.settings = settings
        self.clock = clock
    }

    /// Обновить подписки, которым пора; `connected` — защита подключена. Сообщения — обо всех обновленных.
    public func updateDue(connected: Bool) async -> [SubscriptionNotice] {
        guard let app = try? await settings.load(), UpdateGate.block(onlyViaVpn: app.subscriptions.onlyViaVpn, connected: connected) == .none,
              let groups = try? await servers.groups()
        else {
            return []
        }

        let times = groups.filter(\.canUpdate).map { SubscriptionTimes(id: $0.id, updatedAt: $0.updatedAt, lastAttemptAt: $0.lastAttemptAt) }
        var notices: [SubscriptionNotice] = []
        for id in SubscriptionSchedule.due(times, app.subscriptions.refresh, now: clock()) {
            if Task.isCancelled { break }
            notices += await update(id, connected: connected, scheduled: true).notices
        }

        return notices
    }

    /// Обновить одну подписку (кнопка «Обновить», новый адрес в «Изменить»); `scheduled` — сама, а не кнопкой: тогда неудача —
    /// сообщение «не обновились».
    public func update(_ id: String, connected: Bool, scheduled: Bool = false) async -> UpdateReport {
        guard !Task.isCancelled else { return UpdateReport(.unavailable) }
        guard let app = try? await settings.load() else {
            return UpdateReport(.unavailable)
        }

        let block = UpdateGate.block(onlyViaVpn: app.subscriptions.onlyViaVpn, connected: connected)
        if block != .none {
            return UpdateReport(.blocked(block))
        }

        // Проверка и отметка — одним шагом актора: двойное нажатие не грузит дважды.
        guard busy.insert(id).inserted else {
            return UpdateReport(.blocked(.busy))
        }

        defer { busy.remove(id) }
        guard let groups = try? await servers.groups() else {
            return UpdateReport(.unavailable)
        }

        guard let name = groups.first(where: { $0.id == id })?.name else {
            return UpdateReport(.gone)
        }

        guard let url = try? await servers.subscriptionUrl(id) else {
            return await failed(id, name, .noAddress, nil, scheduled)
        }

        let fetched = await SubscriptionFetch.fetch(url, from: source, protected: app.subscriptions.onlyViaVpn)
        guard !Task.isCancelled else { return UpdateReport(.unavailable) }
        switch fetched {
        case let .failed(failure, status):
            return await failed(id, name, failure, status, scheduled)
        case let .fetched(list, _):
            // Выбор — на момент записи: пока шла загрузка, пользователь мог выбрать другой сервер.
            let current = try? await settings.load()
            let selected = current.map(\.selectedServerId) ?? app.selectedServerId
            guard let update = try? await servers.applyUpdate(id, incoming: list, selectedId: selected) else {
                return UpdateReport(.unavailable)
            }

            var notices: [SubscriptionNotice] = []
            if app.subscriptions.notifySelected, let server = update.selectedName {
                switch update.selected {
                case .missing: notices.append(.gone(server: server, subscription: name))
                case .changed: notices.append(.changed(server: server, subscription: name))
                case .none: break
                }
            }

            return UpdateReport(.updated(added: update.added, removed: update.removed), notices: notices)
        }
    }

    private func failed(_ id: String, _ name: String, _ failure: DownloadFailure, _ status: Int?, _ scheduled: Bool) async -> UpdateReport {
        try? await servers.markFailed(id, failure: failure, status: status)
        let reason = FailureReason.of(failure, status: status)
        return UpdateReport(.failed(reason), notices: scheduled ? [.stale(subscription: name, reason: reason)] : [])
    }
}

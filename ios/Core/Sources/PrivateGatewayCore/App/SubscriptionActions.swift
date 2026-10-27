import Foundation

/// Итог «Изменить» у подписки: неверный адрес — ничего не сохранено; иначе сохранено, и новый адрес сразу проверен загрузкой.
public enum EditResult: Equatable, Sendable {
    case badAddress
    case saved(UpdateReport?)
}

/// Действия «Профили и подписки» — как `SubscriptionsViewModel` Android: обновить кнопкой, изменить название и адрес,
/// удалить группу со всеми серверами.
public struct SubscriptionActions: Sendable {
    public let servers: ServerStore
    public let settings: SettingsStore
    public let control: any TunnelControl
    public let updater: SubscriptionUpdater

    public init(servers: ServerStore, settings: SettingsStore, control: any TunnelControl, updater: SubscriptionUpdater) {
        self.servers = servers
        self.settings = settings
        self.control = control
        self.updater = updater
    }

    /// «Обновить»; `connected` — защита подключена.
    public func update(_ id: String, connected: Bool) async -> UpdateReport {
        await updater.update(id, connected: connected)
    }

    /// Название и, если введен, новый адрес — он сразу проверяется загрузкой, как у десктопа. Адрес не http(s) — ничего не
    /// сохраняется; пустое поле адреса — адрес прежний.
    public func edit(_ id: String, name: String, url: String, connected: Bool) async throws -> EditResult {
        let text = url.trimmingCharacters(in: .whitespacesAndNewlines)
        var address: URLComponents?
        if !text.isEmpty {
            guard let parsed = SubscriptionAddress.parse(text) else {
                return .badAddress
            }

            address = parsed
        }

        try await servers.edit(id, name: name, url: address)
        guard address != nil else {
            return .saved(nil)
        }

        return .saved(await updater.update(id, connected: connected))
    }

    /// Удалить группу со всеми серверами; nil — «Мои серверы». Защита идет через ее сервер — VPN отключается, другой сервер
    /// пользователь выберет сам; выбранный из группы — выбор снимается.
    public func remove(group: String?, inUse: Bool) async throws {
        if inUse {
            await control.disconnect()
        }

        let removed = try await servers.remove(group: group)
        if let selected = try await settings.load().selectedServerId, removed.contains(selected) {
            try await settings.select(nil)
        }
    }
}

extension TextKeys {
    /// Итог кнопки «Обновить» — ключ и подстановки; nil — сказать нечего (подписку удалили, файлы не прочитать).
    public static func updated(_ outcome: UpdateOutcome, name: String) -> (key: String, arguments: [String])? {
        switch outcome {
        case let .updated(added, removed): return ("subs.updated", [name, String(added), String(removed)])
        case .failed: return ("subs.failed", [name])
        case .blocked(.protectionOff): return ("subs.blocked.off", [])
        case .blocked, .gone, .unavailable: return nil
        }
    }

    /// Сообщение о выбранном сервере или о подписке, которая по расписанию не обновилась.
    public static func notice(_ notice: SubscriptionNotice) -> (key: String, arguments: [String]) {
        switch notice {
        case let .gone(server, subscription): return ("subs.notice.missing", [server, subscription])
        case let .changed(server, subscription): return ("subs.notice.changed", [server, subscription])
        case let .stale(subscription, _): return ("subs.failed", [subscription])
        }
    }
}

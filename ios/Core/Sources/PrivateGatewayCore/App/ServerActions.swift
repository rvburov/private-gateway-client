/// Команды туннелю: в приложении — профиль VPN в настройках iOS (`NETunnelProviderManager`), в тестах — подделка.
public protocol TunnelControl: Sendable {
    /// Поднять туннель или поднять его заново — с выбранным сервером и текущими настройками (их читает расширение туннеля).
    /// Первое подключение спрашивает у пользователя разрешение на VPN; отказ — ошибка.
    func connect() async throws

    func disconnect() async
}

/// Действия окна над серверами — как `MainViewModel` Android: сервер выбирается только нажатием пользователя; работающая
/// защита сразу переходит на новый выбранный (путь без окна утечки, решение 410); удаление сервера в работе сначала
/// отключает защиту — другой сервер сам не выбирается (доска 18).
public struct ServerActions: Sendable {
    public let servers: ServerStore
    public let settings: SettingsStore
    public let control: any TunnelControl

    public init(servers: ServerStore, settings: SettingsStore, control: any TunnelControl) {
        self.servers = servers
        self.settings = settings
        self.control = control
    }

    /// Выбор сервера. Тот же — ничего не меняется (и «Недавние» тоже); другой — запоминается для «Недавних», и защита, если
    /// она не выключена и не выключается, сразу переходит на него.
    public func select(_ id: String, state: TunnelState) async throws {
        if try await choose(id), state != .off, state != .stopping {
            try await control.connect()
        }
    }

    /// «Подключиться» в карточке: выбрать сервер и включить защиту через него — после того, как выбор записан: расширение
    /// туннеля читает выбранный сервер само. Защита уже идет через этот сервер или выключается — ничего не делать.
    public func connectTo(_ id: String, state: TunnelState) async throws {
        let changed = try await choose(id)
        switch state {
        case .off, .error:
            try await control.connect()
        case .connecting, .connected, .reconnecting:
            if changed {
                try await control.connect()
            }
        case .stopping:
            break
        }
    }

    /// Удалить сервер; защита идет через него — сначала отключить ее.
    public func delete(_ id: String, status: TunnelStatus) async throws {
        if status.state != .off, status.state != .error, status.serverId == id {
            await control.disconnect()
        }

        try await servers.delete(id)
        if try await settings.load().selectedServerId == id {
            try await settings.select(nil)
        }
    }

    public func setFavorite(_ id: String, _ favorite: Bool) async throws {
        try await servers.setFavorite(id, favorite)
    }

    /// Свое название: без пробелов по краям, 1–64 знака (иначе не сохраняется). Совпало с названием из ссылки — своего нет:
    /// обновление подписки снова сможет его поменять.
    public func rename(_ id: String, _ name: String) async throws {
        guard let valid = ServerNames.validate(name), let server = try await servers.servers().first(where: { $0.id == id }) else {
            return
        }

        try await servers.rename(id, valid == server.linkName ? nil : valid)
    }

    /// Записать выбор; тот же сервер — false.
    private func choose(_ id: String) async throws -> Bool {
        if try await settings.load().selectedServerId == id {
            return false
        }

        try await servers.markUsed(id)
        try await settings.select(id)
        return true
    }
}

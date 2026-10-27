/// Изменение настроек из окна — как `SettingsViewModel` Android вместе со службой: настройка записывается в общую папку, и под
/// защитой то, что получает ядро (DNS, IPv6, локальная сеть) или профиль VPN (блок при обрыве, локальная сеть), сразу
/// уходит в работу — переподключением без окна утечки (решение 410). Что ядро не получает (неверный свой DNS равен «из
/// профиля», подписки) — без переподключения.
public struct SettingsActions: Sendable {
    public let settings: SettingsStore
    public let control: any TunnelControl

    public init(settings: SettingsStore, control: any TunnelControl) {
        self.settings = settings
        self.control = control
    }

    public func setTunnel(_ next: TunnelSettings, state: TunnelState) async throws {
        try await apply(state) { try await settings.setTunnel(next) }
    }

    public func setKillSwitch(_ on: Bool, state: TunnelState) async throws {
        try await apply(state) { try await settings.setKillSwitch(on) }
    }

    public func setSubscriptions(_ next: SubscriptionSettings) async throws {
        try await settings.setSubscriptions(next)
    }

    private func apply(_ state: TunnelState, _ change: () async throws -> Void) async throws {
        let before = try await settings.load()
        try await change()
        let after = try await settings.load()
        let running = state == .connecting || state == .connected || state == .reconnecting
        if running, before.tunnel.needsReconnect(after.tunnel) || VpnProfile.of(before) != VpnProfile.of(after) {
            try await control.connect()
        }
    }
}

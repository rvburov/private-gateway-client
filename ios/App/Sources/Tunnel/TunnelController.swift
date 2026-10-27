import NetworkExtension
import PrivateGatewayCore

/// Профиль VPN в настройках iOS и команды туннелю — `TunnelControl` для действий окна. Профиль один: создается при первом
/// подключении (iOS спрашивает разрешение на VPN), дальше перед каждым подключением приводится к настройкам (`VpnProfile`):
/// блок при обрыве и локальная сеть — свойства профиля, а не конфига ядра. Сервер и остальные настройки расширение туннеля
/// читает само из общей папки.
@MainActor
final class TunnelController: TunnelControl {
    private let settings: SettingsStore
    private var manager: NETunnelProviderManager?

    /// Состояние VPN в системе сменилось.
    var onChange: () -> Void = {}

    init(settings: SettingsStore) {
        self.settings = settings
        _ = NotificationCenter.default.addObserver(forName: .NEVPNStatusDidChange, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated {
                self?.onChange()
            }
        }
    }

    var system: SystemVpnStatus {
        guard let status = manager?.connection.status else {
            return .invalid
        }

        switch status {
        case .invalid: return .invalid
        case .disconnected: return .disconnected
        case .connecting: return .connecting
        case .connected: return .connected
        case .reasserting: return .reasserting
        case .disconnecting: return .disconnecting
        @unknown default: return .invalid
        }
    }

    /// Профиль, который приложение уже сохраняло; нет — nil, разрешение спросит первое подключение.
    func load() async {
        manager = try? await Self.managers().first
        onChange()
    }

    /// iOS не дала сохранить профиль или поднять туннель (разрешение не дано или отозвано, VPN занят) — PGC-NET-010.
    func connect() async throws {
        let profile = try await VpnProfile.of(settings.load())
        do {
            let manager = try await prepared(profile)
            try manager.connection.startVPNTunnel()
        } catch {
            throw TunnelFailure(TunnelErrors.otherVpn, detail: "\(error)")
        }
    }

    func disconnect() async {
        manager?.connection.stopVPNTunnel()
    }

    /// Профиль с нужными свойствами, сохраненный в настройках iOS. Сохранение нового профиля спрашивает разрешение; после
    /// сохранения профиль перечитывается — иначе первый запуск iOS отклоняет.
    private func prepared(_ profile: VpnProfile) async throws -> NETunnelProviderManager {
        let manager = manager ?? NETunnelProviderManager()
        let tunnel = manager.protocolConfiguration as? NETunnelProviderProtocol ?? NETunnelProviderProtocol()
        tunnel.providerBundleIdentifier = VpnProfile.providerBundleIdentifier
        tunnel.serverAddress = VpnProfile.serverAddress
        tunnel.includeAllNetworks = profile.includeAllNetworks
        tunnel.excludeLocalNetworks = profile.excludeLocalNetworks
        tunnel.enforceRoutes = profile.enforceRoutes
        tunnel.disconnectOnSleep = false
        manager.protocolConfiguration = tunnel
        manager.localizedDescription = VpnProfile.serverAddress
        manager.isEnabled = true
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, any Error>) in
            // Обработчики — без изоляции главного потока: в каком потоке их вызовет система, не обещано.
            manager.saveToPreferences { @Sendable error in
                Self.resume(continuation, error)
            }
        }
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, any Error>) in
            manager.loadFromPreferences { @Sendable error in
                Self.resume(continuation, error)
            }
        }
        self.manager = manager
        return manager
    }

    private nonisolated static func resume(_ continuation: CheckedContinuation<Void, any Error>, _ error: (any Error)?) {
        if let error {
            continuation.resume(throwing: error)
        } else {
            continuation.resume()
        }
    }

    /// Профили приложения в настройках iOS. Список уходит из обработчика завершения в главный поток целиком — его больше
    /// никто не держит.
    private static func managers() async throws -> [NETunnelProviderManager] {
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Handoff<[NETunnelProviderManager]>, any Error>) in
            NETunnelProviderManager.loadAllFromPreferences { @Sendable managers, error in
                if let error {
                    continuation.resume(throwing: error)
                } else {
                    continuation.resume(returning: Handoff(value: managers ?? []))
                }
            }
        }.value
    }
}

/// Значение, которое передается из обработчика завершения системы в главный поток и больше нигде не используется.
private struct Handoff<Value>: @unchecked Sendable {
    let value: Value
}

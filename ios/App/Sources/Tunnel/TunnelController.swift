import NetworkExtension
import PrivateGatewayCore

/// Профиль VPN в настройках iOS и команды туннелю — `TunnelControl` для действий окна. Профиль один: создается при первом
/// подключении (iOS спрашивает разрешение на VPN), дальше перед каждым подключением приводится к настройкам (`VpnProfile`):
/// блок при обрыве, локальная сеть и «Постоянная защита» (подключение по требованию) — свойства профиля, а не конфига ядра.
/// Сервер и остальные настройки расширение туннеля читает само из общей папки.
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

    /// Поднять туннель или поднять его заново с выбранным сервером и настройками. Туннель уже идет и профиль тот же — расширению
    /// сообщение «перечитать» (туннель не опускается, трафик ждет в нем, решение 410); профиль другой (блок при обрыве, локальная
    /// сеть) — туннель опускается, профиль сохраняется, туннель поднимается. iOS не дала сохранить профиль или поднять туннель
    /// (разрешение не дано или отозвано, VPN занят) — PGC-NET-010; расширение не перезапустило ядро — его код.
    func connect() async throws {
        let profile = try await VpnProfile.of(settings.load())
        if let manager, Self.running(manager), let saved = Self.profile(of: manager), saved.sameTunnel(profile) {
            // «Постоянная защита» меняется на ходу: туннель не опускается.
            if saved.onDemand != profile.onDemand {
                Self.onDemand(manager, profile.onDemand)
                try? await Self.save(manager)
            }

            try await reload(manager)
            return
        }

        do {
            if let manager, Self.running(manager) {
                manager.connection.stopVPNTunnel()
                await Self.stopped(manager)
            }

            let manager = try await prepared(profile)
            // С подключением по требованию iOS могла поднять туннель сама, как только профиль сохранился.
            do {
                try manager.connection.startVPNTunnel()
            } catch {
                if !Self.running(manager) {
                    throw error
                }
            }
        } catch {
            throw TunnelFailure(TunnelErrors.otherVpn, detail: "\(error)")
        }
    }

    /// Выключить защиту. С «Постоянной защитой» сначала выключается подключение по требованию — иначе iOS сразу подняла бы
    /// туннель снова; следующее подключение включит его опять.
    func disconnect() async {
        if let manager, manager.isOnDemandEnabled {
            manager.isOnDemandEnabled = false
            try? await Self.save(manager)
        }

        manager?.connection.stopVPNTunnel()
    }

    /// Подключение по требованию: правило одно — «подключаться всегда» на любой сети.
    private static func onDemand(_ manager: NETunnelProviderManager, _ on: Bool) {
        let rule = NEOnDemandRuleConnect()
        rule.interfaceTypeMatch = .any
        manager.onDemandRules = [rule]
        manager.isOnDemandEnabled = on
    }

    private static func save(_ manager: NETunnelProviderManager) async throws {
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, any Error>) in
            manager.saveToPreferences { @Sendable error in
                Self.resume(continuation, error)
            }
        }
    }

    private static func running(_ manager: NETunnelProviderManager) -> Bool {
        [.connected, .connecting, .reasserting].contains(manager.connection.status)
    }

    /// Свойства сохраненного профиля.
    private static func profile(of manager: NETunnelProviderManager) -> VpnProfile? {
        guard let tunnel = manager.protocolConfiguration as? NETunnelProviderProtocol else {
            return nil
        }

        return VpnProfile(
            includeAllNetworks: tunnel.includeAllNetworks, excludeLocalNetworks: tunnel.excludeLocalNetworks, enforceRoutes: tunnel.enforceRoutes,
            onDemand: manager.isOnDemandEnabled
        )
    }

    /// Ждать, пока туннель опустится, — не дольше 10 секунд.
    private static func stopped(_ manager: NETunnelProviderManager) async {
        for _ in 0..<100 where running(manager) || manager.connection.status == .disconnecting {
            try? await Task.sleep(nanoseconds: 100_000_000)
        }
    }

    /// «Перечитать сервер и настройки» — расширению; ответ — удалось или код ошибки.
    private func reload(_ manager: NETunnelProviderManager) async throws {
        guard let session = manager.connection as? NETunnelProviderSession else {
            throw TunnelFailure(TunnelErrors.coreFailed, detail: "no provider session")
        }

        let response = try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Data?, any Error>) in
            do {
                try session.sendProviderMessage(TunnelMessages.reload) { @Sendable response in
                    continuation.resume(returning: response)
                }
            } catch {
                continuation.resume(throwing: TunnelFailure(TunnelErrors.coreFailed, detail: "\(error)"))
            }
        }
        if let code = TunnelMessages.failure(response) {
            throw TunnelFailure(code)
        }
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
        Self.onDemand(manager, profile.onDemand)
        // Обработчики — без изоляции главного потока: в каком потоке их вызовет система, не обещано.
        try await Self.save(manager)
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

    /// Защита подключена — по профилю VPN в системе; для фоновой работы без окна, когда модели нет.
    static func systemConnected() async -> Bool {
        (try? await managers().first)?.connection.status == .connected
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

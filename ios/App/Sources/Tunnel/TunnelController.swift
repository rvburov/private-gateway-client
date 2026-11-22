import NetworkExtension
import PrivateGatewayCore

/// Профиль VPN в настройках iOS и команды туннелю — `TunnelControl` для действий окна. Профиль один: создается при первом
/// подключении (iOS спрашивает разрешение на VPN), дальше перед каждым подключением приводится к настройкам (`VpnProfile`):
/// блок при обрыве, локальная сеть и «Постоянная защита» (подключение по требованию) — свойства профиля, а не конфига ядра.
/// Сервер и остальные настройки расширение туннеля читает само из общей папки.
@MainActor
final class TunnelController: TunnelControl {
    private let settings: SettingsStore
    private let account: AccountStore
    private let intents = TunnelIntentCoordinator()
    private var manager: NETunnelProviderManager?
    /// Режим последнего явного действия: сохранение системного профиля может еще показывать прежний режим.
    private var localFilterIntent: Bool?
    private var restorationPending = false

    /// Состояние VPN в системе сменилось.
    var onChange: () -> Void = {}

    init(settings: SettingsStore, account: AccountStore) {
        self.settings = settings
        self.account = account
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
        let ticket = intents.current
        let loaded = try? await Self.managers().first
        guard intents.isCurrent(ticket), manager == nil else { return }
        manager = loaded
        onChange()
    }

    /// Поднять туннель или поднять его заново с выбранным сервером и настройками. Туннель уже идет и профиль тот же — расширению
    /// сообщение «перечитать» (туннель не опускается, трафик ждет в нем, решение 410); профиль другой (блок при обрыве, локальная
    /// сеть) — туннель опускается, профиль сохраняется, туннель поднимается. iOS не дала сохранить профиль или поднять туннель
    /// (VPN занят, профиль отклонен) — PGC-NET-010, «Запретить» в запросе — `VpnPermissionDenied`; расширение не перезапустило
    /// ядро — его код.
    func connect() async throws {
        localFilterIntent = false
        try await intents.perform({ ticket in
            try await self.connect(ticket: ticket)
        }, cleanup: { _ = await self.deactivate() })
    }

    private func requireCurrent(_ ticket: UInt64) throws {
        try intents.requireCurrent(ticket)
        guard account.read().allowed(now: Date()) else { throw TunnelFailure(TunnelErrors.noAccess) }
    }

    private func connect(ticket: UInt64) async throws {
        try await prepareActivation(ticket)
        let profile = try await VpnProfile.of(settings.load())
        try requireCurrent(ticket)
        if let manager, Self.running(manager), !Self.localFilter(manager), let saved = Self.profile(of: manager), saved.sameTunnel(profile) {
            if saved.onDemand != profile.onDemand {
                Self.onDemand(manager, profile.onDemand)
                try await Self.save(manager)
                try requireCurrent(ticket)
            }
            try requireCurrent(ticket)
            try await reload(manager)
            try requireCurrent(ticket)
            return
        }

        do {
            if let manager, Self.running(manager) {
                Self.onDemand(manager, false)
                try await Self.save(manager)
                try requireCurrent(ticket)
                manager.connection.stopVPNTunnel()
                guard await Self.stopped(manager) else { throw TunnelFailure(TunnelErrors.coreFailed, detail: "native stop pending") }
                try requireCurrent(ticket)
            }
            let manager = try await prepared(profile, ticket: ticket)
            try requireCurrent(ticket)
            do {
                try manager.connection.startVPNTunnel()
            } catch {
                if !Self.running(manager) { throw error }
            }
            try requireCurrent(ticket)
            if profile.onDemand {
                // До явного запуска On Demand выключен: запоздалое первое сохранение не поднимет VPN само.
                Self.onDemand(manager, true)
                try await Self.save(manager)
                try requireCurrent(ticket)
            }
        } catch is CancellationError {
            throw CancellationError()
        } catch let failure as TunnelFailure {
            throw failure
        } catch {
            if VpnRefusal.permissionDenied(error) { throw VpnPermissionDenied() }
            throw TunnelFailure(TunnelErrors.otherVpn, detail: "\(error)")
        }
    }

    /// Стоп отменяет намерение до ожидания очереди и возвращается после завершения всех прежних saveToPreferences.
    func disconnect() async {
        _ = await disconnectConfirmed()
    }

    /// Синхронная отмена до сохранения барьера доступа; не дает прежнему start продолжиться после начала выхода.
    func requireManualActivation() {
        localFilterIntent = false
        restorationPending = true
        intents.invalidate()
    }

    /// Для входа в новый аккаунт нужны оба подтверждения: выключенный сохраненный профиль и остановленный туннель.
    func disconnectConfirmed() async -> Bool {
        requireManualActivation()
        var confirmed = false
        await intents.stop { confirmed = await self.deactivate() }
        return confirmed
    }

    private func prepareActivation(_ ticket: UInt64) async throws {
        try requireCurrent(ticket)
        if restorationPending {
            guard await deactivate() else { throw TunnelFailure(TunnelErrors.coreFailed, detail: "native cleanup pending") }
            try requireCurrent(ticket)
        }
        try await settings.setManualActivationRequired(false)
        try requireCurrent(ticket)
    }

    /// Очистка выполняется внутри той же очереди, что запуск. Поэтому ответ старого save не отменит новый сеанс.
    private func deactivate() async -> Bool {
        restorationPending = true
        let barrierSaved = (try? await settings.setManualActivationRequired(true)) != nil
        // Перечитываем именно установленные профили: отказ в первом разрешении не создает профиль, который надо сохранять.
        // Ошибка чтения не равна отсутствию профиля и не считается подтверждением очистки.
        let previous = manager
        do { manager = try await Self.managers().first } catch { return false }
        guard let manager else {
            // Профиль могли удалить отдельно, пока старое расширение еще завершает работу.
            let ended: Bool
            if let previous {
                previous.connection.stopVPNTunnel()
                ended = await Self.stopped(previous)
                if !ended { self.manager = previous }
            } else { ended = true }
            restorationPending = !(barrierSaved && ended)
            return !restorationPending
        }
        Self.onDemand(manager, false)
        manager.isEnabled = false
        if let tunnel = manager.protocolConfiguration as? NETunnelProviderProtocol {
            tunnel.includeAllNetworks = false
            tunnel.enforceRoutes = false
            tunnel.providerConfiguration?["localSiteFilter"] = false
            manager.protocolConfiguration = tunnel
        }
        manager.connection.stopVPNTunnel()
        let confirmed = await TunnelCleanupConfirmation.perform(disable: {
            try await Self.save(manager)
            try await Self.load(manager)
            guard let profile = Self.profile(of: manager) else { return false }
            return !manager.isEnabled && !manager.isOnDemandEnabled && !profile.includeAllNetworks && !profile.enforceRoutes && !Self.localFilter(manager)
        }, stopped: { await Self.stopped(manager) })
        restorationPending = !(barrierSaved && confirmed)
        return !restorationPending
    }

    /// Явный запуск местного фильтра. VPN-сервер не выбирается; профиль iOS не подключается по требованию.
    func connectLocalFilter() async throws {
        localFilterIntent = true
        try await intents.perform({ ticket in
            try await self.connectLocalFilter(ticket: ticket)
        }, cleanup: { _ = await self.deactivate() })
    }

    private func connectLocalFilter(ticket: UInt64) async throws {
        try await prepareActivation(ticket)
        if let manager, Self.running(manager) {
            manager.isOnDemandEnabled = false
            try await Self.save(manager)
            try requireCurrent(ticket)
            manager.connection.stopVPNTunnel()
            guard await Self.stopped(manager) else { throw TunnelFailure(TunnelErrors.coreFailed, detail: "native stop pending") }
            try requireCurrent(ticket)
        }
        let profile = VpnProfile(includeAllNetworks: false, excludeLocalNetworks: true, enforceRoutes: false, onDemand: false)
        let manager = try await prepared(profile, ticket: ticket, localFilter: true)
        try requireCurrent(ticket)
        try manager.connection.startVPNTunnel(options: ["localSiteFilter": NSNumber(value: true)])
    }

    func refresh() async throws {
        let local = localFilterIntent ?? manager.map(Self.localFilter) ?? false
        try await intents.perform({ ticket in
            try await self.prepareActivation(ticket)
            if local, let manager = self.manager, Self.running(manager), Self.localFilter(manager) {
                try await self.reload(manager)
                try self.requireCurrent(ticket)
            } else if local {
                try await self.connectLocalFilter(ticket: ticket)
            } else {
                try await self.connect(ticket: ticket)
            }
        }, cleanup: { _ = await self.deactivate() })
    }

    private static func localFilter(_ manager: NETunnelProviderManager) -> Bool {
        (manager.protocolConfiguration as? NETunnelProviderProtocol)?.providerConfiguration?["localSiteFilter"] as? Bool ?? false
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
    private static func stopped(_ manager: NETunnelProviderManager) async -> Bool {
        for _ in 0..<100 where running(manager) || manager.connection.status == .disconnecting {
            // Cleanup старого намерения выполняется и в отмененной Task: ее sleep вернулся бы сразу, не дождавшись iOS.
            await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
                DispatchQueue.global().asyncAfter(deadline: .now() + .milliseconds(100)) { continuation.resume() }
            }
        }
        return manager.connection.status == .disconnected || manager.connection.status == .invalid
    }

    /// «Перечитать сервер и настройки» — расширению; ответ — удалось или код ошибки.
    private func reload(_ manager: NETunnelProviderManager) async throws {
        guard let session = manager.connection as? NETunnelProviderSession else {
            throw TunnelFailure(TunnelErrors.coreFailed, detail: "no provider session")
        }

        let response = try await Self.message(session, TunnelMessages.reload)
        if let code = TunnelMessages.failure(response) {
            throw TunnelFailure(code)
        }
    }

    /// Вход для подписки — только у подключенного собственного профиля и только через сообщение расширению. Сохраненных
    /// учетных данных нет: каждое ядро выдает свой порт и пароль; нет ответа — загрузчик отказывает без прямого пути.
    func subscriptionProxy() async -> SubscriptionProxy? {
        guard !restorationPending, let manager else { return nil }
        return await Self.subscriptionProxy(manager)
    }

    /// «До сервера» для «Задержки» на «Проверке» — меряет расширение туннеля (мимо туннеля), только у подключенного профиля;
    /// не измерено или расширение не ответило — nil.
    func serverDelay() async -> Int? {
        guard let manager, !Self.localFilter(manager), manager.connection.status == .connected, let session = manager.connection as? NETunnelProviderSession else {
            return nil
        }

        return TunnelMessages.delayMs(try? await Self.message(session, TunnelMessages.delay))
    }

    func blockedSites() async -> BlockedSitesSnapshot? {
        guard let manager, Self.running(manager), let session = manager.connection as? NETunnelProviderSession,
              let data = try? await Self.message(session, TunnelMessages.blockedSites) else { return nil }
        return try? JSONDecoder().decode(BlockedSitesSnapshot.self, from: data)
    }

    static func protectedSubscriptionProxy() async -> SubscriptionProxy? {
        let container = SharedContainer.current()
        guard container.account.read().allowed(now: Date()), let settings = try? container.settings.loadNow(), !settings.manualActivationRequired else { return nil }
        guard let manager = try? await managers().first else { return nil }
        return await subscriptionProxy(manager)
    }

    private static func subscriptionProxy(_ manager: NETunnelProviderManager) async -> SubscriptionProxy? {
        guard !Self.localFilter(manager), manager.connection.status == .connected, let session = manager.connection as? NETunnelProviderSession,
              let data = try? await message(session, TunnelMessages.subscriptionProxy),
              let proxy = try? JSONDecoder().decode(SubscriptionProxy.self, from: data), proxy.valid
        else { return nil }
        return proxy
    }

    /// Ответ расширения ограничен по времени и отменяется вместе с фоновой задачей. Запоздалый callback не возобновляет
    /// continuation второй раз; зависший provider не держит окно или BGAppRefreshTask бесконечно.
    private static func message(_ session: NETunnelProviderSession, _ data: Data) async throws -> Data? {
        let reply = ProviderReply()
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                guard reply.install(continuation) else { return }
                do {
                    try session.sendProviderMessage(data) { @Sendable answer in reply.finish(.success(answer)) }
                } catch {
                    reply.finish(.failure(TunnelFailure(TunnelErrors.coreFailed)))
                }
                DispatchQueue.global().asyncAfter(deadline: .now() + 20) {
                    reply.finish(.failure(TunnelFailure(TunnelErrors.coreFailed)))
                }
            }
        } onCancel: {
            reply.finish(.failure(CancellationError()))
        }
    }

    /// Профиль с нужными свойствами, сохраненный в настройках iOS. Сохранение нового профиля спрашивает разрешение; после
    /// сохранения профиль перечитывается — иначе первый запуск iOS отклоняет.
    private func prepared(_ profile: VpnProfile, ticket: UInt64, localFilter: Bool = false) async throws -> NETunnelProviderManager {
        try requireCurrent(ticket)
        let manager = manager ?? NETunnelProviderManager()
        // Публикуется до save/разрешения системы: отмена всегда знает, какой профиль предстоит очистить.
        self.manager = manager
        let tunnel = manager.protocolConfiguration as? NETunnelProviderProtocol ?? NETunnelProviderProtocol()
        tunnel.providerBundleIdentifier = VpnProfile.providerBundleIdentifier
        tunnel.serverAddress = VpnProfile.serverAddress
        tunnel.includeAllNetworks = profile.includeAllNetworks
        tunnel.excludeLocalNetworks = profile.excludeLocalNetworks
        tunnel.enforceRoutes = profile.enforceRoutes
        tunnel.disconnectOnSleep = false
        tunnel.providerConfiguration = ["localSiteFilter": localFilter]
        manager.protocolConfiguration = tunnel
        manager.localizedDescription = VpnProfile.serverAddress
        manager.isEnabled = true
        Self.onDemand(manager, false)
        // Обработчики — без изоляции главного потока: в каком потоке их вызовет система, не обещано.
        try await Self.save(manager)
        try requireCurrent(ticket)
        try await Self.load(manager)
        try requireCurrent(ticket)
        return manager
    }

    private static func load(_ manager: NETunnelProviderManager) async throws {
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, any Error>) in
            manager.loadFromPreferences { @Sendable error in
                Self.resume(continuation, error)
            }
        }
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
        let container = SharedContainer.current()
        guard container.account.read().allowed(now: Date()), let settings = try? container.settings.loadNow(), !settings.manualActivationRequired else { return false }
        guard let manager = try? await managers().first else { return false }
        return manager.connection.status == .connected && !localFilter(manager)
    }

    /// Профили приложения в настройках iOS. Список уходит из обработчика завершения в главный поток целиком — его больше
    /// никто не держит.
    private static func managers() async throws -> [NETunnelProviderManager] {
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Handoff<[NETunnelProviderManager]>, any Error>) in
            NETunnelProviderManager.loadAllFromPreferences { @Sendable managers, error in
                if let error {
                    continuation.resume(throwing: error)
                } else {
                    let own = (managers ?? []).filter {
                        ($0.protocolConfiguration as? NETunnelProviderProtocol)?.providerBundleIdentifier == VpnProfile.providerBundleIdentifier
                    }
                    continuation.resume(returning: Handoff(value: own))
                }
            }
        }.value
    }
}

/// Значение, которое передается из обработчика завершения системы в главный поток и больше нигде не используется.
private struct Handoff<Value>: @unchecked Sendable {
    let value: Value
}

/// Ровно один ответ, даже если отмена, срок и callback пришли одновременно.
private final class ProviderReply: @unchecked Sendable {
    private let lock = NSLock()
    private var continuation: CheckedContinuation<Data?, any Error>?
    private var result: Result<Data?, any Error>?

    func install(_ continuation: CheckedContinuation<Data?, any Error>) -> Bool {
        lock.lock()
        if let result {
            lock.unlock()
            continuation.resume(with: result)
            return false
        }
        self.continuation = continuation
        lock.unlock()
        return true
    }

    func finish(_ result: Result<Data?, any Error>) {
        lock.lock()
        guard self.result == nil else { lock.unlock(); return }
        self.result = result
        let continuation = self.continuation
        self.continuation = nil
        lock.unlock()
        continuation?.resume(with: result)
    }
}

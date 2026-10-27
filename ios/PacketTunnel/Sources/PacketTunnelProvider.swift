import Foundation
import LibXray
import Network
import NetworkExtension
import os
import PrivateGatewayCore
import PrivateGatewayTheme

/// Расширение туннеля (Packet Tunnel) — то же, что `GatewayVpnService` Android: выбранный сервер и настройки — из общей папки
/// App Group и Связки ключей (`SharedContainer`), маршруты и DNS — по плану `TunPlan`, ядро Xray (libXray) читает и пишет
/// пакеты интерфейса туннеля сам: дескриптор utun уходит ему в конфиг (`xray.tun.fd`). Состояние и код ошибки — в общую папку
/// для приложения. Свои соединения расширения iOS в туннель не отправляет — ядро ходит к серверу мимо него.
///
/// Пока защита включена — наблюдение за связью (`TunnelGuard`): проба через туннель раз в минуту, обрыв — переподключение к
/// тому же серверу по правилу из настроек, iOS показывает «Переподключение» (`reasserting`). Сдались: с блоком при обрыве
/// туннель остается без ядра (трафик заблокирован, «Ошибка»), без него — закрывается. Смена сети и пробуждение — проба сразу.
/// Уведомления о смене состояния — по правилам доски 22 (`NoticeDesk`).
///
/// Методы вызывают разные потоки системы и задача наблюдения; общее состояние — под замком, действия с ядром — по одному
/// (`SerialWork`), поэтому класс помечен `@unchecked Sendable`.
final class PacketTunnelProvider: NEPacketTunnelProvider, @unchecked Sendable {
    private let container = SharedContainer.current()
    private let log = Logger(subsystem: "com.privategateway.client.tunnel", category: "tunnel")
    private let timer = SystemGuardTimer()
    private let work = SerialWork()
    private let lock = NSLock()
    private var guardTask: Task<Void, Never>?
    private var path: NWPathMonitor?
    private var reported = TunnelStatus()
    private var serverName: String?
    private let desk = NoticeDesk()

    // В изоляции вызывающего, как метод суперкласса: словарь параметров (NSObject — не Sendable) никуда не пересылается.
    override nonisolated(nonsending) func startTunnel(options: [String: NSObject]?) async throws {
        do {
            try await work.run { try await self.bringUp(reporting: true) }
        } catch {
            throw failed(error, "start")
        }

        watch()
    }

    /// Сообщение приложения: «перечитать сервер и настройки» — другой сервер под защитой, новые настройки или повтор после
    /// «Ошибки». Туннель не опускается: новые маршруты ставятся поверх, прежнее ядро останавливается, новое запускается —
    /// трафик ждет в туннеле. Не вышло — туннель закрывается с ошибкой: держать его без ядра незачем.
    override func handleAppMessage(_ messageData: Data) async -> Data? {
        guard messageData == TunnelMessages.reload else {
            return nil
        }

        do {
            try await work.run { try await self.bringUp(reporting: true) }
            reasserting = false
            watch()
            return TunnelMessages.ok
        } catch {
            let failure = failed(error, "reload")
            cancelTunnelWithError(failure)
            return Data(failure.code.utf8)
        }
    }

    override func stopTunnel(with reason: NEProviderStopReason) async {
        stopWatching()
        await work.run { self.stopCore() }
        report(TunnelStatus())
    }

    /// iPhone проснулся — проба связи сразу, а не через минуту.
    override func wake() {
        timer.nudge()
    }

    /// Сервер и настройки из общей папки, маршруты и DNS, ядро на дескрипторе utun. Ядро запускается после маршрутов: до него
    /// пакеты ждут в туннеле, а не уходят мимо; прежнее ядро (перезапуск) останавливается тоже после новых маршрутов.
    /// `reporting` — сообщать «Подключение» и «Защищено»; при переподключении состояние ведет наблюдение.
    private nonisolated(nonsending) func bringUp(reporting: Bool) async throws {
        let launch = try await TunnelLaunch.prepare(settings: container.settings, servers: container.servers)
        let name = try? await container.servers.servers().first { $0.id == launch.serverId }?.displayName
        locked { serverName = name }
        if reporting {
            report(TunnelStatus(state: .connecting, serverId: launch.serverId))
        }

        try await apply(launch.plan.networkSettings())
        guard let fd = TunnelInterface.descriptor() else {
            throw TunnelFailure(TunnelErrors.coreFailed, detail: "utun descriptor not found")
        }

        stopCore()
        try TunnelLaunch.started(LibXrayInvoke(XrayConfig.runRequest(launch.config(tunFd: fd))))
        if reporting {
            report(TunnelStatus(state: .connected, serverId: launch.serverId, since: Date()))
        }

        reportCoreVersion()
    }

    // MARK: Наблюдение

    /// Наблюдение за связью заново (после подъема или перезапуска) и подгон на смену сети.
    private func watch() {
        let guarded = TunnelGuard(
            timer: timer,
            policy: { [container] in (try? await container.settings.load().reconnect) ?? .backoff60Seconds },
            probe: { [weak self] in await self?.probe() ?? false },
            restart: { [weak self] attempt in await self?.restart(attempt) },
            events: GuardReport(provider: self)
        )
        let task = Task { await guarded.run() }
        let monitor = NWPathMonitor()
        monitor.pathUpdateHandler = { [timer] _ in timer.nudge() }
        monitor.start(queue: DispatchQueue(label: "com.privategateway.client.tunnel.path"))
        let previous = locked { () -> (Task<Void, Never>?, NWPathMonitor?) in
            defer {
                guardTask = task
                path = monitor
            }
            return (guardTask, path)
        }
        previous.0?.cancel()
        previous.1?.cancel()
    }

    private func stopWatching() {
        let previous = locked { () -> (Task<Void, Never>?, NWPathMonitor?) in
            defer {
                guardTask = nil
                path = nil
            }
            return (guardTask, path)
        }
        previous.0?.cancel()
        previous.1?.cancel()
    }

    /// Проба связи через туннель — как `TunnelProbe` Android: TLS к проверочному адресу через сам туннель (свои соединения
    /// расширения iOS иначе пускает мимо). Рукопожатие TLS доходит до сервера сайта только через ядро и сервер защиты:
    /// удалось за 10 секунд — связь есть.
    private func probe() async -> Bool {
        let connection = createTCPConnectionThroughTunnel(
            to: NWHostEndpoint(hostname: TunnelProbe.host, port: String(TunnelProbe.port)), enableTLS: true, tlsParameters: nil, delegate: nil
        )
        defer { connection.cancel() }
        let deadline = timer.now() + ReconnectTiming().probeTimeout
        while timer.now() < deadline {
            switch connection.state {
            case .connected:
                return true
            case .disconnected, .cancelled, .invalid:
                return false
            default:
                try? await Task.sleep(nanoseconds: 100_000_000)
            }
        }

        return false
    }

    /// Попытка переподключения: новые маршруты и ядро к тому же серверу; nil — удалось, иначе код ошибки.
    private func restart(_ attempt: Int) async -> String? {
        do {
            try await work.run { try await self.bringUp(reporting: false) }
            return nil
        } catch {
            let failure = error as? TunnelFailure ?? TunnelFailure(TunnelErrors.coreFailed, detail: "\(error)")
            log.error("reconnect \(attempt, privacy: .public): \(failure.code, privacy: .public) \(failure.detail ?? "", privacy: .public)")
            return failure.code
        }
    }

    /// События наблюдения: «Переподключение» с номером попытки (iOS показывает его сама — `reasserting`), снова «Защищено»,
    /// «Ошибка». Сдались с блоком при обрыве — туннель остается без ядра: трафик заблокирован, пока пользователь не повторит
    /// или не выберет другой сервер; без блока — туннель закрывается, трафик идет напрямую.
    fileprivate func reconnecting(_ attempt: Int) {
        reasserting = true
        report(TunnelStatus(state: .reconnecting, serverId: currentServer, attempt: attempt))
    }

    fileprivate func protectedAgain() {
        reasserting = false
        report(TunnelStatus(state: .connected, serverId: currentServer, since: Date()))
    }

    fileprivate func gaveUp(_ error: String) async {
        await work.run { self.stopCore() }
        reasserting = false
        report(TunnelStatus(state: .error, serverId: currentServer, error: error))
        let killSwitch = (try? await container.settings.load().killSwitch) ?? true
        if !killSwitch {
            cancelTunnelWithError(TunnelFailure(error))
        }
    }

    private var currentServer: String? { locked { reported.serverId } }

    // MARK: Ядро, состояние, уведомления

    /// Отказ: в журнал — код и подробность без секретов (SecretMasker), ядро — остановить, приложению — «Ошибка» с кодом.
    private func failed(_ error: any Error, _ step: String) -> TunnelFailure {
        let failure = error as? TunnelFailure ?? TunnelFailure(TunnelErrors.coreFailed, detail: "\(error)")
        log.error("\(step, privacy: .public): \(failure.code, privacy: .public) \(failure.detail ?? "", privacy: .public)")
        stopWatching()
        stopCore()
        report(TunnelStatus(state: .error, error: failure.code))
        return failure
    }

    private func stopCore() {
        let response = XrayResponse.parse(LibXrayInvoke(XrayConfig.invoke("stopXray")))
        if !response.success {
            log.error("stop: \(SecretMasker.mask(response.error), privacy: .public)")
        }
    }

    /// Состояние для приложения и уведомление о смене; не записалось — приложение покажет состояние системы, туннель от этого
    /// не зависит.
    private func report(_ status: TunnelStatus) {
        do {
            try container.status.write(status)
        } catch {
            log.error("status: \(SecretMasker.mask("\(error)"), privacy: .public)")
        }

        let (before, name) = locked { () -> (TunnelStatus, String?) in
            defer { reported = status }
            return (reported, serverName)
        }
        let settings = try? container.settings.loadNow()
        guard let notice = NotificationPolicy.forChange(before, status, server: name, killSwitch: settings?.killSwitch ?? true) else {
            return
        }

        let delivery = locked { desk.deliver(notice, now: timer.now(), windowVisible: false, level: settings?.notifications ?? .default) }
        if delivery.withdrawProtection {
            Notifications.withdrawProtection()
        }

        if let show = delivery.show {
            Notifications.post(show, PgTexts.shared(container))
        }
    }

    /// Версия ядра для «О программе»: в процессе приложения ядра нет. Не записалась — там будет «—».
    private func reportCoreVersion() {
        guard let version = XrayResponse.version(LibXrayInvoke(XrayConfig.invoke("xrayVersion"))) else {
            return
        }

        do {
            try container.coreVersion.write(version)
        } catch {
            log.error("core version: \(SecretMasker.mask("\(error)"), privacy: .public)")
        }
    }

    /// Маршруты и DNS туннеля. Через обработчик завершения, а не async-вариант: так провайдер и настройки (не Sendable) не
    /// покидают изоляцию вызывающего.
    private nonisolated(nonsending) func apply(_ settings: NEPacketTunnelNetworkSettings) async throws {
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, any Error>) in
            setTunnelNetworkSettings(settings) { @Sendable error in
                if let error {
                    continuation.resume(throwing: TunnelFailure(TunnelErrors.coreFailed, detail: "network settings: \(error)"))
                } else {
                    continuation.resume()
                }
            }
        }
    }

    private func locked<T>(_ body: () -> T) -> T {
        lock.lock()
        defer { lock.unlock() }
        return body()
    }
}

/// События наблюдения — расширению; слабая ссылка: закрытый туннель задачу наблюдения не держит.
private struct GuardReport: GuardEvents {
    weak var provider: PacketTunnelProvider?

    func reconnecting(_ attempt: Int) async {
        provider?.reconnecting(attempt)
    }

    func protectedAgain() async {
        provider?.protectedAgain()
    }

    func gaveUp(_ error: String) async {
        await provider?.gaveUp(error)
    }
}

/// Одно действие с ядром за раз: подъем, перезапуск и остановка приходят из разных потоков — от системы, от приложения и от
/// наблюдения; без очереди два ядра запустились бы на одном дескрипторе.
final class SerialWork: @unchecked Sendable {
    private let lock = NSLock()
    private var busy = false
    private var waiting: [CheckedContinuation<Void, Never>] = []

    nonisolated(nonsending) func run<T>(_ body: () async throws -> T) async rethrows -> T {
        await acquire()
        defer { release() }
        return try await body()
    }

    private func acquire() async {
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            lock.lock()
            if busy {
                waiting.append(continuation)
                lock.unlock()
            } else {
                busy = true
                lock.unlock()
                continuation.resume()
            }
        }
    }

    private func release() {
        lock.lock()
        if waiting.isEmpty {
            busy = false
            lock.unlock()
        } else {
            let next = waiting.removeFirst()
            lock.unlock()
            next.resume()
        }
    }
}

extension TunPlan {
    /// План туннеля в виде настроек iOS: адреса, маршруты, DNS для всех имен и MTU.
    func networkSettings() -> NEPacketTunnelNetworkSettings {
        let settings = NEPacketTunnelNetworkSettings(tunnelRemoteAddress: TunPlan.remoteAddress)
        let v4 = NEIPv4Settings(addresses: [ipv4.address], subnetMasks: [TunPlan.subnetMask(ipv4.length)])
        v4.includedRoutes = routes.filter { !$0.address.contains(":") }.map {
            NEIPv4Route(destinationAddress: $0.address, subnetMask: TunPlan.subnetMask($0.length))
        }
        settings.ipv4Settings = v4
        let v6 = NEIPv6Settings(addresses: [ipv6.address], networkPrefixLengths: [NSNumber(value: ipv6.length)])
        v6.includedRoutes = routes.filter { $0.address.contains(":") }.map {
            NEIPv6Route(destinationAddress: $0.address, networkPrefixLength: NSNumber(value: $0.length))
        }
        settings.ipv6Settings = v6
        let dns = NEDNSSettings(servers: dnsServers)
        dns.matchDomains = dnsMatchDomains
        settings.dnsSettings = dns
        settings.mtu = NSNumber(value: mtu)
        return settings
    }
}

/// Дескриптор интерфейса туннеля (utun) в процессе расширения: NetworkExtension его не отдает, а ядру он нужен, чтобы
/// читать и писать пакеты самому. Ищется среди открытых дескрипторов — тот, у которого есть имя интерфейса utun (так же
/// делают WireGuard и Xray на iOS).
enum TunnelInterface {
    /// Параметры getsockopt из <sys/kern_control.h> и <net/if_utun.h>: SYSPROTO_CONTROL и UTUN_OPT_IFNAME.
    private static let sysprotoControl: Int32 = 2
    private static let utunOptIfname: Int32 = 2

    static func descriptor() -> Int32? {
        var name = [UInt8](repeating: 0, count: 16) // IFNAMSIZ
        for fd: Int32 in 0...1024 {
            var length = socklen_t(name.count)
            guard getsockopt(fd, sysprotoControl, utunOptIfname, &name, &length) == 0 else {
                continue
            }

            if String(decoding: name.prefix { $0 != 0 }, as: UTF8.self).hasPrefix("utun") {
                return fd
            }
        }

        return nil
    }
}

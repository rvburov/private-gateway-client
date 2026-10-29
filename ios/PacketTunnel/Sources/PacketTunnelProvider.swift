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
    private var probeNote: String?
    private let desk = NoticeDesk()
    private var generation: UInt64 = 0
    private var stopping = false
    private var subscriptionProxy: SubscriptionProxy?

    // В изоляции вызывающего, как метод суперкласса: словарь параметров (NSObject — не Sendable) никуда не пересылается.
    override nonisolated(nonsending) func startTunnel(options: [String: NSObject]?) async throws {
        var session: UInt64?
        var failure: TunnelFailure?
        do {
            try await work.run({
                let active = try self.beginSession(starting: true)
                session = active
                try await self.bringUp(reporting: true, session: active)
                self.watch(session: active)
            }, onFailure: { error in
                // Cleanup остается в той же очереди: ошибка прежнего запуска не остановит следующее ядро.
                if let session { failure = self.failed(error, "start", session: session) }
            })
        } catch {
            if let failure { throw failure }
            throw error
        }
    }

    /// Сообщение приложения: «перечитать сервер и настройки» — другой сервер под защитой, новые настройки или повтор после
    /// «Ошибки». Туннель не опускается: новые маршруты ставятся поверх, прежнее ядро останавливается, новое запускается —
    /// трафик ждет в туннеле. Не вышло — туннель закрывается с ошибкой: держать его без ядра незачем.
    override func handleAppMessage(_ messageData: Data) async -> Data? {
        if messageData == TunnelMessages.subscriptionProxy {
            return await work.run {
                guard let proxy = self.locked({ self.subscriptionProxy }), self.locked({ !self.stopping }),
                      self.locked({ self.reported.state == .connected }) else { return nil }
                return try? JSONEncoder().encode(proxy)
            }
        }
        guard messageData == TunnelMessages.reload else {
            return nil
        }

        container.log.add("reload")
        return await work.run {
            do {
                let session = try self.beginSession()
                do {
                    try await self.bringUp(reporting: true, session: session)
                    self.reasserting = false
                    self.watch(session: session)
                    return TunnelMessages.ok
                } catch {
                    let failure = self.failed(error, "reload", session: session)
                    if self.current(session) { self.cancelTunnelWithError(failure) }
                    return Data(failure.code.utf8)
                }
            } catch {
                return Data(TunnelErrors.coreFailed.utf8)
            }
        }
    }

    override func stopTunnel(with reason: NEProviderStopReason) async {
        container.log.add("stopped, reason \(reason.rawValue)")
        locked {
            stopping = true
            generation &+= 1
            subscriptionProxy = nil
        }
        stopWatching()
        await work.run {
            self.stopWatching()
            self.stopCore()
            self.report(TunnelStatus())
        }
    }

    /// iPhone проснулся — проба связи сразу, а не через минуту.
    override func wake() {
        timer.nudge()
    }

    /// Сервер и настройки из общей папки, маршруты и DNS, ядро на дескрипторе utun. Ядро запускается после маршрутов: до него
    /// пакеты ждут в туннеле, а не уходят мимо; прежнее ядро (перезапуск) останавливается тоже после новых маршрутов.
    /// `reporting` — сообщать «Подключение» и «Защищено»; при переподключении состояние ведет наблюдение.
    private nonisolated(nonsending) func bringUp(reporting: Bool, session: UInt64) async throws {
        let launch = try await TunnelLaunch.prepare(settings: container.settings, servers: container.servers)
        try requireCurrent(session)
        let name = try? await container.servers.servers().first { $0.id == launch.serverId }?.displayName
        try requireCurrent(session)
        locked { serverName = name }
        if reporting {
            report(TunnelStatus(state: .connecting, serverId: launch.serverId))
        }

        try await apply(launch.plan.networkSettings())
        try requireCurrent(session)
        guard let fd = TunnelInterface.descriptor() else {
            throw TunnelFailure(TunnelErrors.coreFailed, detail: "utun descriptor not found")
        }

        stopCore()
        let proxy = launch.protectedSubscriptionsAllowed ? try TunnelInterface.proxy() : nil
        try TunnelLaunch.started(LibXrayInvoke(XrayConfig.runRequest(launch.config(tunFd: fd, subscriptionProxy: proxy))))
        try requireCurrent(session)
        locked { subscriptionProxy = proxy }
        if reporting {
            report(TunnelStatus(state: .connected, serverId: launch.serverId, since: Date()))
            container.log.add("connected")
        }

        reportCoreVersion()
    }

    // MARK: Наблюдение

    /// Наблюдение за связью заново (после подъема или перезапуска) и подгон на смену сети.
    private func watch(session: UInt64) {
        guard current(session) else { return }
        let guarded = TunnelGuard(
            timer: timer,
            policy: { [container] in (try? await container.settings.load().reconnect) ?? .backoff60Seconds },
            probe: { [weak self] in await self?.probe() ?? false },
            restart: { [weak self] attempt in await self?.restart(attempt, session: session) },
            events: GuardReport(provider: self, session: session)
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

    /// Проба связи через туннель — как `TunnelProbe` Android: запрос к проверочному адресу через сам туннель (сокет привязан
    /// к интерфейсу utun — свои соединения расширения iOS иначе пускает мимо), ответ доходит только через ядро и сервер защиты.
    /// Пробу не удалось даже начать (нет интерфейса, система не дала привязать сокет) — это не обрыв: наблюдение считает
    /// связь живой, причина — в журнал туннеля, один раз.
    private func probe() async -> Bool {
        let timeout = ReconnectTiming().probeTimeout
        let answer = await withCheckedContinuation { (continuation: CheckedContinuation<ProbeAnswer, Never>) in
            DispatchQueue.global(qos: .utility).async {
                continuation.resume(returning: TunnelInterface.name().map { SocketProbe.run(interface: $0, timeout: timeout) } ?? .unavailable("no utun"))
            }
        }

        switch answer {
        case .answered:
            return true
        case .silent:
            return false
        case let .unavailable(reason):
            let first = locked { () -> Bool in
                defer { probeNote = reason }
                return probeNote != reason
            }
            if first {
                log.error("probe unavailable: \(reason, privacy: .public)")
                container.log.add("probe unavailable: \(reason)")
            }
            return true
        }
    }

    /// Попытка переподключения: новые маршруты и ядро к тому же серверу; nil — удалось, иначе код ошибки.
    private func restart(_ attempt: Int, session: UInt64) async -> String? {
        return await work.run {
            guard self.current(session), !Task.isCancelled else { return TunnelErrors.coreFailed }
            do {
                try await self.bringUp(reporting: false, session: session)
                return nil
            } catch {
                self.stopCore()
                guard self.current(session) else { return TunnelErrors.coreFailed }
                let failure = error as? TunnelFailure ?? TunnelFailure(TunnelErrors.coreFailed, detail: "\(error)")
                self.log.error("reconnect \(attempt, privacy: .public): \(failure.code, privacy: .public) \(failure.detail ?? "", privacy: .public)")
                self.container.log.add("reconnect \(attempt): \(failure.code) \(failure.detail ?? "")")
                return failure.code
            }
        }
    }

    /// События наблюдения: «Переподключение» с номером попытки (iOS показывает его сама — `reasserting`), снова «Защищено»,
    /// «Ошибка». Сдались с блоком при обрыве — туннель остается без ядра: трафик заблокирован, пока пользователь не повторит
    /// или не выберет другой сервер; без блока — туннель закрывается, трафик идет напрямую.
    fileprivate func reconnecting(_ attempt: Int, session: UInt64) async {
        await work.run {
            guard self.current(session), !Task.isCancelled else { return }
            self.markReconnecting(attempt)
        }
    }

    private func markReconnecting(_ attempt: Int) {
        container.log.add("connection lost, attempt \(attempt)")
        reasserting = true
        report(TunnelStatus(state: .reconnecting, serverId: currentServer, attempt: attempt))
    }

    fileprivate func protectedAgain(session: UInt64) async {
        await work.run {
            guard self.current(session), !Task.isCancelled else { return }
            self.markProtectedAgain()
        }
    }

    private func markProtectedAgain() {
        container.log.add("connection restored")
        reasserting = false
        report(TunnelStatus(state: .connected, serverId: currentServer, since: Date()))
    }

    fileprivate func gaveUp(_ error: String, session: UInt64) async {
        await work.run {
            guard self.current(session), !Task.isCancelled else { return }
            self.container.log.add("gave up: \(error)")
            self.stopCore()
            self.reasserting = false
            self.report(TunnelStatus(state: .error, serverId: self.currentServer, error: error))
            let killSwitch = (try? await self.container.settings.load().killSwitch) ?? true
            if !killSwitch, self.current(session) {
                self.cancelTunnelWithError(TunnelFailure(error))
            }
        }
    }

    private var currentServer: String? { locked { reported.serverId } }

    // MARK: Ядро, состояние, уведомления

    /// Отказ: в журнал — код и подробность без секретов (SecretMasker), ядро — остановить, приложению — «Ошибка» с кодом.
    private func failed(_ error: any Error, _ step: String, session: UInt64) -> TunnelFailure {
        let failure = error as? TunnelFailure ?? TunnelFailure(TunnelErrors.coreFailed, detail: "\(error)")
        stopCore()
        guard current(session) else { return failure }
        log.error("\(step, privacy: .public): \(failure.code, privacy: .public) \(failure.detail ?? "", privacy: .public)")
        container.log.add("\(step): \(failure.code) \(failure.detail ?? "")")
        stopWatching()
        report(TunnelStatus(state: .error, error: failure.code))
        return failure
    }

    private func stopCore() {
        locked { subscriptionProxy = nil }
        let response = XrayResponse.parse(LibXrayInvoke(XrayConfig.invoke("stopXray")))
        if !response.success {
            log.error("stop: \(SecretMasker.mask(response.error), privacy: .public)")
            container.log.add("core stop: \(response.error ?? "")")
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

    /// Вызывается в очереди ядра. Старый watcher может еще завершать пробу, но его callbacks с прежним поколением уже
    /// ничего не меняют. Stop инвалидирует поколение сразу, в том числе пока setTunnelNetworkSettings еще отвечает.
    private func beginSession(starting: Bool = false) throws -> UInt64 {
        let session = locked { () -> UInt64? in
            guard starting || !stopping else { return nil }
            stopping = false
            generation &+= 1
            return generation
        }
        guard let session else { throw CancellationError() }
        stopWatching()
        return session
    }

    private func current(_ session: UInt64) -> Bool {
        locked { !stopping && generation == session }
    }

    private func requireCurrent(_ session: UInt64) throws {
        try Task.checkCancellation()
        guard current(session) else { throw CancellationError() }
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
    let session: UInt64

    func reconnecting(_ attempt: Int) async {
        await provider?.reconnecting(attempt, session: session)
    }

    func protectedAgain() async {
        await provider?.protectedAgain(session: session)
    }

    func gaveUp(_ error: String) async {
        await provider?.gaveUp(error, session: session)
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

    /// Случайный свободный порт петли и учетные данные на один запуск ядра. Если порт заняли между проверкой и запуском,
    /// Xray откажет в запуске целиком; входа для защищенной загрузки тогда приложению не выдаем.
    static func proxy() throws -> SubscriptionProxy {
        let fd = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP)
        guard fd >= 0 else { throw TunnelFailure(TunnelErrors.coreFailed, detail: "proxy socket") }
        defer { close(fd) }
        var address = sockaddr_in()
        address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        address.sin_family = sa_family_t(AF_INET)
        address.sin_addr.s_addr = inet_addr(SubscriptionProxy.host)
        let bound = withUnsafePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { bind(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) }
        }
        var length = socklen_t(MemoryLayout<sockaddr_in>.size)
        let named = withUnsafeMutablePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { getsockname(fd, $0, &length) }
        }
        guard bound == 0, named == 0 else { throw TunnelFailure(TunnelErrors.coreFailed, detail: "proxy port") }
        return SubscriptionProxy(port: Int(UInt16(bigEndian: address.sin_port)), user: UUID().uuidString, password: UUID().uuidString)
    }

    static func descriptor() -> Int32? {
        find()?.fd
    }

    /// Имя интерфейса туннеля (utunN) — к нему привязывается сокет пробы.
    static func name() -> String? {
        find()?.name
    }

    private static func find() -> (fd: Int32, name: String)? {
        var name = [UInt8](repeating: 0, count: 16) // IFNAMSIZ
        for fd: Int32 in 0...1024 {
            var length = socklen_t(name.count)
            guard getsockopt(fd, sysprotoControl, utunOptIfname, &name, &length) == 0 else {
                continue
            }

            let text = String(decoding: name.prefix { $0 != 0 }, as: UTF8.self)
            if text.hasPrefix("utun") {
                return (fd, text)
            }
        }

        return nil
    }
}

/// Итог пробы: ответил сервер сайта, промолчал (обрыв, если дважды подряд) или пробу не удалось начать.
enum ProbeAnswer: Sendable {
    case answered
    case silent
    case unavailable(String)
}

/// Проба на сокетах BSD: TCP к `TunnelProbe.host` через интерфейс туннеля (IP_BOUND_IF), запрос HEAD, ждать строку
/// состояния HTTP не дольше `timeout`. Соединение с интерфейсом туннеля принимает само ядро, поэтому молчание после него —
/// нет связи дальше ядра. Отказ системы сразу (нет прав, нет маршрута) — пробу начать не удалось. Блокирует поток.
enum SocketProbe {
    /// Параметр setsockopt из <netinet/in.h>: IP_BOUND_IF — привязать сокет к интерфейсу по номеру.
    private static let ipBoundIf: Int32 = 25

    static func run(interface: String, timeout: Int) -> ProbeAnswer {
        let index = if_nametoindex(interface)
        guard index != 0 else {
            return .unavailable("no interface \(interface)")
        }

        let fd = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP)
        guard fd >= 0 else {
            return .unavailable("socket errno \(errno)")
        }

        defer { close(fd) }
        var bound = Int32(bitPattern: index)
        guard setsockopt(fd, IPPROTO_IP, ipBoundIf, &bound, socklen_t(MemoryLayout<Int32>.size)) == 0 else {
            return .unavailable("bind errno \(errno)")
        }

        var on: Int32 = 1
        _ = setsockopt(fd, SOL_SOCKET, SO_NOSIGPIPE, &on, socklen_t(MemoryLayout<Int32>.size))
        _ = fcntl(fd, F_SETFL, fcntl(fd, F_GETFL) | O_NONBLOCK)

        var address = sockaddr_in()
        address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        address.sin_family = sa_family_t(AF_INET)
        address.sin_port = in_port_t(TunnelProbe.port).bigEndian
        guard inet_pton(AF_INET, TunnelProbe.host, &address.sin_addr) == 1 else {
            return .unavailable("address")
        }

        let deadline = DispatchTime.now().uptimeNanoseconds + UInt64(timeout) * 1_000_000
        let started = withUnsafePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { connect(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) }
        }
        if started != 0 {
            let code = errno
            guard code == EINPROGRESS else {
                return refused(code)
            }
        }

        guard ready(fd, Int16(POLLOUT), deadline) else {
            return .silent
        }

        var failure: Int32 = 0
        var length = socklen_t(MemoryLayout<Int32>.size)
        _ = getsockopt(fd, SOL_SOCKET, SO_ERROR, &failure, &length)
        if failure != 0 {
            return refused(failure)
        }

        let request = Array(TunnelProbe.request.utf8)
        let sent = request.withUnsafeBytes { send(fd, $0.baseAddress, $0.count, 0) }
        guard sent == request.count else {
            return .silent
        }

        var head: [UInt8] = []
        var buffer = [UInt8](repeating: 0, count: 256)
        while head.count < TunnelProbe.headLength, ready(fd, Int16(POLLIN), deadline) {
            let count = buffer.withUnsafeMutableBytes { recv(fd, $0.baseAddress, $0.count, 0) }
            guard count > 0 else {
                break
            }

            head += buffer[0..<count]
        }

        return TunnelProbe.answered(head) ? .answered : .silent
    }

    /// Отказ при соединении: запрет и «нет маршрута» дает система (сокет не пустили в туннель) — пробу начать не удалось;
    /// сброс и прочее — ответ ядра: дальше него связи нет.
    private static func refused(_ code: Int32) -> ProbeAnswer {
        [EPERM, EACCES, ENETUNREACH, EHOSTUNREACH, ENETDOWN, EADDRNOTAVAIL].contains(code) ? .unavailable("connect errno \(code)") : .silent
    }

    /// Ждать готовности сокета до срока (закрытие и ошибка — тоже готовность: их покажут SO_ERROR и recv); false — срок вышел.
    private static func ready(_ fd: Int32, _ events: Int16, _ deadline: UInt64) -> Bool {
        while true {
            let now = DispatchTime.now().uptimeNanoseconds
            guard now < deadline else {
                return false
            }

            var descriptor = pollfd(fd: fd, events: events, revents: 0)
            let count = poll(&descriptor, 1, Int32(min((deadline - now) / 1_000_000 + 1, 1000)))
            if count > 0 {
                return descriptor.revents & (events | Int16(POLLHUP) | Int16(POLLERR)) != 0
            }

            if count < 0, errno != EINTR {
                return false
            }
        }
    }
}

import Foundation
import LibXray
import NetworkExtension
import os
import PrivateGatewayCore

/// Расширение туннеля (Packet Tunnel) — то же, что `GatewayVpnService` Android: выбранный сервер и настройки — из общей папки
/// App Group и Связки ключей (`SharedContainer`), маршруты и DNS — по плану `TunPlan`, ядро Xray (libXray) читает и пишет
/// пакеты интерфейса туннеля сам: дескриптор utun уходит ему в конфиг (`xray.tun.fd`). Состояние и код ошибки — в общую папку
/// для приложения. Свои соединения расширения iOS в туннель не отправляет — ядро ходит к серверу мимо него.
final class PacketTunnelProvider: NEPacketTunnelProvider {
    private let container = SharedContainer.current()
    private let log = Logger(subsystem: "com.privategateway.client.tunnel", category: "tunnel")

    // В изоляции вызывающего, как метод суперкласса: словарь параметров (NSObject — не Sendable) никуда не пересылается.
    override nonisolated(nonsending) func startTunnel(options: [String: NSObject]?) async throws {
        do {
            try await bringUp()
        } catch {
            throw failed(error, "start")
        }
    }

    /// Сообщение приложения: «перечитать сервер и настройки» — другой сервер под защитой или новые настройки. Туннель не
    /// опускается: новые маршруты ставятся поверх, прежнее ядро останавливается, новое запускается — трафик ждет в туннеле.
    /// Не вышло — туннель закрывается с ошибкой: держать его без ядра незачем.
    override func handleAppMessage(_ messageData: Data) async -> Data? {
        guard messageData == TunnelMessages.reload else {
            return nil
        }

        do {
            try await bringUp()
            return TunnelMessages.ok
        } catch {
            let failure = failed(error, "reload")
            cancelTunnelWithError(failure)
            return Data(failure.code.utf8)
        }
    }

    override func stopTunnel(with reason: NEProviderStopReason) async {
        stopCore()
        report(TunnelStatus())
    }

    /// Сервер и настройки из общей папки, маршруты и DNS, ядро на дескрипторе utun. Ядро запускается после маршрутов: до него
    /// пакеты ждут в туннеле, а не уходят мимо; прежнее ядро (перезапуск) останавливается тоже после новых маршрутов.
    private nonisolated(nonsending) func bringUp() async throws {
        let launch = try await TunnelLaunch.prepare(settings: container.settings, servers: container.servers)
        report(TunnelStatus(state: .connecting, serverId: launch.serverId))
        try await apply(launch.plan.networkSettings())
        guard let fd = TunnelInterface.descriptor() else {
            throw TunnelFailure(TunnelErrors.coreFailed, detail: "utun descriptor not found")
        }

        stopCore()
        try TunnelLaunch.started(LibXrayInvoke(XrayConfig.runRequest(launch.config(tunFd: fd))))
        report(TunnelStatus(state: .connected, serverId: launch.serverId, since: Date()))
        reportCoreVersion()
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

    /// Отказ: в журнал — код и подробность без секретов (SecretMasker), ядро — остановить, приложению — «Ошибка» с кодом.
    private func failed(_ error: any Error, _ step: String) -> TunnelFailure {
        let failure = error as? TunnelFailure ?? TunnelFailure(TunnelErrors.coreFailed, detail: "\(error)")
        log.error("\(step, privacy: .public): \(failure.code, privacy: .public) \(failure.detail ?? "", privacy: .public)")
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

    /// Состояние для приложения; не записалось — приложение покажет состояние системы, туннель от этого не зависит.
    private func report(_ status: TunnelStatus) {
        do {
            try container.status.write(status)
        } catch {
            log.error("status: \(SecretMasker.mask("\(error)"), privacy: .public)")
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

import Darwin
import Foundation
import PrivateGatewayCore

/// Счетчики байт интерфейса туннеля в момент замера: принято (загрузка), отправлено (отдача) и монотонное время в секундах.
/// Счетчики интерфейса 32-битные — переходят через ноль.
struct TrafficSample: Equatable {
    var received: UInt32
    var sent: UInt32
    var at: TimeInterval
}

/// Скорость туннеля в Мбит/с — как `TrafficStats` десктопа, где служба считает ее по адаптеру туннеля.
struct TunnelSpeed: Equatable {
    var down: Double
    var up: Double

    /// Скорость между двумя замерами: разница счетчиков по модулю 2³² (переход через ноль — не отрицательная скорость), за
    /// время между замерами. Замеры ближе 0,2 с — скорости нет: время слишком мало для честного деления.
    static func between(_ earlier: TrafficSample, _ later: TrafficSample) -> TunnelSpeed? {
        let seconds = later.at - earlier.at
        guard seconds >= 0.2 else {
            return nil
        }

        let down = Double(later.received &- earlier.received)
        let up = Double(later.sent &- earlier.sent)
        return TunnelSpeed(down: down * 8 / seconds / 1_000_000, up: up * 8 / seconds / 1_000_000)
    }
}

/// Счетчики интерфейса туннеля из таблицы интерфейсов системы (`getifaddrs`): свой utun узнается по адресу IPv4 туннеля
/// (`XrayConfig.tunAddress`), счетчики — у его записи AF_LINK. Только чтение; ничего не меняет и в сеть не ходит.
enum TunnelCounters {
    static func sample(address: String = XrayConfig.tunAddress) -> TrafficSample? {
        guard let counters = read(address: address) else {
            return nil
        }

        return TrafficSample(received: counters.received, sent: counters.sent, at: ProcessInfo.processInfo.systemUptime)
    }

    /// Принято и отправлено интерфейсом с адресом `address`; такого интерфейса нет — nil.
    static func read(address: String) -> (received: UInt32, sent: UInt32)? {
        var list: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&list) == 0, let first = list else {
            return nil
        }

        defer { freeifaddrs(first) }
        let wanted = inet_addr(address)
        var tunnel: String?
        var counters: [String: (received: UInt32, sent: UInt32)] = [:]
        var cursor: UnsafeMutablePointer<ifaddrs>? = first
        while let entry = cursor {
            cursor = entry.pointee.ifa_next
            guard let socketAddress = entry.pointee.ifa_addr else {
                continue
            }

            let name = String(cString: entry.pointee.ifa_name)
            let family = Int32(socketAddress.pointee.sa_family)
            if family == AF_INET {
                let ip = socketAddress.withMemoryRebound(to: sockaddr_in.self, capacity: 1) { $0.pointee.sin_addr.s_addr }
                if ip == wanted {
                    tunnel = name
                }
            } else if family == AF_LINK, let data = entry.pointee.ifa_data {
                let stats = data.assumingMemoryBound(to: if_data.self).pointee
                counters[name] = (received: stats.ifi_ibytes, sent: stats.ifi_obytes)
            }
        }

        guard let tunnel else {
            return nil
        }

        return counters[tunnel]
    }
}

/// Скорость туннеля для главной: раз в секунду — счетчики интерфейса туннеля, скорость — между соседними замерами. Пока
/// счетчики ни разу не сдвинулись с нуля, скорости нет: нулевые счетчики живого туннеля значат, что система их не отдает,
/// а «0,0» было бы неправдой.
@MainActor
final class SpeedMeter: ObservableObject {
    @Published private(set) var speed: TunnelSpeed?

    /// Замеры, пока задача не отменена (главная ушла с экрана, защита выключилась); после — скорости нет.
    func run() async {
        var last: TrafficSample?
        var moving = false
        while !Task.isCancelled {
            if let sample = TunnelCounters.sample() {
                moving = moving || sample.received > 0 || sample.sent > 0
                if moving, let last, let next = TunnelSpeed.between(last, sample) {
                    speed = next
                }

                last = sample
            } else {
                last = nil
                speed = nil
            }

            try? await Task.sleep(nanoseconds: 1_000_000_000)
        }

        speed = nil
    }

    func stop() {
        speed = nil
    }
}

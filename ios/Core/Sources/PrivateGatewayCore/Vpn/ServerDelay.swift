import Foundation

/// «Задержка» на «Проверке» (решение 509 десктопа): каждое число — медиана трех замеров. «До сервера» — прямое соединение TCP с
/// сервером мимо туннеля: его меряет расширение туннеля (свои соединения расширения iOS в туннель не пускает), приложение
/// просит его сообщением `TunnelMessages.delay`. Сервер по UDP (Hysteria2, TUIC, WireGuard) так не измерить — числа нет.
public enum ServerDelay {
    /// Сколько замеров на каждое число.
    public static let samples = 3

    /// Медиана: середина по порядку, у четного числа — среднее двух средних (половина — вверх); замеров нет — nil.
    public static func median(_ samples: [Int]) -> Int? {
        guard !samples.isEmpty else {
            return nil
        }

        let sorted = samples.sorted()
        let middle = sorted.count / 2
        if sorted.count % 2 == 1 {
            return sorted[middle]
        }

        return Int((Double(sorted[middle - 1] + sorted[middle]) / 2).rounded(.toNearestOrAwayFromZero))
    }

    /// Меряется ли «До сервера» соединением TCP: ядро ходит к серверу по TCP.
    public static func measures(_ kind: ProxyProtocol) -> Bool {
        switch kind {
        case .vless, .vmess, .trojan, .shadowsocks: return true
        case .hysteria2, .tuic, .wireGuard: return false
        }
    }

    /// Строка журнала после отказа туннеля — соединение TCP с сервером мимо туннеля (`step`; не мерили — nil): сервер ответил
    /// (сколько мс) или молчит. Различает «эта сеть не пускает к серверу» и «сервер принимает соединение, но связи через него
    /// нет» (отчет Android 2026-11-05: мобильная сеть, PGC-NET-001 без причины) — те же строки, что у Android. Порт — да,
    /// адрес — нет.
    public static func verdict(_ kind: ProxyProtocol, port: Int, step: ProbeStep?) -> String {
        guard measures(kind) else {
            return "server tcp not checked  udp protocol"
        }

        switch step?.outcome {
        case .ok?:
            return "server answers on tcp  port=\(port) ms=\(step?.timeMs ?? 0)  profile not accepted or no internet through the server"
        case .notFound?:
            return "server tcp not checked  name not found in this network"
        case nil:
            return "server tcp not checked  no answer from the probe"
        default:
            return "server silent on tcp  port=\(port)  this network does not reach it or the server is off"
        }
    }
}

extension TunnelMessages {
    /// «До сервера» — расширению туннеля: ответ — миллисекунды числом, не измерено — пустой ответ.
    public static let delay = Data("delay".utf8)

    /// Ответ расширения на `delay`.
    public static func delayAnswer(_ ms: Int?) -> Data {
        Data((ms.map { String($0) } ?? "").utf8)
    }

    /// Миллисекунды из ответа расширения; нет ответа, пустой или не число — nil.
    public static func delayMs(_ response: Data?) -> Int? {
        guard let response, let ms = Int(String(decoding: response, as: UTF8.self)), ms > 0 else {
            return nil
        }

        return ms
    }
}

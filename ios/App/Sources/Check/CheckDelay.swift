import Foundation
import PrivateGatewayCore
import PrivateGatewayTheme

/// «Задержка» на «Проверке» (решение 509 десктопа): до сервера, через сервер по уже открытому соединению, новое соединение —
/// медианы трех замеров; не измерено — nil.
struct DelayBreakdown: Equatable {
    var toServer: Int?
    var through: Int?
    var fresh: Int?
    var measuredAt: Date

    /// Сколько кругов до сервера уходит на новое соединение — по замерам, до десятой; не измерено — nil.
    var rounds: Double? {
        guard let one = toServer, one > 0, let fresh else {
            return nil
        }

        return (Double(fresh) / Double(one) * 10).rounded() / 10
    }
}

/// Строка «Задержки»: что измерено, пояснение и число.
struct DelayRow: Equatable {
    let title: String
    let text: String
    let value: String
}

/// Тексты «Задержки» — DelayRows, DelayWhy, DelayUpdated и DelayHint десктопа.
enum DelayLook {
    static func rows(_ delay: DelayBreakdown?, _ texts: PgTexts) -> [DelayRow] {
        [
            DelayRow(title: texts.diagDelayToServer, text: texts.diagDelayToServerText, value: ms(delay?.toServer, texts)),
            DelayRow(title: texts.diagDelayThrough, text: texts.diagDelayThroughText, value: ms(delay?.through, texts)),
            DelayRow(title: texts.diagDelayFresh, text: texts.diagDelayFreshText, value: ms(delay?.fresh, texts)),
        ]
    }

    /// «38 мс»; не измерено — «—».
    static func ms(_ value: Int?, _ texts: PgTexts) -> String {
        value.map { texts.diagDelayMs($0) } ?? "—"
    }

    /// Почему чисел нет: идет замер или нет подключения; иначе пояснения нет.
    static func hint(_ state: TunnelState, measuring: Bool, _ texts: PgTexts) -> String? {
        if measuring {
            return texts.diagDelayMeasuring
        }

        return state == .connected ? nil : texts.diagDelayOff
    }

    /// Откуда число нового соединения: сколько кругов до сервера (по замерам) и сколько длится один круг.
    static func why(_ delay: DelayBreakdown?, server: ServerItem?, _ texts: PgTexts) -> String? {
        guard let delay, let one = delay.toServer, let fresh = delay.fresh, let rounds = delay.rounds else {
            return nil
        }

        let count = max(1, Int(rounds.rounded(.toNearestOrAwayFromZero)))
        let word = texts.text(key: "diag.delay.rounds." + Plural.form(count, texts.language.rawValue)) ?? ""
        return texts.diagDelayWhy(fresh, count, word, handshake(server, texts), one)
    }

    /// Рукопожатие сервера в объяснении: REALITY, TLS или протокола.
    static func handshake(_ server: ServerItem?, _ texts: PgTexts) -> String {
        switch server?.security {
        case .reality?: return "REALITY"
        case .tls?: return "TLS"
        default: return texts.diagDelayHandshakeProtocol
        }
    }

    /// «Обновлено 15:42:12» по времени телефона; не мерили или идет замер — nil.
    static func updated(_ delay: DelayBreakdown?, measuring: Bool, _ texts: PgTexts, timeZone: TimeZone = .current) -> String? {
        guard let delay, !measuring else {
            return nil
        }

        let format = DateFormatter()
        format.locale = Locale(identifier: "en_US_POSIX")
        format.timeZone = timeZone
        format.dateFormat = "HH:mm:ss"
        return texts.diagDelayUpdated(format.string(from: delay.measuredAt))
    }
}

/// Замеры «Через сервер» и «Новое соединение» из процесса приложения: под защитой его запросы идут в туннель, к ядру и через
/// сервер — как у любой программы iPhone. Запрос — к проверочному адресу замера серверов (`PingBatch.target`), по три раза.
/// «Через сервер» — одно соединение на все запросы, первый — прогрев, не в счет; «Новое соединение» — каждый раз свое:
/// соединение с сервером, рукопожатие, шифрование до сайта и сам запрос. Ничего не пишет в журнал.
enum DelayProbe {
    /// Сколько ждать ответа на один запрос — как у десктопа.
    static let timeout: TimeInterval = 5

    static func throughServer() async -> Int? {
        let session = session()
        defer { session.invalidateAndCancel() }
        guard await timed(session) != nil else {
            return nil
        }

        var samples: [Int] = []
        for _ in 0..<ServerDelay.samples {
            if let ms = await timed(session) {
                samples.append(ms)
            }
        }

        return ServerDelay.median(samples)
    }

    static func newConnection() async -> Int? {
        var samples: [Int] = []
        for _ in 0..<ServerDelay.samples {
            let session = session()
            if let ms = await timed(session) {
                samples.append(ms)
            }

            session.invalidateAndCancel()
        }

        return ServerDelay.median(samples)
    }

    /// Свой пул соединений без кеша и cookie: одно соединение на узел.
    private static func session() -> URLSession {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = timeout
        configuration.timeoutIntervalForResource = timeout
        configuration.httpCookieStorage = nil
        configuration.urlCache = nil
        configuration.requestCachePolicy = .reloadIgnoringLocalCacheData
        configuration.httpMaximumConnectionsPerHost = 1
        return URLSession(configuration: configuration)
    }

    /// Миллисекунды до ответа (любой ответ HTTP — связь есть); нет ответа — nil.
    private static func timed(_ session: URLSession) async -> Int? {
        guard let url = URL(string: PingBatch.target) else {
            return nil
        }

        let started = DispatchTime.now()
        guard let answer = try? await session.data(from: url), answer.1 is HTTPURLResponse else {
            return nil
        }

        return max(1, Int((DispatchTime.now().uptimeNanoseconds - started.uptimeNanoseconds) / 1_000_000))
    }
}

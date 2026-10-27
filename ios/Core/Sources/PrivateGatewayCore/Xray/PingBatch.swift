import Foundation

/// Сервер для замера: ключ (id в хранилище) и описание.
public struct PingTarget: Sendable {
    public let id: String
    public let server: ServerDefinition

    public init(id: String, server: ServerDefinition) {
        self.id = id
        self.server = server
    }
}

/// Порция замера — один вызов `LibXrayInvoke`: серверы `ids` и готовый запрос `request`. В запросе — выходы серверов с
/// ключами, поэтому в текст порции (журнал, отладка) он не попадает.
public struct PingChunk: Sendable, CustomStringConvertible {
    public let ids: [String]
    public let request: String

    /// Замер каждого сервера порции по ответу libXray на `request`.
    public func results(_ response: String?) -> [String: Latency] {
        Dictionary(zip(ids, PingBatch.parse(response, ids.count)), uniquingKeysWith: { first, _ in first })
    }

    /// Текст ошибок libXray по серверам порции, у которых он есть, — только для журнала через маскировку.
    public func errors(_ response: String?) -> [String: String] {
        var result: [String: String] = [:]
        for (id, error) in zip(ids, PingBatch.errors(response, ids.count)) {
            if let error {
                result[id] = error
            }
        }

        return result
    }

    public var description: String { "PingChunk(\(ids))" }
}

/// План замера: порции для libXray по порядку списка и серверы, за которые ядро не возьмется, — сразу «не поддерживается».
public struct PingPlan: Sendable {
    public let chunks: [PingChunk]
    public let unsupported: [String: Latency]
}

/// Задержка серверов через libXray (`pingBatch`) — как `PingBatch` Android: через настоящее ядро, а не пингом адреса. На
/// каждый сервер — конфиг из одного выхода, того же, что собирает `XrayConfig` для туннеля; libXray поднимает их в своем
/// экземпляре ядра и через каждый шлет запрос к проверочному адресу; задержка — до ответа. За вызов — не больше `maxConfigs`
/// серверов, все одновременно. Сервер, за который ядро не возьмется, в libXray не отправляется. Текст ошибок libXray в
/// результат не попадает: в нем бывают адреса серверов.
public enum PingBatch {
    /// Больше конфигов за вызов libXray не примет (`maxPingBatchConfigs`).
    public static let maxConfigs = 5

    /// Проверочный адрес — как у десктопа и Android: пустой ответ 204, без cookies и без Google.
    public static let target = "https://cp.cloudflare.com/generate_204"

    /// Срок ответа одного сервера, секунды — как у десктопа и Android.
    public static let timeoutSeconds = 5

    /// Задержка-признак ошибки в ответе libXray (`nodep.PingDelayError`).
    public static let delayError = 10_000

    /// Задержка-признак тайм-аута (`nodep.PingDelayTimeout`).
    public static let delayTimeout = 11_000

    /// Конфиг для замера — только выход сервера, `{"outbounds":[…]}` с меткой `XrayConfig.proxyTag`, или отказ ядра.
    public static func outbounds(_ server: ServerDefinition, _ settings: CoreSettings) -> XrayConfigResult {
        switch XrayConfig.outbound(server, settings) {
        case let .success(outbound):
            return XrayConfigResult(json: JsonText.write(.object([("outbounds", .array([outbound]))])), rejection: nil)
        case let .failure(refusal):
            return XrayConfigResult(json: nil, rejection: refusal.reason)
        }
    }

    /// Порции по `maxConfigs` в порядке `targets`; отказанные ядром — сразу в `unsupported`, как у десктопа.
    ///
    /// - Parameters:
    ///   - targets: серверы с неповторяющимися id.
    ///   - settings: настройки подключения: от них зависит выход (отпечаток TLS, mux).
    public static func plan(
        _ targets: [PingTarget], _ settings: CoreSettings, url: String = PingBatch.target, timeoutSeconds: Int = PingBatch.timeoutSeconds
    ) -> PingPlan {
        var unsupported: [String: Latency] = [:]
        var configs: [(id: String, json: String)] = []
        for target in targets {
            if let json = outbounds(target.server, settings).json {
                configs.append((target.id, json))
            } else {
                unsupported[target.id] = .failed(.unsupported)
            }
        }

        let chunks = stride(from: 0, to: configs.count, by: maxConfigs).compactMap { start -> PingChunk? in
            let part = configs[start..<min(start + maxConfigs, configs.count)]
            return request(part.map(\.json), url: url, timeoutSeconds: timeoutSeconds).map { PingChunk(ids: part.map(\.id), request: $0) }
        }
        return PingPlan(chunks: chunks, unsupported: unsupported)
    }

    /// Запрос `pingBatch` (версия API 3): у каждого конфига — выход `XrayConfig.proxyTag`; срок — в целых секундах. Конфигов
    /// не от 1 до `maxConfigs` или срок не больше нуля — nil: такой вызов libXray не примет.
    public static func request(_ configs: [String], url: String = PingBatch.target, timeoutSeconds: Int = PingBatch.timeoutSeconds) -> String? {
        guard (1...maxConfigs).contains(configs.count), timeoutSeconds > 0 else {
            return nil
        }

        return XrayConfig.invoke("pingBatch", [
            ("configs", .array(configs.map { .object([("xrayJson", .string($0)), ("outboundTag", .string(XrayConfig.proxyTag))]) })),
            ("timeout", .int(timeoutSeconds)),
            ("url", .string(url)),
        ])
    }

    /// Ответ libXray на запрос из `count` конфигов — замер каждого в том же порядке. Отказ всего вызова (в том числе «ядро
    /// уже запущено в процессе»), не JSON или не столько результатов — «ядро не запустилось» у всех; у сервера без ответа
    /// или с задержкой-признаком ошибки или тайм-аута — «нет ответа».
    public static func parse(_ response: String?, _ count: Int) -> [Latency] {
        guard let results = results(response, count) else {
            return Array(repeating: .failed(.coreFailed), count: count)
        }

        return results.map(item)
    }

    /// Текст ошибки каждого сервера из ответа на `count` конфигов (в том же порядке), пустой — nil. В нем бывают адреса
    /// серверов, поэтому он не входит в `parse` и в журнал идет только через маскировку. Отказ всего вызова здесь не
    /// повторяется — его текст в самом ответе (`XrayResponse`); тогда у всех nil.
    public static func errors(_ response: String?, _ count: Int) -> [String?] {
        guard let results = results(response, count) else {
            return Array(repeating: nil, count: count)
        }

        return results.map { node in
            guard case let .object(result, _) = node, case let .string(text)? = result["error"], !text.trimmingCharacters(in: .whitespaces).isEmpty else {
                return nil
            }

            return text
        }
    }

    /// Результаты удавшегося вызова, если их столько, сколько конфигов.
    private static func results(_ response: String?, _ count: Int) -> [ConfigNode]? {
        guard let parsed = try? JsonTree.parse(response ?? ""), case let .object(root, _) = parsed.root, case .bool(true)? = root["success"],
              case let .object(data, _)? = root["data"], case let .array(results, _, _)? = data["results"], results.count == count
        else {
            return nil
        }

        return results
    }

    private static func item(_ node: ConfigNode) -> Latency {
        guard case let .object(result, _) = node, case .bool(true)? = result["success"], case let .number(text)? = result["delay"], let delay = Int(text) else {
            return .failed(.noResponse)
        }

        // Признак вместо задержки — не ответ, даже если флаг успеха говорит иное.
        if delay == delayError || delay == delayTimeout {
            return .failed(.noResponse)
        }

        return .of(min(delay, Int(Int32.max)))
    }
}

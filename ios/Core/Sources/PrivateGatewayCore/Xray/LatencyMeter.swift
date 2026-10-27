/// Итог замера: все порции замерены; защита включена (или включилась во время замера); замер уже идет.
public enum MeasureResult: Equatable, Sendable {
    case done
    case busy
    case running
}

/// Задержка серверов через настоящее ядро — как `LatencyMeter` Android (решения 424, 430): libXray `pingBatch` по 5
/// серверов, тот же выход, что у туннеля, с текущими настройками. Только без защиты: libXray не меряет рядом с работающим
/// ядром, а на iOS соединения приложения под защитой ушли бы в туннель — мерилась бы дорога через сервер в работе. Перед
/// каждой порцией состояние проверяется; порция, во время которой началось подключение, отбрасывается. Итоги пишутся
/// порцией — список обновляется по мере замера. Только по просьбе пользователя, как у десктопа.
public actor LatencyMeter {
    private let servers: ServerStore
    private let settings: SettingsStore
    private let off: @Sendable () async -> Bool
    private let ping: @Sendable (String) async -> String?
    private var running = false

    /// - Parameters:
    ///   - off: выключена ли защита — спрашивается перед порцией и после нее.
    ///   - ping: вызов libXray с запросом порции; ответ как есть.
    public init(
        servers: ServerStore, settings: SettingsStore, off: @escaping @Sendable () async -> Bool, ping: @escaping @Sendable (String) async -> String?
    ) {
        self.servers = servers
        self.settings = settings
        self.off = off
        self.ping = ping
    }

    /// Идет ли замер — для кнопки «Обновить».
    public var measuring: Bool { running }

    /// Замер всех серверов; `chunk` — после записи каждой порции (окно перечитывает список).
    public func measure(chunk: @Sendable () async -> Void = {}) async -> MeasureResult {
        guard await off() else {
            return .busy
        }

        guard !running else {
            return .running
        }

        running = true
        defer { running = false }
        var targets: [PingTarget] = []
        for item in (try? await servers.servers()) ?? [] {
            if let server = try? await servers.load(item.id) {
                targets.append(PingTarget(id: item.id, server: server))
            }
        }

        let core = ((try? await settings.load()) ?? AppSettings()).tunnel.core()
        let plan = PingBatch.plan(targets, core)
        try? await servers.setLatency(plan.unsupported)
        for part in plan.chunks {
            guard await off() else {
                return .busy
            }

            let response = await ping(part.request)
            // Подключение началось во время порции: libXray мерил рядом с ядром туннеля — итогам не верить.
            guard await off() else {
                return .busy
            }

            try? await servers.setLatency(part.results(response))
            await chunk()
        }

        return .done
    }
}

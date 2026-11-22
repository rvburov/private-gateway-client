import Foundation

/// Итог замера: все порции замерены; путь сейчас не позволяет замер; замер уже идет.
public enum MeasureResult: Equatable, Sendable {
    case done
    case busy
    case running
}

/// Результат старше пяти минут или из будущего не подтверждает доступность сейчас.
public enum LatencyFreshness {
    public static let maxAge: TimeInterval = 5 * 60
    public static func isFresh(_ measuredAt: Date?, now: Date = Date()) -> Bool {
        guard let measuredAt else { return false }
        let age = now.timeIntervalSince(measuredAt)
        return age >= 0 && age < maxAge
    }
}

/// Задержка серверов через настоящее ядро — как `LatencyMeter` Android (решения 424, 430): libXray `pingBatch` по 5
/// серверов, тот же выход, что у туннеля, с текущими настройками. По умолчанию только без защиты; iOS передает context,
/// разрешая отдельному процессу приложения мерить через действующий VPN. Перед порцией и после нее ключ пути сравнивается:
/// результат прежней сети не выдается за текущий. Итоги пишутся порцией — список обновляется по мере замера.
public actor LatencyMeter {
    private let servers: ServerStore
    private let settings: SettingsStore
    private let off: @Sendable () async -> Bool
    private let ping: @Sendable (String) async -> String?
    /// iOS: отдельный процесс приложения может мерить через поднятый туннель. Непустой ключ описывает неизменный путь;
    /// его смена отбрасывает запоздалую порцию. Без этого параметра остается прежняя политика «только без защиты».
    private let context: (@Sendable () async -> String?)?
    private var running = false

    /// - Parameters:
    ///   - off: выключена ли защита — спрашивается перед порцией и после нее.
    ///   - ping: вызов libXray с запросом порции; ответ как есть.
    public init(
        servers: ServerStore, settings: SettingsStore, off: @escaping @Sendable () async -> Bool, ping: @escaping @Sendable (String) async -> String?,
        context: (@Sendable () async -> String?)? = nil
    ) {
        self.servers = servers
        self.settings = settings
        self.off = off
        self.ping = ping
        self.context = context
    }

    /// Идет ли замер — для кнопки «Обновить».
    public var measuring: Bool { running }

    /// Замер всех серверов; `chunk` — после записи каждой порции (окно перечитывает список).
    public func measure(
        results: @Sendable ([String: Latency]) async -> Void = { _ in }, chunk: @Sendable () async -> Void = {}
    ) async -> MeasureResult {
        guard let started = await path() else {
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

        let app = (try? await settings.load()) ?? AppSettings()
        // Медленный профиль получает срок из настроек подключения, но одна порция не занимает больше 30 секунд.
        let timeout = min(30, max(PingBatch.timeoutSeconds, app.connection.connectTimeout))
        let plan = PingBatch.plan(targets, app.core(), timeoutSeconds: timeout)
        try? await servers.setLatency(plan.unsupported)
        await results(plan.unsupported)
        for part in plan.chunks {
            guard await path() == started else {
                return .busy
            }

            let response = await ping(part.request)
            // Сеть или профиль сменились во время порции: результат уже относится к прежнему пути.
            guard await path() == started else {
                return .busy
            }

            let measured = part.results(response)
            try? await servers.setLatency(measured)
            await results(measured)
            await chunk()
        }

        return .done
    }

    private func path() async -> String? {
        if let context { return await context() }
        return await off() ? "direct" : nil
    }
}

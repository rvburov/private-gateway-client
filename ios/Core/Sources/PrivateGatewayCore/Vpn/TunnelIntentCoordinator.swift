import Foundation

/// Намерение окна меняется до первого await. Нативные сохранения профиля нельзя отменить, поэтому запоздалое сохранение
/// сначала дожидается завершения и очищается в той же очереди, а уже потом разрешается следующий запуск.
@MainActor
public final class TunnelIntentCoordinator {
    private let work = SerialWork()
    private var generation: UInt64 = 0
    public var current: UInt64 { generation }

    public init() {}

    /// Отмена намерения синхронно, до сохранения защитного барьера или ожидания очереди нативного профиля.
    public func invalidate() { generation &+= 1 }

    public func isCurrent(_ ticket: UInt64) -> Bool {
        generation == ticket && !Task.isCancelled
    }

    public func requireCurrent(_ ticket: UInt64) throws {
        guard isCurrent(ticket) else { throw CancellationError() }
    }

    public func perform(
        _ operation: @MainActor (UInt64) async throws -> Void, cleanup: @MainActor () async -> Void
    ) async throws {
        generation &+= 1
        let ticket = generation
        try await work.run {
            // Намерение отменилось, еще не начавшись: ему нечего очищать и нельзя трогать чужой сеанс.
            try requireCurrent(ticket)
            do {
                try await operation(ticket)
                try requireCurrent(ticket)
            } catch {
                await cleanup()
                throw error
            }
        }
    }

    /// Возвращается только после всех прежних нативных сохранений и их cleanup. Более новый запуск стоит за этим барьером.
    public func stop(_ cleanup: @MainActor () async -> Void) async {
        invalidate()
        await work.run { await cleanup() }
    }
}

/// Подтверждение нативной очистки: прекращение процесса без сохраненного выключения On Demand недостаточно.
@MainActor
public enum TunnelCleanupConfirmation {
    public static func perform(
        disable: @MainActor () async throws -> Bool, stopped: @MainActor () async -> Bool
    ) async -> Bool {
        let saved: Bool
        do { saved = try await disable() } catch { saved = false }
        // Остановку нужно дождаться и при ошибке preferences, чтобы по возможности восстановить сеть.
        let ended = await stopped()
        return saved && ended
    }
}

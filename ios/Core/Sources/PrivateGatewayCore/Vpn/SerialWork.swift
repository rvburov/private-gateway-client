import Foundation

/// Асинхронная очередь действий с ядром. Ошибка и ее cleanup должны обрабатываться внутри body: тогда следующее действие
/// не начнется между ошибкой прежнего запуска и остановкой его ядра. Body проверяет отмену и поколение перед действием.
public final class SerialWork: @unchecked Sendable {
    private let lock = NSLock()
    private var busy = false
    private var waiting: [CheckedContinuation<Void, Never>] = []

    public init() {}

    public nonisolated(nonsending) func run<T>(
        _ body: () async throws -> T, onFailure: ((any Error) async -> Void)? = nil
    ) async rethrows -> T {
        await acquire()
        defer { release() }
        do {
            return try await body()
        } catch {
            await onFailure?(error)
            throw error
        }
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

import Foundation

/// Журнал расширения туннеля для «Проверки» — файл в общей папке: приложение не может читать журнал системы другого
/// процесса. Каждая строка — время и событие, уже без секретов (`SecretMasker`): адресов серверов, ключей и адресов подписок в
/// файле нет. Хранятся последние `limit` строк — файл не растет.
public struct TunnelLog: Sendable {
    public static let limit = 200

    private let file: any StateFile
    private let clock: @Sendable () -> Date
    private let lock = NSLock()

    public init(file: any StateFile, clock: @escaping @Sendable () -> Date = { Date() }) {
        self.file = file
        self.clock = clock
    }

    /// Добавить строку; не записалась — журнал не важнее туннеля, ошибка молча пропускается.
    public func add(_ event: String) {
        lock.lock()
        defer { lock.unlock() }
        let line = Self.stamp(clock()) + " " + SecretMasker.mask(event).replacingOccurrences(of: "\n", with: " ")
        let kept = (readLines() + [line]).suffix(Self.limit)
        try? file.write(Data(kept.joined(separator: "\n").utf8))
    }

    /// Строки по порядку, последние — в конце.
    public func lines() -> [String] {
        lock.lock()
        defer { lock.unlock() }
        return readLines()
    }

    private func readLines() -> [String] {
        guard let data = try? file.read() else {
            return []
        }

        return String(decoding: data, as: UTF8.self).split(separator: "\n").map(String.init)
    }

    /// «2026-10-27 13:05:42» по UTC — одинаково у приложения и расширения и без часового пояса в отчете.
    static func stamp(_ date: Date) -> String {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = TimeZone(identifier: "UTC")
        formatter.dateFormat = "yyyy-MM-dd HH:mm:ss"
        return formatter.string(from: date)
    }
}

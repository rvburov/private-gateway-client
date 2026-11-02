import Foundation

/// Уровень записи журнала туннеля — как у журнала службы десктопа: подробность, событие, предупреждение, ошибка. В файле —
/// три буквы после времени («INF», «WRN»), «Проверка» красит уровень цветом.
public enum TunnelLogLevel: String, CaseIterable, Sendable {
    case debug = "DBG"
    case info = "INF"
    case warning = "WRN"
    case error = "ERR"
}

/// Запись журнала туннеля: время (в файле — UTC), уровень и событие без секретов. Время не разобрано — nil.
public struct TunnelLogEntry: Equatable, Sendable {
    public var time: Date?
    public var level: TunnelLogLevel
    public var message: String

    public init(time: Date?, level: TunnelLogLevel, message: String) {
        self.time = time
        self.level = level
        self.message = message
    }
}

/// Журнал расширения туннеля для «Проверки» — файл в общей папке: приложение не может читать журнал системы другого
/// процесса. Каждая строка — время, уровень и событие, уже без секретов (`SecretMasker`): адресов серверов, ключей и адресов
/// подписок в файле нет. Хранятся последние `limit` строк — файл не растет.
public struct TunnelLog: Sendable {
    public static let limit = 200

    /// Длина времени в начале строки: «2026-10-27 13:05:42».
    static let stampLength = 19

    private let file: any StateFile
    private let clock: @Sendable () -> Date
    private let lock = NSLock()

    public init(file: any StateFile, clock: @escaping @Sendable () -> Date = { Date() }) {
        self.file = file
        self.clock = clock
    }

    /// Добавить запись уровня `level`; не записалась — журнал не важнее туннеля, ошибка молча пропускается.
    public func add(_ event: String, level: TunnelLogLevel = .info) {
        lock.lock()
        defer { lock.unlock() }
        let line = Self.stamp(clock()) + " " + level.rawValue + " " + SecretMasker.mask(event).replacingOccurrences(of: "\n", with: " ")
        let kept = (readLines() + [line]).suffix(Self.limit)
        try? file.write(Data(kept.joined(separator: "\n").utf8))
    }

    /// Строки по порядку, последние — в конце.
    public func lines() -> [String] {
        lock.lock()
        defer { lock.unlock() }
        return readLines()
    }

    /// Записи по порядку, последние — в конце.
    public func entries() -> [TunnelLogEntry] {
        lines().map(Self.entry)
    }

    /// Запись из строки файла «2026-10-27 13:05:42 WRN событие». Строка без уровня (журнал прежней версии) — INF; время не
    /// разобрано — без времени, и событие — вся строка.
    public static func entry(_ line: String) -> TunnelLogEntry {
        guard line.count >= stampLength, let time = formatter().date(from: String(line.prefix(stampLength))) else {
            return TunnelLogEntry(time: nil, level: .info, message: line)
        }

        var rest = line.dropFirst(stampLength)
        if rest.first == " " {
            rest = rest.dropFirst()
        }

        let word = rest.prefix(4)
        if word.count == 4, word.last == " ", let level = TunnelLogLevel(rawValue: String(word.prefix(3))) {
            return TunnelLogEntry(time: time, level: level, message: String(rest.dropFirst(4)))
        }

        return TunnelLogEntry(time: time, level: .info, message: String(rest))
    }

    private func readLines() -> [String] {
        guard let data = try? file.read() else {
            return []
        }

        return String(decoding: data, as: UTF8.self).split(separator: "\n").map(String.init)
    }

    /// «2026-10-27 13:05:42» по UTC — одинаково у приложения и расширения и без часового пояса в отчете.
    static func stamp(_ date: Date) -> String {
        formatter().string(from: date)
    }

    private static func formatter() -> DateFormatter {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = TimeZone(identifier: "UTC")
        formatter.dateFormat = "yyyy-MM-dd HH:mm:ss"
        return formatter
    }
}

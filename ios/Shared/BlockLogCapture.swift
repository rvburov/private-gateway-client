import Darwin
import Foundation
import PrivateGatewayCore

/// Error-поток ядра направляется в отдельный pipe, access остается none. Внутренний Info сообщает подтвержденный
/// post-sniff/FakeDNS detour. Посещения не пишутся на диск; остальные строки фильтруются по выбранному уровню и маскируются.
final class BlockLogCapture: @unchecked Sendable {
    private let pipe = Pipe()
    private let history: BlockedSiteHistory
    private let level: String
    private let knownSecrets: [String]
    private let emit: @Sendable (String, TunnelLogLevel) -> Void
    private let lock = NSLock()
    private var pending = Data()
    private var started = false
    private var source: (any DispatchSourceRead)?
    private var accepting = false

    init(history: BlockedSiteHistory, level: String = "warning", knownSecrets: [String] = [], emit: @escaping @Sendable (String, TunnelLogLevel) -> Void = { _, _ in }) {
        self.history = history
        self.level = level
        self.knownSecrets = knownSecrets
        self.emit = emit
    }

    func start() throws -> String {
        let writer = pipe.fileHandleForWriting.fileDescriptor
        guard fcntl(writer, F_GETFD) >= 0 else { throw TunnelFailure(TunnelErrors.coreFailed, detail: "capture unavailable") }
        let path = "/dev/fd/\(writer)"
        guard !started else { return path }
        // Та же проверка прав, которую выполнит Go os.OpenFile. Если устройство закрывает /dev/fd, журнал недоступен,
        // но конфиг VPN остается прежним: вспомогательная история не должна мешать подключению.
        let checked = open(path, O_WRONLY | O_APPEND | O_CREAT, mode_t(0o600))
        guard checked >= 0 else { throw TunnelFailure(TunnelErrors.coreFailed, detail: "capture unavailable") }
        close(checked)
        let reader = pipe.fileHandleForReading
        let descriptor = reader.fileDescriptor
        let source = DispatchSource.makeReadSource(fileDescriptor: descriptor, queue: DispatchQueue(label: "com.privategateway.client.block-events"))
        source.setEventHandler { [weak self] in
            var bytes = [UInt8](repeating: 0, count: 16_384)
            let count = bytes.withUnsafeMutableBytes { Darwin.read(descriptor, $0.baseAddress, $0.count) }
            if count > 0 { self?.receive(Data(bytes.prefix(count))) }
        }
        source.setCancelHandler { try? reader.close() }
        started = true
        lock.lock()
        accepting = true
        lock.unlock()
        self.source = source
        source.resume()
        return path
    }

    func stop() {
        guard started else { return }
        started = false
        lock.lock()
        accepting = false
        pending = Data()
        lock.unlock()
        source?.cancel()
        source = nil
        try? pipe.fileHandleForWriting.close()
        lock.lock()
        pending = Data()
        lock.unlock()
    }

    private func receive(_ data: Data) {
        lock.lock()
        defer { lock.unlock() }
        guard accepting else { return }
        pending.append(data)
        while let end = pending.firstIndex(of: 10) {
            let line = String(decoding: pending[..<end], as: UTF8.self)
            pending.removeSubrange(...end)
            history.receive(line)
            if let (message, entryLevel) = Self.filteredLog(line, requested: level, knownSecrets: knownSecrets) { emit(message, entryLevel) }
        }
        if pending.count > 16_384 { pending = Data() }
    }

    /// Обычный журнал не должен становиться списком сайтов даже на выбранном пользователем Debug.
    static func filteredLog(_ line: String, requested: String, knownSecrets: [String] = []) -> (String, TunnelLogLevel)? {
        guard !line.contains("taking detour ["), !line.contains("taking platform initialized detour ["),
              !line.contains("sniffed domain:"), !line.contains(" accepted "), !line.contains(" rejected ") else { return nil }
        let severity: TunnelLogLevel
        if line.contains("[Error]") { severity = .error }
        else if line.contains("[Warning]") { severity = .warning }
        else if line.contains("[Info]"), requested != "warning" { severity = .info }
        else if line.contains("[Debug]"), requested == "debug" { severity = .debug }
        else { return nil }
        let masked = SecretMasker.mask(line, knownSecrets: knownSecrets)
        let endpoints = endpoint.stringByReplacingMatches(in: masked, range: NSRange(masked.startIndex..., in: masked), withTemplate: "[target]")
        let result = domain.stringByReplacingMatches(in: endpoints, range: NSRange(endpoints.startIndex..., in: endpoints), withTemplate: "[site]")
        return (result, severity)
    }

    nonisolated(unsafe) private static let endpoint = try! NSRegularExpression(pattern: #"\b(?:tcp|udp):\S+"#)
    nonisolated(unsafe) private static let domain = try! NSRegularExpression(
        pattern: #"\b(?:[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?\.)+[A-Za-z]{2,63}\b"#
    )

    deinit { stop() }
}

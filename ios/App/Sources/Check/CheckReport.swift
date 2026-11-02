import Foundation
import PrivateGatewayCore
import PrivateGatewayTheme
import SwiftUI
import UniformTypeIdentifiers

/// Строка журнала на «Проверке» (.card.log доски 7): местное время, уровень и событие без секретов.
struct JournalLine: Equatable {
    let time: String
    let level: TunnelLogLevel
    let message: String
}

/// Журнал туннеля на экране — как Journal десктопа: последние 50 записей, новые сверху, время телефона «15:42:12»; уровень —
/// цветом: INF — акцентом, WRN — оранжевым, ERR — красным, DBG — серым.
enum JournalLook {
    static let shown = 50

    static func lines(_ entries: [TunnelLogEntry], limit: Int = shown, timeZone: TimeZone = .current) -> [JournalLine] {
        let format = DateFormatter()
        format.locale = Locale(identifier: "en_US_POSIX")
        format.timeZone = timeZone
        format.dateFormat = "HH:mm:ss"
        return entries.suffix(limit).reversed().map { entry in
            JournalLine(time: entry.time.map { format.string(from: $0) } ?? "--:--:--", level: entry.level, message: SecretMasker.mask(entry.message))
        }
    }

    static func color(_ level: TunnelLogLevel) -> PgRGBA {
        switch level {
        case .debug: return PgColor.Text.muted
        case .info: return PgColor.Accent.text
        case .warning: return PgColor.Status.Warning.text
        case .error: return PgColor.Status.Danger.text
        }
    }
}

/// Обезличивание отчета — `ReportAnonymizer` десктопа: публичные адреса IPv4 и все адреса IPv6 — метками «[адрес-1]»,
/// «[адрес-2]»… (один адрес — одна метка во всем отчете), MAC — «[MAC]». Частные и служебные IPv4, известные открытые
/// резолверы и широкие сети (/8 и шире) остаются. Секреты и ссылки потом скрывает `SecretMasker`.
final class ReportAnonymizer {
    /// Открытые резолверы: известны всем и ничего не говорят о пользователе.
    static let wellKnown: Set<String> = [
        "1.1.1.1", "1.0.0.1", "8.8.8.8", "8.8.4.4", "9.9.9.9", "149.112.112.112", "2606:4700:4700::1111", "2606:4700:4700::1001",
        "2001:4860:4860::8888", "2001:4860:4860::8844",
    ]

    private let label: String
    private var tokens: [String: String] = [:]
    // Выражения постоянные — ошибка в них видна первым же тестом.
    private let mac = try! NSRegularExpression(pattern: #"\b[0-9A-Fa-f]{2}(?:[:-][0-9A-Fa-f]{2}){5}\b"#)
    private let ipv4 = try! NSRegularExpression(
        pattern: #"(?<![\d.])(?<address>(?:25[0-5]|2[0-4]\d|1?\d?\d)(?:\.(?:25[0-5]|2[0-4]\d|1?\d?\d)){3})(?<prefix>/\d{1,2}(?!\d))?(?![\d.])"#
    )
    private let ipv6 = try! NSRegularExpression(
        pattern: #"(?<![0-9A-Fa-f:])(?<address>(?:[0-9A-Fa-f]{0,4}:){2,7}[0-9A-Fa-f]{0,4}(?:%[0-9A-Za-z]+)?)(?<prefix>/\d{1,3}(?!\d))?(?![0-9A-Fa-f:])"#
    )

    /// - Parameter label: слово метки — «адрес» или «address» (diag.report.addressLabel).
    init(label: String) {
        self.label = label
    }

    /// Строка с метками вместо личных адресов и MAC и без секретов.
    func clean(_ text: String) -> String {
        guard !text.isEmpty else {
            return text
        }

        var result = replace(mac, text) { _, _ in "[MAC]" }
        result = replace(ipv6, result) { match, source in
            let whole = source.substring(with: match.range)
            guard let raw = Self.group(match, "address", source), let bytes = Self.ipv6Bytes(String(raw.split(separator: "%").first ?? "")) else {
                return whole
            }

            let prefix = Self.group(match, "prefix", source)
            return Self.keep6(bytes) || Self.wide(prefix) ? whole : token(AddressText.of(bytes)) + (prefix ?? "")
        }
        result = replace(ipv4, result) { match, source in
            let whole = source.substring(with: match.range)
            guard let address = Self.group(match, "address", source) else {
                return whole
            }

            let bytes = address.split(separator: ".").compactMap { UInt8($0) }
            let prefix = Self.group(match, "prefix", source)
            return bytes.count != 4 || Self.keep4(bytes, address) || Self.wide(prefix) ? whole : token(address) + (prefix ?? "")
        }
        return SecretMasker.mask(result)
    }

    /// Адрес IPv4 остается: известный резолвер, частный или служебный (локальная сеть, туннель, петля, CGNAT, link-local,
    /// тестовые 198.18/15, групповые и выше).
    static func keep4(_ b: [UInt8], _ address: String) -> Bool {
        if wellKnown.contains(address) {
            return true
        }

        return b[0] == 0 || b[0] == 10 || b[0] == 127 || b[0] >= 224
            || (b[0] == 100 && b[1] >= 64 && b[1] < 128)
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 172 && b[1] >= 16 && b[1] < 32)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 198 && (b[1] == 18 || b[1] == 19))
    }

    /// Адрес IPv6 остается: петля, пустой и известный резолвер.
    static func keep6(_ bytes: [UInt8]) -> Bool {
        let zero = bytes.dropLast().allSatisfy { $0 == 0 }
        return (zero && (bytes.last == 0 || bytes.last == 1)) || wellKnown.contains(AddressText.of(bytes))
    }

    /// Сеть маршрута шириной /8 и больше — общая для всех, а не адрес пользователя.
    static func wide(_ prefix: String?) -> Bool {
        guard let prefix, let length = Int(prefix.dropFirst()) else {
            return false
        }

        return length <= 8
    }

    /// Шестнадцать байт адреса IPv6; не адрес — nil.
    static func ipv6Bytes(_ text: String) -> [UInt8]? {
        var address = in6_addr()
        guard inet_pton(AF_INET6, text, &address) == 1 else {
            return nil
        }

        return withUnsafeBytes(of: address) { Array($0) }
    }

    private func token(_ key: String) -> String {
        if let token = tokens[key] {
            return token
        }

        let token = "[\(label)-\(tokens.count + 1)]"
        tokens[key] = token
        return token
    }

    private static func group(_ match: NSTextCheckingResult, _ name: String, _ source: NSString) -> String? {
        let range = match.range(withName: name)
        return range.location == NSNotFound ? nil : source.substring(with: range)
    }

    /// Замена каждого совпадения тем, что вернет `transform`.
    private func replace(_ regex: NSRegularExpression, _ text: String, _ transform: (NSTextCheckingResult, NSString) -> String) -> String {
        let source = text as NSString
        var result = ""
        var last = 0
        for match in regex.matches(in: text, range: NSRange(location: 0, length: source.length)) {
            result += source.substring(with: NSRange(location: last, length: match.range.location - last))
            result += transform(match, source)
            last = match.range.location + match.range.length
        }

        return result + source.substring(from: last)
    }
}

/// Что входит в отчет «Проверки».
struct CheckReportInput {
    var rows: [CheckRow] = []
    var delay: [DelayRow] = []
    var why: String?
    var hinder: [CheckRow] = []
    var networks: [PhoneNetwork] = []
    var journal: [TunnelLogEntry] = []
    var about = AboutInfo()
    /// Состояние словом — подпись под щитом.
    var state = ""
    /// Реальный IP этой сети: в отчет он не попадает даже меткой.
    var real: [String] = []
    var now = Date()
}

/// «Экспорт отчета» — BuildReport десктопа для телефона: версия, iOS и ядро, когда проверено, состояние, те же строки, что на
/// экране, задержка, что мешает, сети iPhone и весь журнал туннеля (новые сверху). Каждая строка обезличена
/// (`ReportAnonymizer`, затем `SecretMasker`): публичные адреса — метками, MAC и секреты скрыты, реального IP нет совсем.
enum CheckReport {
    static func text(_ input: CheckReportInput, _ texts: PgTexts, timeZone: TimeZone = .current) -> String {
        let platform = [input.about.system, input.about.arch, input.about.core.map { CheckRows.coreName + " " + $0 } ?? ""].filter { !$0.isEmpty }
        var lines = [
            texts.diagReportTitle(input.about.app),
            platform.joined(separator: " · "),
            texts.diagReportCheckedAt(stamp(input.now, "yyyy-MM-dd HH:mm:ss xxx", timeZone)),
            texts.diagReportState(input.state),
            "",
            texts.diagReportChecks,
        ]
        lines += input.rows.map { "  [\($0.chip)] \($0.title) — \(hide($0.detail, input.real))" }
        lines += ["", texts.diagReportDelay]
        lines += input.delay.map { "  \($0.title): \($0.value) — \($0.text)" }
        if let why = input.why {
            lines.append("  " + why)
        }

        if !input.hinder.isEmpty {
            lines += ["", texts.diagReportHinder]
            lines += input.hinder.map { "  [\($0.chip)] \($0.title) — \(hide($0.detail, input.real))" }
        }

        if !input.networks.isEmpty {
            lines += ["", texts.iosInspectTitle]
            lines += input.networks.map { "  " + PhoneNetworkText.line($0, texts) }
        }

        lines += ["", texts.diagReportJournal]
        lines += input.journal.reversed().map { entry in
            let time = entry.time.map { stamp($0, "yyyy-MM-dd HH:mm:ss", timeZone) } ?? ""
            return "  \(time) \(entry.level.rawValue)  \(SecretMasker.mask(entry.message))"
        }
        lines += ["", texts.diagReportAnonymous]

        let anonymizer = ReportAnonymizer(label: texts.diagReportAddressLabel)
        var text = ""
        for line in lines {
            text += anonymizer.clean(hide(line, input.real)) + "\n"
        }

        return text
    }

    /// Имя файла: «private-gateway-report-20261102-1542» — расширение .txt ставит система по виду файла.
    static func fileName(_ now: Date, timeZone: TimeZone = .current) -> String {
        "private-gateway-report-" + stamp(now, "yyyyMMdd-HHmm", timeZone)
    }

    /// Реальный IP — звездочками: в отчет он не попадает, даже если сейчас виден сайтам.
    private static func hide(_ text: String, _ real: [String]) -> String {
        real.filter { !$0.isEmpty }.reduce(text) { $0.replacingOccurrences(of: $1, with: CheckRows.hidden) }
    }

    private static func stamp(_ date: Date, _ pattern: String, _ timeZone: TimeZone) -> String {
        let format = DateFormatter()
        format.locale = Locale(identifier: "en_US_POSIX")
        format.timeZone = timeZone
        format.dateFormat = pattern
        return format.string(from: date)
    }
}

/// Отчет файлом для «Экспорта отчета» (`fileExporter`): обычный текст UTF-8.
struct ReportFile: FileDocument {
    static var readableContentTypes: [UTType] { [.plainText] }

    let text: String

    init(text: String) {
        self.text = text
    }

    init(configuration: ReadConfiguration) throws {
        text = configuration.file.regularFileContents.map { String(decoding: $0, as: UTF8.self) } ?? ""
    }

    func fileWrapper(configuration: WriteConfiguration) throws -> FileWrapper {
        FileWrapper(regularFileWithContents: Data(text.utf8))
    }
}

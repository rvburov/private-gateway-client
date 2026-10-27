import Foundation

/// Строка разобранного текста: номер (с 1) и результат разбора.
public struct ImportLine: Equatable, Sendable {
    public let number: Int
    public let result: LinkResult
}

/// Разобранный текст: формат ([ImportParser.formats]) и строки по порядку.
public struct ImportBatch: Equatable, Sendable {
    public let format: String
    public let lines: [ImportLine]

    public var found: Int { lines.count }

    public var failed: Int { lines.filter { !$0.result.isOk }.count }
}

/// Текст из буфера, файла или подписки — как на десктопе и Android (`ImportParser`): ссылки по одной в строке, такой же
/// список в base64 (обычная подписка), конфиг sing-box (JSON) или Clash/Mihomo (YAML со списком proxies). В списке ссылок
/// пустые строки и строки-комментарии («#», «//») пропускаются.
public enum ImportParser {
    public static let formatLinks = "links"
    public static let formatBase64 = "base64"
    public static let formatSingBox = "singbox"
    public static let formatClash = "clash"

    /// Все форматы — для подписей на экране импорта.
    public static let formats = [formatLinks, formatBase64, formatSingBox, formatClash]

    public static func parse(_ input: String) -> ImportBatch {
        var text = String(Substring(input.unicodeScalars.drop(while: { $0 == "\u{FEFF}" })))
        let trimmed = TextKit.trimStart(text)
        if trimmed.hasPrefix("{") || trimmed.hasPrefix("[") {
            return SingBoxImport.parse(text)
        }

        if hasClashProxies(text) {
            return ClashImport.parse(text)
        }

        var format = formatLinks
        if !text.contains("://"), let decoded = LinkParser.decodeBase64(text), decoded.contains("://") {
            format = formatBase64
            text = decoded
        }

        var lines: [ImportLine] = []
        for (index, raw) in TextKit.lines(text).enumerated() {
            let line = TextKit.trim(raw)
            if line.isEmpty || line.hasPrefix("#") || line.hasPrefix("//") {
                continue
            }

            lines.append(ImportLine(number: index + 1, result: LinkParser.parse(line)))
        }

        return ImportBatch(format: format, lines: lines)
    }

    /// Конфиг Clash: строка «proxies:» с начала строки (пробелы и табуляция перед «:» можно).
    private static func hasClashProxies(_ text: String) -> Bool {
        TextKit.lines(text).contains { line in
            guard line.hasPrefix("proxies") else {
                return false
            }

            let rest = line.unicodeScalars.dropFirst("proxies".unicodeScalars.count).drop { $0 == " " || $0 == "\t" }
            return rest.first == ":"
        }
    }
}

/// Флаг страны в начале названия («🇳🇱 Нидерланды 01») — код страны для значка и название без флага: значок «NL» есть в
/// макете, а цветные флаги шрифт рисует не везде.
public enum CountryFlag {
    private static let regionalA: UInt32 = 0x1F1E6
    private static let regionalZ: UInt32 = 0x1F1FF

    /// Код страны (пусто — флага нет) и название без флага.
    public static func split(_ name: String) -> (String, String) {
        let trimmed = TextKit.trim(name)
        let points = Array(trimmed.unicodeScalars)
        if points.count >= 2, isRegional(points[0]), isRegional(points[1]) {
            let code = String(letter(points[0])) + String(letter(points[1]))
            var rest = String.UnicodeScalarView()
            rest.append(contentsOf: points[2...])
            return (code, TextKit.trim(String(rest)))
        }

        return ("", trimmed)
    }

    private static func isRegional(_ point: Unicode.Scalar) -> Bool {
        (regionalA...regionalZ).contains(point.value)
    }

    private static func letter(_ point: Unicode.Scalar) -> Character {
        Character(Unicode.Scalar(UInt8(65 + point.value - regionalA)))
    }
}

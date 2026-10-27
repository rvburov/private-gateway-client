import Foundation

/// Texts.swift из localization/ru.json и en.json: ключ «status.connected.title» → свойство `PgTexts.statusConnectedTitle`,
/// текст с подстановками «{N}» → функция с N+1 аргументами (наибольший номер плюс один, как у десктопа). Ключи RU и EN и
/// номера подстановок в них обязаны совпадать, два ключа с одним именем — ошибка сборки, а не молча потерянный текст.
enum TextsWriter {
    /// Имена, которые уже есть у `PgTexts`, — ключ с таким именем перекрыл бы их.
    static let reserved: Set<String> = ["language", "text", "line", "format", "count", "keys", "russian", "english", "index", "substitute"]

    static func write(russian: JsonValue, english: JsonValue) throws -> String {
        let ru = try texts(russian, "ru")
        let en = try texts(english, "en")
        if Set(ru.keys) != Set(en.keys) {
            let missing = Set(ru.keys).subtracting(en.keys).sorted().map { "en: \($0)" } + Set(en.keys).subtracting(ru.keys).sorted().map { "ru: \($0)" }
            throw SharedError("Ключи RU и EN разные — нет \(missing.prefix(10).joined(separator: ", "))")
        }

        let keys = ru.keys.sorted()
        var owners: [String: String] = [:]
        var members: [(name: String, key: String, arguments: Int)] = []
        for key in keys {
            let name = try SwiftCode.camel(key)
            if let owner = owners[name] {
                throw SharedError("Ключи «\(owner)» и «\(key)» дают одно имя \(name)")
            }

            if reserved.contains(name) {
                throw SharedError("Ключ «\(key)» дает имя \(name), занятое у PgTexts")
            }

            owners[name] = key
            let placeholders = Self.placeholders(ru[key] ?? "")
            if placeholders != Self.placeholders(en[key] ?? "") {
                throw SharedError("У «\(key)» разные подстановки в RU и EN")
            }

            members.append((name: name, key: key, arguments: (placeholders.max() ?? -1) + 1))
        }

        let code = SwiftCode()
        code.line(SharedWriter.header)
        code.line("// Источник: shared/specifications/localization/ru.json и en.json, текстов — \(keys.count).")
        code.line()
        code.block("extension PgTexts") {
            array(code, "keys", keys)
            code.line()
            array(code, "russian", keys.map { ru[$0] ?? "" })
            code.line()
            array(code, "english", keys.map { en[$0] ?? "" })
            for (number, member) in members.enumerated() {
                code.line()
                code.line("/// " + summary(ru[member.key] ?? ""))
                if member.arguments == 0 {
                    code.line("public var \(member.name): String { line(\(number)) }")
                } else {
                    let parameters = (0..<member.arguments).map { "_ p\($0): any CustomStringConvertible" }.joined(separator: ", ")
                    let arguments = (0..<member.arguments).map { "p\($0)" }.joined(separator: ", ")
                    code.line("public func \(member.name)(\(parameters)) -> String { format(\(number), [\(arguments)]) }")
                }
            }
        }

        return code.result
    }

    /// Номера подстановок «{N}» в тексте.
    static func placeholders(_ text: String) -> Set<Int> {
        var result = Set<Int>()
        let scalars = Array(text.unicodeScalars)
        var i = 0
        while i < scalars.count {
            if scalars[i] == "{" {
                var j = i + 1
                var digits = ""
                while j < scalars.count, ("0"..."9").contains(scalars[j]) {
                    digits.unicodeScalars.append(scalars[j])
                    j += 1
                }

                if !digits.isEmpty, j < scalars.count, scalars[j] == "}", let number = Int(digits) {
                    result.insert(number)
                    i = j
                }
            }

            i += 1
        }

        return result
    }

    private static func texts(_ json: JsonValue, _ language: String) throws -> [String: String] {
        var result: [String: String] = [:]
        for (key, value) in try json.object("localization/\(language).json") {
            guard case let .string(text) = value else {
                throw SharedError("Текст «\(key)» в \(language).json — не строка")
            }

            result[key] = text
        }

        return result
    }

    private static func array(_ code: SwiftCode, _ name: String, _ values: [String]) {
        code.line("static let \(name): [String] = [")
        for value in values {
            code.line("    \(SwiftCode.literal(value)),")
        }

        code.line("]")
    }

    /// Русский текст для подсказки Xcode — одной строкой, не длиннее 100 знаков.
    private static func summary(_ text: String) -> String {
        let line = text.split(whereSeparator: \.isNewline).joined(separator: " ")
        return line.count > 100 ? String(line.prefix(99)) + "…" : line
    }
}

/// Icons.swift из Icons.axaml десктопа: Icon.Nav.Home → `PgIcons.navHome` — данные пути SVG, как у Icons.kt Android; контур
/// для SwiftUI собирает `SvgPath`.
enum IconsWriter {
    static func write(_ xaml: String) throws -> String {
        let icons = try parse(xaml)
        if icons.isEmpty {
            throw SharedError("В Icons.axaml нет иконок")
        }

        let code = SwiftCode()
        code.line(SharedWriter.header)
        code.line("// Источник: desktop/src/Client.UI/Themes/Icons.axaml — контуры 24×24 из макета, рисуются линией.")
        code.line()
        code.line("/// Контуры иконок (данные пути SVG); контур SwiftUI — через `SvgPath.parse`.")
        try code.block("public enum PgIcons") {
            var names: [String: String] = [:]
            var entries: [(String, String)] = []
            for (key, path) in icons {
                let name = try SwiftCode.camel(key)
                if let owner = names[name] {
                    throw SharedError("Иконки «\(owner)» и «\(key)» дают одно имя \(name)")
                }

                names[name] = key
                entries.append((key, name))
                code.line("public static let \(name) = \(SwiftCode.literal(path))")
            }

            code.line()
            code.line("/// Все иконки по имени без «Icon.» («Nav.Home»).")
            code.line("public static let all: [String: String] = [")
            for (key, name) in entries {
                code.line("    \(SwiftCode.literal(key)): \(name),")
            }

            code.line("]")
        }

        return code.result
    }

    /// Иконки по порядку файла: ключ без «Icon.» → контур. Комментарии XML пропускаются.
    static func parse(_ xaml: String) throws -> [(String, String)] {
        var text = Substring(xaml)
        var clean = ""
        while let open = text.range(of: "<!--") {
            clean += text[..<open.lowerBound]
            guard let close = text[open.upperBound...].range(of: "-->") else {
                throw SharedError("Icons.axaml: незакрытый комментарий")
            }

            text = text[close.upperBound...]
        }

        clean += text

        var icons: [(String, String)] = []
        var rest = Substring(clean)
        while let open = rest.range(of: "<StreamGeometry") {
            guard let tagEnd = rest[open.upperBound...].range(of: ">"),
                  let close = rest[tagEnd.upperBound...].range(of: "</StreamGeometry>"),
                  let keyStart = rest[open.upperBound..<tagEnd.lowerBound].range(of: "x:Key=\""),
                  let keyEnd = rest[keyStart.upperBound..<tagEnd.lowerBound].range(of: "\"")
            else {
                throw SharedError("Icons.axaml: StreamGeometry не разобран")
            }

            let key = String(rest[keyStart.upperBound..<keyEnd.lowerBound])
            guard key.hasPrefix("Icon.") else {
                throw SharedError("Иконка без префикса Icon.: «\(key)»")
            }

            let path = rest[tagEnd.upperBound..<close.lowerBound].trimmingCharacters(in: .whitespacesAndNewlines)
            icons.append((String(key.dropFirst(5)), path))
            rest = rest[close.upperBound...]
        }

        return icons
    }
}

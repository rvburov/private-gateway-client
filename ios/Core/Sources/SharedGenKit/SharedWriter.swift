import Foundation

/// Общие файлы не превращаются в код — сборка падает с этим текстом.
public struct SharedError: Error, CustomStringConvertible {
    public let description: String

    init(_ description: String) {
        self.description = description
    }
}

/// Код Swift темы, текстов и иконок iOS из общих файлов — как build-logic Android (`ThemeWriter`, `ResourcesWriter`) и
/// scripts/gen-tokens.cs десктопа, с теми же именами и значениями. Запускает его плагин SharedResources при каждой сборке
/// модуля PrivateGatewayTheme (через исполняемый SharedGen), поэтому сгенерированное в git не хранится.
public enum SharedWriter {
    public static let tokensPath = "shared/specifications/design-tokens.json"
    public static let russianPath = "shared/specifications/localization/ru.json"
    public static let englishPath = "shared/specifications/localization/en.json"
    public static let iconsPath = "desktop/src/Client.UI/Themes/Icons.axaml"

    /// Файлы из корня репозитория: имя файла → текст.
    public static func write(root: URL) throws -> [String: String] {
        func read(_ path: String) throws -> Data {
            do {
                return try Data(contentsOf: root.appendingPathComponent(path))
            } catch {
                throw SharedError("Не прочитан \(path): \(error.localizedDescription)")
            }
        }

        return try write(
            tokens: read(tokensPath), russian: read(russianPath), english: read(englishPath),
            icons: String(decoding: read(iconsPath), as: UTF8.self)
        )
    }

    public static func write(tokens: Data, russian: Data, english: Data, icons: String) throws -> [String: String] {
        [
            "Tokens.swift": try TokensWriter().write(JsonValue.parse(tokens, "design-tokens.json")),
            "Texts.swift": try TextsWriter.write(
                russian: JsonValue.parse(russian, "localization/ru.json"), english: JsonValue.parse(english, "localization/en.json")
            ),
            "Icons.swift": try IconsWriter.write(icons),
        ]
    }

    static let header = "// Сгенерировано SharedGen (ios/Core, плагин SharedResources) при сборке — руками не править и в git не хранить."
}

/// Значение JSON: словарь (порядок ключей не важен — код пишется по алфавиту), список, строка, число, логическое, null.
enum JsonValue: Decodable, Equatable {
    case object([String: JsonValue])
    case array([JsonValue])
    case string(String)
    case number(Double)
    case bool(Bool)
    case null

    init(from decoder: any Decoder) throws {
        let container = try decoder.singleValueContainer()
        if container.decodeNil() {
            self = .null
        } else if let flag = try? container.decode(Bool.self) {
            self = .bool(flag)
        } else if let number = try? container.decode(Double.self) {
            self = .number(number)
        } else if let text = try? container.decode(String.self) {
            self = .string(text)
        } else if let items = try? container.decode([JsonValue].self) {
            self = .array(items)
        } else {
            self = .object(try container.decode([String: JsonValue].self))
        }
    }

    static func parse(_ data: Data, _ name: String) throws -> JsonValue {
        do {
            return try JSONDecoder().decode(JsonValue.self, from: data)
        } catch {
            throw SharedError("\(name) — не JSON: \(error)")
        }
    }

    func object(_ path: String) throws -> [String: JsonValue] {
        guard case let .object(map) = self else {
            throw SharedError("\(path) — не словарь")
        }

        return map
    }

    func section(_ key: String, _ path: String) throws -> [String: JsonValue] {
        guard let child = try object(path)[key] else {
            throw SharedError("В \(path) нет раздела \(key)")
        }

        return try child.object("\(path).\(key)")
    }
}

/// Текст кода с отступами по вложенности, литералы и имена Swift.
final class SwiftCode {
    private var text = ""
    private var depth = 0

    var result: String { text }

    func line(_ value: String = "") {
        if !value.isEmpty {
            text += String(repeating: "    ", count: depth)
        }

        text += value + "\n"
    }

    func block(_ header: String, _ body: () throws -> Void) rethrows {
        line(header + " {")
        depth += 1
        try body()
        depth -= 1
        line("}")
    }

    /// Строка Swift в кавычках: «\» и кавычка экранируются (и «\(» не станет подстановкой), управляющие — «\u{…}».
    static func literal(_ value: String) -> String {
        var out = "\""
        for scalar in value.unicodeScalars {
            switch scalar {
            case "\\": out += "\\\\"
            case "\"": out += "\\\""
            case "\n": out += "\\n"
            case "\r": out += "\\r"
            case "\t": out += "\\t"
            default:
                if scalar.value < 0x20 || scalar.value == 0x7F {
                    out += "\\u{" + String(scalar.value, radix: 16, uppercase: true) + "}"
                } else {
                    out.unicodeScalars.append(scalar)
                }
            }
        }

        return out + "\""
    }

    /// Число токена: «16», «10.5», «-0.02».
    static func number(_ value: Double) -> String {
        if value == value.rounded(), abs(value) < 1e15 {
            return String(Int(value))
        }

        return "\(value)"
    }

    /// «desktopWindow» → «DesktopWindow», «Nav.Servers.Detail» → «NavServersDetail» — имя типа.
    static func pascal(_ name: String) throws -> String {
        let result = parts(name).map { $0.prefix(1).uppercased() + $0.dropFirst() }.joined()
        return try checked(result, name)
    }

    /// «status.connected.title» → «statusConnectedTitle», «error.PGC-SVC-001» → «errorPGCSVC001» — имя свойства.
    static func camel(_ name: String) throws -> String {
        let pieces = parts(name)
        guard let first = pieces.first else {
            throw SharedError("Пустое имя «\(name)»")
        }

        var result = first.prefix(1).lowercased() + first.dropFirst()
        for piece in pieces.dropFirst() {
            result += piece.prefix(1).uppercased() + piece.dropFirst()
        }

        return try checked(result, name)
    }

    private static func parts(_ name: String) -> [Substring] {
        name.split(whereSeparator: { $0 == "." || $0 == "-" || $0 == "_" })
    }

    private static func checked(_ result: String, _ name: String) throws -> String {
        let scalars = Array(result.unicodeScalars)
        let letter: (Unicode.Scalar) -> Bool = { ("a"..."z").contains($0) || ("A"..."Z").contains($0) }
        guard let first = scalars.first, letter(first), scalars.allSatisfy({ letter($0) || ("0"..."9").contains($0) }) else {
            throw SharedError("«\(name)» не превращается в имя Swift («\(result)»)")
        }

        return keywords.contains(result) ? "`\(result)`" : result
    }

    private static let keywords: Set<String> = [
        "associatedtype", "class", "deinit", "enum", "extension", "fileprivate", "func", "import", "init", "inout", "internal", "let",
        "open", "operator", "private", "precedencegroup", "protocol", "public", "rethrows", "static", "struct", "subscript", "typealias",
        "var", "break", "case", "catch", "continue", "default", "defer", "do", "else", "fallthrough", "for", "guard", "if", "in", "repeat",
        "return", "throw", "switch", "where", "while", "Any", "as", "await", "false", "is", "nil", "self", "Self", "super", "throws",
        "true", "try", "Type", "Protocol",
    ]
}

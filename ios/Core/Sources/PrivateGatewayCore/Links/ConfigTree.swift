/// Узел дерева конфига — JSON ([JsonTree]) или YAML ([YamlTree]): словарь и список помнят строку своего начала (с 1), список
/// JSON — еще строки начала своих элементов; число — его текст как есть (порт в vmess и sing-box бывает числом).
indirect enum ConfigNode {
    case object([String: ConfigNode], line: Int)
    case array([ConfigNode], line: Int, itemLines: [Int])
    case string(String)
    case number(String)
    case bool(Bool)
    case null

    /// Строка начала словаря или списка; у значений — nil.
    var line: Int? {
        switch self {
        case let .object(_, line), let .array(_, line, _):
            return line
        default:
            return nil
        }
    }
}

/// JSON в дерево [ConfigNode], как `JsonTree` Android: разрешены комментарии «//» и «/* */» и запятая перед закрывающей
/// скобкой — как в конфигах sing-box. Глубина — не больше [maxDepth]: текст приходит из сети.
enum JsonTree {
    static let maxDepth = 64

    /// Не JSON: строка ошибки — с 1.
    struct SyntaxError: Error {
        let line: Int
        let message: String
    }

    struct Parsed {
        let root: ConfigNode
    }

    static func parse(_ text: String) throws -> Parsed {
        var reader = Reader(text)
        try reader.skip()
        let value = try reader.value(0)
        try reader.skip()
        if !reader.end {
            throw reader.error("Лишний текст после JSON")
        }

        return Parsed(root: value)
    }

    private struct Reader {
        let text: Scalars
        var position = 0
        var line = 1

        init(_ text: String) {
            self.text = Scalars(text)
        }

        var end: Bool { position >= text.count }

        func error(_ message: String) -> SyntaxError {
            SyntaxError(line: line, message: message)
        }

        /// Пробелы и комментарии.
        mutating func skip() throws {
            while position < text.count {
                let c = text[position]
                if c == "\n" {
                    line += 1
                    position += 1
                } else if TextKit.isWhitespace(c) || c == "\u{FEFF}" {
                    position += 1
                } else if text.starts(with: "//", at: position) {
                    while position < text.count, text[position] != "\n" {
                        position += 1
                    }
                } else if text.starts(with: "/*", at: position) {
                    guard let close = text.index(ofText: "*/", from: position + 2) else {
                        throw error("Незакрытый комментарий")
                    }

                    for i in position..<close where text[i] == "\n" {
                        line += 1
                    }

                    position = close + 2
                } else {
                    return
                }
            }
        }

        mutating func value(_ depth: Int) throws -> ConfigNode {
            if depth >= JsonTree.maxDepth {
                throw error("Слишком глубокая вложенность")
            }

            if end {
                throw error("Нет значения")
            }

            let c = text[position]
            switch c {
            case "{": return try object(depth)
            case "[": return try array(depth)
            case "\"": return .string(try string())
            case "t": return try literal("true", .bool(true))
            case "f": return try literal("false", .bool(false))
            case "n": return try literal("null", .null)
            default:
                if c == "-" || ("0"..."9").contains(c) {
                    return try number()
                }

                throw error("Неожиданный символ «\(c)»")
            }
        }

        private mutating func object(_ depth: Int) throws -> ConfigNode {
            let start = line
            var result: [String: ConfigNode] = [:]
            position += 1
            while true {
                try skip()
                if end {
                    throw error("Незакрытый «{»")
                }

                if text[position] == "}" {
                    position += 1
                    return .object(result, line: start)
                }

                if text[position] != "\"" {
                    throw error("Нужно имя поля в кавычках")
                }

                let key = try string()
                try skip()
                if end || text[position] != ":" {
                    throw error("Нужно «:»")
                }

                position += 1
                try skip()
                result[key] = try value(depth + 1)
                try skip()
                if end {
                    throw error("Незакрытый «{»")
                }

                switch text[position] {
                case ",": position += 1
                case "}": continue
                default: throw error("Нужна «,» или «}»")
                }
            }
        }

        private mutating func array(_ depth: Int) throws -> ConfigNode {
            let start = line
            var result: [ConfigNode] = []
            var lines: [Int] = []
            position += 1
            while true {
                try skip()
                if end {
                    throw error("Незакрытый «[»")
                }

                if text[position] == "]" {
                    position += 1
                    return .array(result, line: start, itemLines: lines)
                }

                lines.append(line)
                result.append(try value(depth + 1))
                try skip()
                if end {
                    throw error("Незакрытый «[»")
                }

                switch text[position] {
                case ",": position += 1
                case "]": continue
                default: throw error("Нужна «,» или «]»")
                }
            }
        }

        private mutating func string() throws -> String {
            var result = String.UnicodeScalarView()
            position += 1
            while true {
                if end {
                    throw error("Незакрытая строка")
                }

                let c = text[position]
                position += 1
                switch c {
                case "\"":
                    return String(result)
                case "\n":
                    throw error("Перевод строки в строке")
                case "\\":
                    if end {
                        throw error("Незакрытая строка")
                    }

                    let e = text[position]
                    position += 1
                    switch e {
                    case "\"", "\\", "/": result.append(e)
                    case "b": result.append("\u{08}")
                    case "f": result.append("\u{0C}")
                    case "n": result.append("\n")
                    case "r": result.append("\r")
                    case "t": result.append("\t")
                    case "u": result.append(try unicode())
                    default: throw error("Неверная escape-последовательность")
                    }
                default:
                    result.append(c)
                }
            }
        }

        /// «\uXXXX» после «\u»; пара суррогатов — один символ, одинокий суррогат — «�».
        private mutating func unicode() throws -> Unicode.Scalar {
            let code = try hex4()
            if (0xD800...0xDBFF).contains(code), text.starts(with: "\\u", at: position) {
                let save = position
                position += 2
                let low = try hex4()
                if (0xDC00...0xDFFF).contains(low), let scalar = Unicode.Scalar(0x10000 + ((code - 0xD800) << 10) + (low - 0xDC00)) {
                    return scalar
                }

                position = save
            }

            return Unicode.Scalar(code) ?? "\u{FFFD}"
        }

        private mutating func hex4() throws -> UInt32 {
            guard position + 4 <= text.count, let code = UInt32(text.text(position, position + 4), radix: 16) else {
                throw error("Неверный \\u")
            }

            position += 4
            return code
        }

        private mutating func number() throws -> ConfigNode {
            let start = position
            if text[position] == "-" {
                position += 1
            }

            while position < text.count, ("0"..."9").contains(text[position]) || ".eE+-".unicodeScalars.contains(text[position]) {
                position += 1
            }

            let raw = text.text(start, position)
            if Double(raw) == nil {
                throw error("Неверное число")
            }

            return .number(raw)
        }

        private mutating func literal(_ word: String, _ value: ConfigNode) throws -> ConfigNode {
            if !text.starts(with: word, at: position) {
                throw error("Неожиданное слово")
            }

            position += word.unicodeScalars.count
            return value
        }
    }
}

/// Общее для конфигов sing-box и Clash: поля узлов дерева и сборка сервера через те же части ссылки и проверки, что у ссылок
/// ([LinkParser.fromParts]), — одинаковый сервер и одинаковые коды ошибок, откуда бы он ни пришел.
enum ConfigTree {
    /// Весь текст не разобран: одна строка с ошибкой [ImportErrors.badEncoding].
    static func broken(_ format: String, line: Int) -> ImportBatch {
        ImportBatch(format: format, lines: [ImportLine(number: max(1, line), result: LinkParser.fail(ImportErrors.badEncoding))])
    }

    /// Добавить параметр; пустое значение не пишется — как отсутствующее в ссылке.
    static func put(_ query: inout Query, _ key: String, _ value: String?) {
        if let value, !value.isEmpty {
            query[key] = value
        }
    }

    /// Сервер из полей: секреты, адрес, порт, параметры и название — как части ссылки.
    static func server(_ proto: ProxyProtocol, _ secrets: [String?], host: String?, port: String?, query: Query, name: String?) -> LinkResult {
        LinkParser.fromParts(proto, LinkParser.parts(secrets, host: host, port: port, query: query, name: name))
    }

    /// Строковое поле; число и логическое значение — их текст.
    static func text(_ node: ConfigNode?, _ key: String) -> String? {
        guard case let .object(map, _) = node else {
            return nil
        }

        return scalar(map[key])
    }

    private static func scalar(_ node: ConfigNode?) -> String? {
        switch node {
        case let .string(text): return text
        case let .number(text): return text
        case let .bool(value): return value ? "true" : "false"
        default: return nil
        }
    }

    /// Логическое поле: true в JSON, «true», «yes», «on» или «1» в YAML.
    static func flag(_ node: ConfigNode?, _ key: String) -> Bool {
        guard let text = Self.text(node, key) else {
            return false
        }

        return ["true", "yes", "on", "1"].contains(text.lowercased())
    }

    /// Вложенный словарь.
    static func object(_ node: ConfigNode?, _ key: String) -> ConfigNode? {
        guard case let .object(map, _) = node, let child = map[key], case .object(_, _) = child else {
            return nil
        }

        return child
    }

    /// Вложенный список.
    static func array(_ node: ConfigNode?, _ key: String) -> [ConfigNode]? {
        guard case let .object(map, _) = node, case let .array(items, _, _)? = map[key] else {
            return nil
        }

        return items
    }

    /// Список строк: массив или одна строка.
    static func list(_ node: ConfigNode?, _ key: String) -> [String] {
        if let items = array(node, key) {
            return items.compactMap { scalar($0) }
        }

        if let text = Self.text(node, key), !text.isEmpty {
            return [text]
        }

        return []
    }

    /// Список через запятую, как в параметрах ссылки; пустой — nil.
    static func joined(_ node: ConfigNode?, _ key: String) -> String? {
        let items = list(node, key)
        return items.isEmpty ? nil : items.joined(separator: ",")
    }

    /// Заголовок HTTP из словаря заголовков (значение — строка или список); регистр имени не важен.
    static func header(_ headers: ConfigNode?, _ name: String) -> String? {
        guard case let .object(map, _) = headers else {
            return nil
        }

        for key in map.keys.sorted() where key.lowercased() == name.lowercased() {
            if case let .array(items, _, _)? = map[key] {
                return scalar(items.first)
            }

            return scalar(map[key])
        }

        return nil
    }
}

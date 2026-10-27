import Yams

/// YAML (конфиг Clash/Mihomo) — в дерево [ConfigNode], как `YamlTree` Android и десктопа: значения — строки, «~», «null» и
/// пустые без кавычек — null; ссылки (*имя) копируют узел, слияние «<<» добавляет ключи, которых нет. Пределы глубины и числа
/// узлов: подписка приходит из сети, и вложенность или ссылки-«бомбы» не должны уронить программу. Разбирает libyaml (Yams);
/// до него текст проверяется на глубину скобок и отступов — разборщик Yams рекурсивен.
enum YamlTree {
    static let maxDepth = 64
    static let maxNodes = 200_000

    /// Отступ, глубже которого конфиг не разбирается: столько уровней вложенности конфигу Clash не нужно.
    static let maxIndent = 512

    /// Корень первого документа и строка ошибки (0 — разобрано).
    struct Parsed {
        let root: ConfigNode
        let errorLine: Int
    }

    private struct Broken: Error {
        let line: Int
    }

    static func parse(_ text: String) -> Parsed {
        if let line = tooDeep(text) {
            return Parsed(root: .null, errorLine: line)
        }

        do {
            let parser = try Parser(yaml: text)
            guard let root = try parser.nextRoot() else {
                return Parsed(root: .null, errorLine: 0)
            }

            var nodes = 0
            return Parsed(root: try convert(root, depth: 0, nodes: &nodes), errorLine: 0)
        } catch let broken as Broken {
            return Parsed(root: .null, errorLine: max(1, broken.line))
        } catch let error as YamlError {
            return Parsed(root: .null, errorLine: max(1, line(of: error)))
        } catch {
            return Parsed(root: .null, errorLine: 1)
        }
    }

    /// Незакрытый «{» или «[» — строка, где конструкция началась (как у десктопа и Android); иначе — где нашлась ошибка.
    private static func line(of error: YamlError) -> Int {
        switch error {
        case let .scanner(context, _, mark, _), let .parser(context, _, mark, _), let .composer(context, _, mark, _):
            if let context, context.text.hasPrefix("while parsing a flow") {
                return context.mark.line
            }

            return mark.line
        default:
            return 1
        }
    }

    private static func convert(_ node: Node, depth: Int, nodes: inout Int) throws -> ConfigNode {
        let line = node.mark?.line ?? 1
        nodes += 1
        if nodes > maxNodes {
            throw Broken(line: line)
        }

        switch node {
        case let .scalar(scalar):
            if scalar.style == .plain, ["", "~", "null", "Null", "NULL"].contains(scalar.string) {
                return .null
            }

            return .string(scalar.string)
        case let .mapping(mapping):
            if depth >= maxDepth {
                throw Broken(line: line)
            }

            var result: [String: ConfigNode] = [:]
            var merges: [ConfigNode] = []
            for (key, value) in mapping {
                guard case let .scalar(keyScalar) = key else {
                    throw Broken(line: key.mark?.line ?? line)
                }

                let converted = try convert(value, depth: depth + 1, nodes: &nodes)
                if keyScalar.string == "<<" {
                    // Слияние: один словарь или список словарей; явные ключи важнее.
                    switch converted {
                    case .object(_, _):
                        merges.append(converted)
                    case let .array(items, _, _):
                        merges.append(contentsOf: items.filter { if case .object(_, _) = $0 { return true } else { return false } })
                    default:
                        break
                    }

                    continue
                }

                result[keyScalar.string] = converted
            }

            for case let .object(merged, _) in merges {
                for (key, item) in merged where result[key] == nil {
                    result[key] = item
                }
            }

            return .object(result, line: line)
        case let .sequence(sequence):
            if depth >= maxDepth {
                throw Broken(line: line)
            }

            var items: [ConfigNode] = []
            var lines: [Int] = []
            for item in sequence {
                lines.append(item.mark?.line ?? line)
                items.append(try convert(item, depth: depth + 1, nodes: &nodes))
            }

            return .array(items, line: line, itemLines: lines)
        default:
            throw Broken(line: line)
        }
    }

    /// Строка, где скобки «{» и «[» вложены глубже [maxDepth] или отступ глубже [maxIndent]; nil — текст можно отдавать
    /// разборщику. Строки в кавычках и комментарии скобками не считаются.
    static func tooDeep(_ text: String) -> Int? {
        var depth = 0
        var line = 1
        var indent = 0
        var atLineStart = true
        var quote: Unicode.Scalar?
        var comment = false
        var previous: Unicode.Scalar = "\n"
        for c in text.unicodeScalars {
            if c == "\n" {
                line += 1
                indent = 0
                atLineStart = true
                comment = false
                previous = c
                continue
            }

            if atLineStart {
                if c == " " {
                    indent += 1
                    if indent > maxIndent {
                        return line
                    }

                    previous = c
                    continue
                }

                atLineStart = false
            }

            if comment {
                previous = c
                continue
            }

            if let open = quote {
                if c == open && (open == "'" || previous != "\\") {
                    quote = nil
                }
            } else if c == "\"" || c == "'", previous == "\n" || " \t[{,:-".unicodeScalars.contains(previous) {
                // Кавычка открывает строку только в начале значения: апостроф внутри слова («it's») — просто буква.
                quote = c
            } else if c == "#" && (previous == " " || previous == "\t" || previous == "\n") {
                comment = true
            } else if c == "{" || c == "[" {
                depth += 1
                if depth > maxDepth {
                    return line
                }
            } else if c == "}" || c == "]" {
                depth = max(0, depth - 1)
            }

            previous = c
        }

        return nil
    }
}

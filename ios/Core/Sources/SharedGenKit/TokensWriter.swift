import Foundation

/// Цвета CSS макета одним числом ARGB.
enum Css {
    /// «#RRGGBB» или «rgba(r, g, b, a)» → ARGB; альфа — a × 255 с округлением половины вверх в десятичной записи, как у
    /// десктопа и Android (0.70 → 179, а не 178 из-за двоичной дроби).
    static func argb(_ css: String) throws -> UInt32 {
        if css.hasPrefix("#"), css.count == 7, let rgb = UInt32(css.dropFirst(), radix: 16) {
            return 0xFF00_0000 | rgb
        }

        guard css.hasPrefix("rgba("), css.hasSuffix(")") else {
            throw SharedError("Цвет не разобран: «\(css)»")
        }

        let parts = css.dropFirst(5).dropLast().split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }
        guard parts.count == 4, let red = UInt32(parts[0]), let green = UInt32(parts[1]), let blue = UInt32(parts[2]),
              red < 256, green < 256, blue < 256, let alpha = alpha(parts[3])
        else {
            throw SharedError("Цвет не разобран: «\(css)»")
        }

        return alpha << 24 | red << 16 | green << 8 | blue
    }

    /// «0.70» → 179: целые числа вместо Double — без ошибки двоичной дроби.
    private static func alpha(_ text: String) -> UInt32? {
        let pieces = text.split(separator: ".", omittingEmptySubsequences: false)
        guard pieces.count <= 2, pieces.allSatisfy({ $0.allSatisfy(\.isASCII) && $0.allSatisfy(\.isNumber) }),
              pieces.joined().count > 0, pieces.count == 1 || pieces[1].count <= 9
        else {
            return nil
        }

        let fraction = pieces.count == 2 ? String(pieces[1]) : ""
        guard let value = UInt64(String(pieces[0]) + fraction) else {
            return nil
        }

        var scale: UInt64 = 1
        for _ in 0..<fraction.count {
            scale *= 10
        }

        let rounded = (2 * value * 255 + scale) / (2 * scale)
        return rounded <= 255 ? UInt32(rounded) : nil
    }

    /// Литерал Swift цвета.
    static func swift(_ css: String) throws -> String {
        let hex = try String(argb(css), radix: 16, uppercase: true)
        return "PgRGBA(argb: 0x" + String(repeating: "0", count: 8 - hex.count) + hex + ")"
    }
}

/// Tokens.swift из design-tokens.json — имена как у Theme.kt Android и Tokens.axaml десктопа:
///   color.bg.window → PgColor.Bg.window, state.connected.ring → PgState.Connected.ring (подпись — из текстов, не отсюда),
///   type.h1 → PgType.h1, space.card → PgSpace.card, radius.card → PgRadius.card, layout.island.height → PgLayout.Island.height,
///   shadow.card → PgShadow.card, glass.blur → PgGlass.blur;
/// PgTokens.colors и PgTokens.lengths — те же значения по именам ресурсов десктопа (PgColor.Bg.Window, PgSpace.Card), чтобы
/// тест сверил платформы.
final class TokensWriter {
    private let code = SwiftCode()
    private var colors: [(String, String)] = []
    private var lengths: [(String, String)] = []

    func write(_ tokens: JsonValue) throws -> String {
        let root = try tokens.object("design-tokens.json")
        guard case let .string(version)? = root["version"] else {
            throw SharedError("В design-tokens.json нет версии")
        }

        code.line(SharedWriter.header)
        code.line("// Источник: shared/specifications/design-tokens.json, версия \(version).")
        code.line()

        code.line("/// Цвета макета (color.*).")
        try code.block("public enum PgColor") {
            try colorTree(tokens.section("color", "design-tokens.json"), swift: "PgColor", desktop: "PgColor")
        }
        code.line()

        code.line("/// Цвета состояний защиты (state.*): кольцо кнопки, иконка, свечение, индикатор.")
        try code.block("public enum PgState") {
            for (state, value) in try sorted(tokens.section("state", "design-tokens.json")) {
                let type = try SwiftCode.pascal(state)
                try code.block("public enum \(type)") {
                    for (key, css) in try sorted(value.object("state.\(state)")) where key != "label" {
                        try color(css, name: SwiftCode.camel(key), swift: "PgState.\(type)", desktop: "PgColor.State.\(type).\(SwiftCode.pascal(key))")
                    }
                }
            }
        }
        code.line()

        code.line("/// Шрифты iOS (font.ios): системные, в приложение не встраиваются.")
        try code.block("public enum PgFont") {
            let ios = try (tokens.section("font", "design-tokens.json")["ios"] ?? .null).object("font.ios")
            let ui = try text(ios["ui"], "font.ios.ui")
            let mono = try text(ios["mono"], "font.ios.mono")
            code.line("public static let ui = \(SwiftCode.literal(ui))")
            code.line("public static let mono = \(SwiftCode.literal(mono))")
        }
        code.line()

        code.line("/// Начертания текста (type.*): Active — weightActive, Mobile — sizeMobile.")
        try code.block("public enum PgType") {
            for (key, value) in try sorted(tokens.section("type", "design-tokens.json")) {
                try textStyles(key, value.object("type.\(key)"))
            }
        }
        code.line()

        code.line("/// Отступы (space.*), пункты.")
        try code.block("public enum PgSpace") {
            try lengthTree(tokens.section("space", "design-tokens.json"), path: "space", swift: "PgSpace", desktop: "PgSpace")
        }
        code.line()

        code.line("/// Скругления (radius.*), пункты; round — круг, процент от стороны.")
        try code.block("public enum PgRadius") {
            for (key, value) in try sorted(tokens.section("radius", "design-tokens.json")) {
                if case let .string(percent) = value, percent.hasSuffix("%"), let number = Double(percent.dropLast()) {
                    let name = try SwiftCode.camel(key + "Percent")
                    code.line("public static let \(name): Double = \(SwiftCode.number(number))")
                } else {
                    try length(value, name: SwiftCode.camel(key), path: "radius.\(key)", swift: "PgRadius", desktop: "PgRadius.\(SwiftCode.pascal(key))")
                }
            }
        }
        code.line()

        code.line("/// Тени (shadow.*): слои сверху вниз.")
        try code.block("public enum PgShadow") {
            for (key, value) in try sorted(tokens.section("shadow", "design-tokens.json")) {
                guard case let .array(items) = value else {
                    throw SharedError("shadow.\(key) — не список")
                }

                let layers = try items.map { try shadow(text($0, "shadow.\(key)")) }.joined(separator: ", ")
                let name = try SwiftCode.camel(key)
                code.line("public static let \(name): [PgShadowLayer] = [\(layers)]")
            }
        }
        code.line()

        code.line("/// Стекло (glass.*): заливка, размытие, насыщенность и запасной цвет без размытия.")
        try code.block("public enum PgGlass") {
            let glass = try tokens.section("glass", "design-tokens.json")
            try color(glass["fill"] ?? .null, name: "fill", swift: "PgGlass", desktop: "PgColor.Glass.Fill")
            try length(glass["blur"] ?? .null, name: "blur", path: "glass.blur", swift: "PgGlass", desktop: "PgGlass.Blur")
            let saturate = try number(glass["saturate"], "glass.saturate")
            code.line("public static let saturate: Double = \(SwiftCode.number(saturate))")
            try color(glass["fallback"] ?? .null, name: "fallback", swift: "PgGlass", desktop: "PgColor.Glass.Fallback")
        }
        code.line()

        code.line("/// Размеры раскладки (layout.*), пункты; колонки сетки CSS — только в макете.")
        try code.block("public enum PgLayout") {
            try lengthTree(tokens.section("layout", "design-tokens.json"), path: "layout", swift: "PgLayout", desktop: "PgLayout")
        }
        code.line()

        code.line("/// Все цвета и длины по именам ресурсов десктопа (Tokens.axaml) — для сверки платформ.")
        code.block("public enum PgTokens") {
            code.line("public static let version = \(SwiftCode.literal(version))")
            code.line()
            table("colors", "PgRGBA", colors)
            code.line()
            table("lengths", "Double", lengths)
        }

        return code.result
    }

    private func table(_ name: String, _ type: String, _ entries: [(String, String)]) {
        code.line("public static let \(name): [String: \(type)] = [")
        for (key, value) in entries {
            code.line("    \(SwiftCode.literal(key)): \(value),")
        }

        code.line("]")
    }

    private func colorTree(_ node: [String: JsonValue], swift: String, desktop: String) throws {
        for (key, value) in sorted(node) {
            if case let .object(child) = value {
                let type = try SwiftCode.pascal(key)
                try code.block("public enum \(type)") {
                    try colorTree(child, swift: "\(swift).\(type)", desktop: "\(desktop).\(type)")
                }
            } else {
                try color(value, name: SwiftCode.camel(key), swift: swift, desktop: "\(desktop).\(SwiftCode.pascal(key))")
            }
        }
    }

    private func color(_ value: JsonValue, name: String, swift: String, desktop: String) throws {
        let literal = try Css.swift(text(value, desktop))
        code.line("public static let \(name) = \(literal)")
        colors.append((desktop, "\(swift).\(name)"))
    }

    /// Числа — в Double, вложенные словари — во вложенные типы, строки (колонки CSS) пропускаются.
    private func lengthTree(_ node: [String: JsonValue], path: String, swift: String, desktop: String) throws {
        for (key, value) in sorted(node) {
            switch value {
            case let .object(child):
                let type = try SwiftCode.pascal(key)
                try code.block("public enum \(type)") {
                    try lengthTree(child, path: "\(path).\(key)", swift: "\(swift).\(type)", desktop: "\(desktop).\(type)")
                }
            case .string:
                continue
            default:
                try length(value, name: SwiftCode.camel(key), path: "\(path).\(key)", swift: swift, desktop: "\(desktop).\(SwiftCode.pascal(key))")
            }
        }
    }

    private func length(_ value: JsonValue, name: String, path: String, swift: String, desktop: String) throws {
        let length = try number(value, path)
        code.line("public static let \(name): Double = \(SwiftCode.number(length))")
        lengths.append((desktop, "\(swift).\(name)"))
    }

    private func textStyles(_ key: String, _ style: [String: JsonValue]) throws {
        let name = try SwiftCode.camel(key)
        let size = try number(style["size"], "type.\(key).size")
        let weight = try number(style["weight"], "type.\(key).weight")
        try textStyle(name, style, size, weight, key)
        if let active = style["weightActive"] {
            try textStyle(name + "Active", style, size, number(active, "type.\(key).weightActive"), key)
        }

        if let mobile = style["sizeMobile"] {
            let mobileSize = try number(mobile, "type.\(key).sizeMobile")
            try textStyle(name + "Mobile", style, mobileSize, weight, key)
            if let active = style["weightActive"] {
                try textStyle(name + "MobileActive", style, mobileSize, number(active, "type.\(key).weightActive"), key)
            }
        }
    }

    private func textStyle(_ name: String, _ style: [String: JsonValue], _ size: Double, _ weight: Double, _ key: String) throws {
        var parts = ["size: \(SwiftCode.number(size))", "weight: \(SwiftCode.number(weight))"]
        if let tracking = style["tracking"] {
            let value = try number(tracking, "type.\(key).tracking")
            parts.append("tracking: \(SwiftCode.number(value))")
        }

        if let lineHeight = style["lineHeight"] {
            let value = try number(lineHeight, "type.\(key).lineHeight")
            parts.append("lineHeight: \(SwiftCode.number(value))")
        }

        if style["family"] == .string("mono") {
            parts.append("mono: true")
        }

        if style["uppercase"] == .bool(true) {
            parts.append("uppercase: true")
        }

        code.line("public static let \(name) = PgTextStyle(\(parts.joined(separator: ", ")))")
    }

    /// «0 8 24 rgba(15, 23, 42, 0.06)» → PgShadowLayer(x: 0, y: 8, blur: 24, color: …).
    private func shadow(_ css: String) throws -> String {
        guard let colorStart = css.range(of: "rgba(")?.lowerBound ?? css.range(of: "#")?.lowerBound else {
            throw SharedError("Тень не разобрана: «\(css)»")
        }

        let offsets = css[..<colorStart].split(separator: " ").compactMap { Double($0) }
        guard offsets.count == 3, css[..<colorStart].split(separator: " ").count == 3 else {
            throw SharedError("Тень не разобрана: «\(css)»")
        }

        let color = try Css.swift(String(css[colorStart...]))
        return "PgShadowLayer(x: \(SwiftCode.number(offsets[0])), y: \(SwiftCode.number(offsets[1])), blur: \(SwiftCode.number(offsets[2])), color: \(color))"
    }

    private func sorted(_ node: [String: JsonValue]) -> [(String, JsonValue)] {
        node.sorted { $0.key < $1.key }.map { ($0.key, $0.value) }
    }

    private func number(_ value: JsonValue?, _ path: String) throws -> Double {
        guard case let .number(number)? = value else {
            throw SharedError("Токен \(path) — не число")
        }

        return number
    }

    private func text(_ value: JsonValue?, _ path: String) throws -> String {
        guard case let .string(text)? = value else {
            throw SharedError("Токен \(path) — не строка")
        }

        return text
    }
}

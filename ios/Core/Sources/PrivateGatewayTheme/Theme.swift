/// Значения темы без SwiftUI — их генерирует плагин SharedResources из shared/specifications при сборке (`PgColor`, `PgState`,
/// `PgType`, `PgSpace`, `PgRadius`, `PgShadow`, `PgGlass`, `PgLayout`, `PgIcons`, тексты `PgTexts`), а цвет, шрифт и контур
/// SwiftUI из них собирает приложение. Без SwiftUI модуль собирается и проверяется и на Linux: те же значения, что у десктопа.

/// Цвет одним числом ARGB — как в Tokens.axaml десктопа («#AARRGGBB») и Theme.kt Android.
public struct PgRGBA: Sendable, Hashable, CustomStringConvertible {
    public let argb: UInt32

    public init(argb: UInt32) {
        self.argb = argb
    }

    public var alpha: Double { Double(argb >> 24 & 0xFF) / 255 }
    public var red: Double { Double(argb >> 16 & 0xFF) / 255 }
    public var green: Double { Double(argb >> 8 & 0xFF) / 255 }
    public var blue: Double { Double(argb & 0xFF) / 255 }

    /// «#AARRGGBB» — как в Tokens.axaml.
    public var description: String {
        let hex = String(argb, radix: 16, uppercase: true)
        return "#" + String(repeating: "0", count: 8 - hex.count) + hex
    }
}

/// Начертание текста (type.*): размер в пунктах, насыщенность 100–900, трекинг и высота строки — доли размера (em).
public struct PgTextStyle: Sendable, Hashable {
    public let size: Double
    public let weight: Int
    public let tracking: Double?
    public let lineHeight: Double?
    public let mono: Bool
    public let uppercase: Bool

    public init(size: Double, weight: Int, tracking: Double? = nil, lineHeight: Double? = nil, mono: Bool = false, uppercase: Bool = false) {
        self.size = size
        self.weight = weight
        self.tracking = tracking
        self.lineHeight = lineHeight
        self.mono = mono
        self.uppercase = uppercase
    }
}

/// Слой тени (shadow.*): смещение, размытие и цвет — как box-shadow макета.
public struct PgShadowLayer: Sendable, Hashable {
    public let x: Double
    public let y: Double
    public let blur: Double
    public let color: PgRGBA

    public init(x: Double, y: Double, blur: Double, color: PgRGBA) {
        self.x = x
        self.y = y
        self.blur = blur
        self.color = color
    }
}

/// Тексты интерфейса RU и EN (shared/specifications/localization): свойство на каждый ключ — `status.connected.title` →
/// `statusConnectedTitle`, с подстановками «{0}», «{1}» — функция с аргументами в том же порядке.
public struct PgTexts: Sendable {
    public enum Language: String, CaseIterable, Sendable {
        case russian = "ru"
        case english = "en"
    }

    public let language: Language

    public init(_ language: Language) {
        self.language = language
    }

    /// Язык по списку предпочитаемых языков (`Locale.preferredLanguages`): первый из них русский или английский, иначе
    /// английский — как у Android (values — EN, values-ru — RU).
    public static func language(for preferred: [String]) -> Language {
        for tag in preferred {
            let code = tag.lowercased().split(whereSeparator: { $0 == "-" || $0 == "_" }).first.map(String.init) ?? ""
            if let language = Language(rawValue: code) {
                return language
            }
        }

        return .english
    }

    /// Число текстов — одинаковое в обоих языках.
    public static var count: Int { keys.count }

    /// Текст по ключу — для ключей, которые собираются на ходу (код ошибки, состояние); нет такого ключа — nil.
    public func text(key: String) -> String? {
        Self.index[key].map { line($0) }
    }

    /// Текст по ключу с подстановками «{0}», «{1}» по порядку — для ключей, которые собираются на ходу (формы чисел, причина
    /// отказа); нет такого ключа — nil.
    public func text(key: String, _ arguments: [String]) -> String? {
        text(key: key).map { Self.substitute($0, arguments) }
    }

    private static let index: [String: Int] = {
        var index: [String: Int] = [:]
        for (number, key) in keys.enumerated() {
            index[key] = number
        }

        return index
    }()

    func line(_ number: Int) -> String {
        language == .russian ? Self.russian[number] : Self.english[number]
    }

    /// Текст с подстановками: «{N}» → N-й аргумент; номера без аргумента остаются как есть. Подставленное не разбирается
    /// повторно — «{1}» в первом аргументе так и останется текстом.
    func format(_ number: Int, _ arguments: [any CustomStringConvertible]) -> String {
        Self.substitute(line(number), arguments.map { $0.description })
    }

    static func substitute(_ text: String, _ arguments: [String]) -> String {
        let scalars = Array(text.unicodeScalars)
        var result = String.UnicodeScalarView()
        var i = 0
        while i < scalars.count {
            if scalars[i] == "{" {
                var j = i + 1
                var digits = ""
                while j < scalars.count, ("0"..."9").contains(scalars[j]) {
                    digits.unicodeScalars.append(scalars[j])
                    j += 1
                }

                if !digits.isEmpty, j < scalars.count, scalars[j] == "}", let n = Int(digits), n < arguments.count {
                    result.append(contentsOf: arguments[n].unicodeScalars)
                    i = j + 1
                    continue
                }
            }

            result.append(scalars[i])
            i += 1
        }

        return String(result)
    }
}

/// Текст как массив скаляров Unicode с целыми индексами: разбор переносится с Kotlin (`android/core`) один к одному, а «\r\n»
/// и флаги стран не склеиваются в один символ, как у `Character`.
struct Scalars {
    let items: [Unicode.Scalar]

    init(_ text: String) {
        items = Array(text.unicodeScalars)
    }

    var count: Int { items.count }

    var isEmpty: Bool { items.isEmpty }

    subscript(index: Int) -> Unicode.Scalar { items[index] }

    func index(of scalar: Unicode.Scalar, from start: Int = 0) -> Int? {
        var i = start
        while i < items.count {
            if items[i] == scalar {
                return i
            }

            i += 1
        }

        return nil
    }

    func lastIndex(of scalar: Unicode.Scalar) -> Int? {
        var i = items.count - 1
        while i >= 0 {
            if items[i] == scalar {
                return i
            }

            i -= 1
        }

        return nil
    }

    /// Начало [text] с позиции [start] или дальше.
    func index(ofText text: String, from start: Int = 0) -> Int? {
        let needle = Array(text.unicodeScalars)
        guard !needle.isEmpty, needle.count <= items.count else {
            return needle.isEmpty ? start : nil
        }

        var i = start
        while i + needle.count <= items.count {
            if starts(with: needle, at: i) {
                return i
            }

            i += 1
        }

        return nil
    }

    func starts(with text: String, at position: Int) -> Bool {
        starts(with: Array(text.unicodeScalars), at: position)
    }

    private func starts(with needle: [Unicode.Scalar], at position: Int) -> Bool {
        guard position >= 0, position + needle.count <= items.count else {
            return false
        }

        for (offset, scalar) in needle.enumerated() where items[position + offset] != scalar {
            return false
        }

        return true
    }

    /// Текст с [start] до [end] (не включая).
    func text(_ start: Int, _ end: Int) -> String {
        guard start < end else {
            return ""
        }

        var view = String.UnicodeScalarView()
        view.append(contentsOf: items[start..<end])
        return String(view)
    }

    func text(from start: Int) -> String {
        text(start, items.count)
    }
}

/// Мелочи текста, как у Kotlin: пробельные символы — Unicode White_Space.
enum TextKit {
    static func isWhitespace(_ scalar: Unicode.Scalar) -> Bool {
        scalar.properties.isWhitespace
    }

    /// Без пробельных символов по краям — как `trim()` Kotlin.
    static func trim(_ text: String) -> String {
        let scalars = Array(text.unicodeScalars)
        var start = 0
        var end = scalars.count
        while start < end, isWhitespace(scalars[start]) {
            start += 1
        }

        while end > start, isWhitespace(scalars[end - 1]) {
            end -= 1
        }

        var view = String.UnicodeScalarView()
        view.append(contentsOf: scalars[start..<end])
        return String(view)
    }

    /// Без пробельных символов в начале.
    static func trimStart(_ text: String) -> String {
        let scalars = Array(text.unicodeScalars)
        var start = 0
        while start < scalars.count, isWhitespace(scalars[start]) {
            start += 1
        }

        var view = String.UnicodeScalarView()
        view.append(contentsOf: scalars[start...])
        return String(view)
    }

    /// Строки по «\n» — по скалярам: «\r\n» в Swift — один символ, и `split` по «\n» его бы не разрезал.
    static func lines(_ text: String) -> [String] {
        text.unicodeScalars.split(separator: "\n", omittingEmptySubsequences: false).map { String(Substring($0)) }
    }

    /// Строгий UTF-8: неверные байты — nil.
    static func utf8(_ bytes: [UInt8]) -> String? {
        var result = String.UnicodeScalarView()
        var iterator = bytes.makeIterator()
        var decoder = UTF8()
        while true {
            switch decoder.decode(&iterator) {
            case let .scalarValue(scalar):
                result.append(scalar)
            case .emptyInput:
                return String(result)
            case .error:
                return nil
            }
        }
    }
}

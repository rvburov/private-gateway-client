import Foundation

/// Точка контура в единицах иконки (24×24).
public struct PgPoint: Sendable, Hashable {
    public var x: Double
    public var y: Double

    public init(_ x: Double, _ y: Double) {
        self.x = x
        self.y = y
    }
}

/// Шаг контура в абсолютных координатах — то, что умеет Path SwiftUI: дуги SVG уже переведены в кривые Безье.
public enum PgPathCommand: Sendable, Hashable {
    case move(PgPoint)
    case line(PgPoint)
    case cubic(PgPoint, PgPoint, PgPoint)
    case quad(PgPoint, PgPoint)
    case close
}

/// Данные контура SVG (`d`) — как у иконок Icons.axaml (`PgIcons`): M, L, H, V, C, S, Q, T, A, Z и их строчные
/// (относительные) варианты, числа без разделителей («.33.5», «1-2»), флаги дуги слитно («011»). Android рисует ту же строку
/// своим разборщиком, у SwiftUI его нет.
public enum SvgPath {
    public struct SyntaxError: Error, Equatable {
        public let position: Int
        public let message: String
    }

    public static func parse(_ data: String) throws -> [PgPathCommand] {
        var reader = Reader(Array(data.utf8))
        return try reader.commands()
    }

    private struct Reader {
        let text: [UInt8]
        var position = 0
        var result: [PgPathCommand] = []
        var current = PgPoint(0, 0)
        var start = PgPoint(0, 0)

        init(_ text: [UInt8]) {
            self.text = text
        }

        mutating func commands() throws -> [PgPathCommand] {
            var command: UInt8?
            // Вторая точка управления прошлой кривой — для отражения в S и T.
            var lastCubic: PgPoint?
            var lastQuad: PgPoint?
            while true {
                skip()
                if position >= text.count {
                    return result
                }

                let c = text[position]
                if Self.isLetter(c) {
                    command = c
                    position += 1
                } else if command == nil {
                    throw error("Контур начинается не с команды")
                } else if command == UInt8(ascii: "Z") || command == UInt8(ascii: "z") {
                    throw error("Число после Z")
                }

                guard let letter = command else {
                    throw error("Нет команды")
                }

                let relative = letter >= UInt8(ascii: "a")
                var cubic: PgPoint?
                var quad: PgPoint?
                switch letter | 0x20 {
                case UInt8(ascii: "m"):
                    let point = try self.point(relative)
                    result.append(.move(point))
                    current = point
                    start = point
                    // Пары после M — отрезки L.
                    command = relative ? UInt8(ascii: "l") : UInt8(ascii: "L")
                case UInt8(ascii: "l"):
                    let point = try self.point(relative)
                    result.append(.line(point))
                    current = point
                case UInt8(ascii: "h"):
                    let x = try number()
                    current = PgPoint(relative ? current.x + x : x, current.y)
                    result.append(.line(current))
                case UInt8(ascii: "v"):
                    let y = try number()
                    current = PgPoint(current.x, relative ? current.y + y : y)
                    result.append(.line(current))
                case UInt8(ascii: "c"):
                    let first = try point(relative)
                    let second = try point(relative)
                    let end = try point(relative)
                    result.append(.cubic(first, second, end))
                    current = end
                    cubic = second
                case UInt8(ascii: "s"):
                    let first = lastCubic.map { reflect($0) } ?? current
                    let second = try point(relative)
                    let end = try point(relative)
                    result.append(.cubic(first, second, end))
                    current = end
                    cubic = second
                case UInt8(ascii: "q"):
                    let control = try point(relative)
                    let end = try point(relative)
                    result.append(.quad(control, end))
                    current = end
                    quad = control
                case UInt8(ascii: "t"):
                    let control = lastQuad.map { reflect($0) } ?? current
                    let end = try point(relative)
                    result.append(.quad(control, end))
                    current = end
                    quad = control
                case UInt8(ascii: "a"):
                    let rx = try number()
                    let ry = try number()
                    let rotation = try number()
                    let large = try flag()
                    let sweep = try flag()
                    let end = try point(relative)
                    result.append(contentsOf: Self.arc(from: current, rx: rx, ry: ry, rotation: rotation, large: large, sweep: sweep, to: end))
                    current = end
                case UInt8(ascii: "z"):
                    result.append(.close)
                    current = start
                default:
                    throw error("Неизвестная команда «\(Character(Unicode.Scalar(letter)))»")
                }

                lastCubic = cubic
                lastQuad = quad
            }
        }

        private func reflect(_ control: PgPoint) -> PgPoint {
            PgPoint(2 * current.x - control.x, 2 * current.y - control.y)
        }

        private mutating func point(_ relative: Bool) throws -> PgPoint {
            let x = try number()
            let y = try number()
            return relative ? PgPoint(current.x + x, current.y + y) : PgPoint(x, y)
        }

        private mutating func skip() {
            while position < text.count, [0x20, 0x09, 0x0A, 0x0D, 0x2C].contains(text[position]) {
                position += 1
            }
        }

        private mutating func number() throws -> Double {
            skip()
            let begin = position
            if position < text.count, text[position] == UInt8(ascii: "+") || text[position] == UInt8(ascii: "-") {
                position += 1
            }

            var digits = false
            while position < text.count, Self.isDigit(text[position]) {
                position += 1
                digits = true
            }

            // Вторая точка начинает следующее число: «.33.5» — это 0.33 и 0.5.
            if position < text.count, text[position] == UInt8(ascii: ".") {
                position += 1
                while position < text.count, Self.isDigit(text[position]) {
                    position += 1
                    digits = true
                }
            }

            if !digits {
                position = begin
                throw error("Нужно число")
            }

            if position < text.count, text[position] == UInt8(ascii: "e") || text[position] == UInt8(ascii: "E") {
                var next = position + 1
                if next < text.count, text[next] == UInt8(ascii: "+") || text[next] == UInt8(ascii: "-") {
                    next += 1
                }

                if next < text.count, Self.isDigit(text[next]) {
                    position = next
                    while position < text.count, Self.isDigit(text[position]) {
                        position += 1
                    }
                }
            }

            guard let value = Double(String(decoding: text[begin..<position], as: UTF8.self)) else {
                position = begin
                throw error("Нужно число")
            }

            return value
        }

        /// Флаг дуги — одна цифра 0 или 1, разделитель после нее не обязателен.
        private mutating func flag() throws -> Bool {
            skip()
            guard position < text.count, text[position] == UInt8(ascii: "0") || text[position] == UInt8(ascii: "1") else {
                throw error("Нужен флаг дуги 0 или 1")
            }

            position += 1
            return text[position - 1] == UInt8(ascii: "1")
        }

        private func error(_ message: String) -> SyntaxError {
            SyntaxError(position: position, message: message)
        }

        private static func isDigit(_ c: UInt8) -> Bool {
            c >= UInt8(ascii: "0") && c <= UInt8(ascii: "9")
        }

        private static func isLetter(_ c: UInt8) -> Bool {
            (c >= UInt8(ascii: "A") && c <= UInt8(ascii: "Z")) || (c >= UInt8(ascii: "a") && c <= UInt8(ascii: "z"))
        }

        /// Дуга эллипса SVG → кривые Безье, не больше четверти оборота каждая (SVG 1.1, приложение F.6.5): из концов дуги —
        /// центр и углы, затем на каждую часть — кубическая кривая с точками управления на касательных.
        static func arc(
            from begin: PgPoint, rx radiusX: Double, ry radiusY: Double, rotation: Double, large: Bool, sweep: Bool, to end: PgPoint
        ) -> [PgPathCommand] {
            if begin == end {
                return []
            }

            var rx = abs(radiusX)
            var ry = abs(radiusY)
            if rx == 0 || ry == 0 {
                return [.line(end)]
            }

            let phi = rotation * .pi / 180
            let cosPhi = cos(phi)
            let sinPhi = sin(phi)
            let dx = (begin.x - end.x) / 2
            let dy = (begin.y - end.y) / 2
            let x1 = cosPhi * dx + sinPhi * dy
            let y1 = -sinPhi * dx + cosPhi * dy

            // Радиусы малы для расстояния между концами — увеличить до наименьших подходящих.
            let lambda = x1 * x1 / (rx * rx) + y1 * y1 / (ry * ry)
            if lambda > 1 {
                rx *= lambda.squareRoot()
                ry *= lambda.squareRoot()
            }

            let numerator = rx * rx * ry * ry - rx * rx * y1 * y1 - ry * ry * x1 * x1
            let denominator = rx * rx * y1 * y1 + ry * ry * x1 * x1
            let coefficient = (large != sweep ? 1.0 : -1.0) * max(0, numerator / denominator).squareRoot()
            let cxPrime = coefficient * rx * y1 / ry
            let cyPrime = -coefficient * ry * x1 / rx
            let cx = cosPhi * cxPrime - sinPhi * cyPrime + (begin.x + end.x) / 2
            let cy = sinPhi * cxPrime + cosPhi * cyPrime + (begin.y + end.y) / 2

            let ux = (x1 - cxPrime) / rx
            let uy = (y1 - cyPrime) / ry
            let vx = (-x1 - cxPrime) / rx
            let vy = (-y1 - cyPrime) / ry
            let theta = angle(1, 0, ux, uy)
            var delta = angle(ux, uy, vx, vy)
            if !sweep, delta > 0 {
                delta -= 2 * .pi
            } else if sweep, delta < 0 {
                delta += 2 * .pi
            }

            let parts = max(1, Int((abs(delta) / (.pi / 2) - 1e-9).rounded(.up)))
            let step = delta / Double(parts)
            let k = 4.0 / 3.0 * tan(step / 4)

            func point(_ t: Double) -> PgPoint {
                let x = rx * cos(t)
                let y = ry * sin(t)
                return PgPoint(cosPhi * x - sinPhi * y + cx, sinPhi * x + cosPhi * y + cy)
            }

            func tangent(_ t: Double) -> PgPoint {
                let x = -rx * sin(t)
                let y = ry * cos(t)
                return PgPoint(cosPhi * x - sinPhi * y, sinPhi * x + cosPhi * y)
            }

            var result: [PgPathCommand] = []
            for part in 0..<parts {
                let a1 = theta + Double(part) * step
                let a2 = a1 + step
                let p1 = point(a1)
                // Последняя часть кончается ровно в конце дуги, без накопленной погрешности.
                let p2 = part == parts - 1 ? end : point(a2)
                let d1 = tangent(a1)
                let d2 = tangent(a2)
                result.append(.cubic(PgPoint(p1.x + k * d1.x, p1.y + k * d1.y), PgPoint(p2.x - k * d2.x, p2.y - k * d2.y), p2))
            }

            return result
        }

        private static func angle(_ ux: Double, _ uy: Double, _ vx: Double, _ vy: Double) -> Double {
            atan2(ux * vy - uy * vx, ux * vx + uy * vy)
        }
    }
}

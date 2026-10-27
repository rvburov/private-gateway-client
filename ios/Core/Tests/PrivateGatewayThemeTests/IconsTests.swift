import Foundation
import XCTest
@testable import PrivateGatewayTheme

/// Иконки — все из Icons.axaml десктопа, и каждая превращается в контур: дуги SVG — в кривые Безье на той же окружности.
final class IconsTests: XCTestCase {
    func testEveryDesktopIconIsHereAndParses() throws {
        let xaml = try String(contentsOf: TokensTests.root.appendingPathComponent("desktop/src/Client.UI/Themes/Icons.axaml"), encoding: .utf8)
        XCTAssertEqual(PgIcons.all.count, xaml.components(separatedBy: "<StreamGeometry ").count - 1)
        XCTAssertEqual(PgIcons.all["Nav.Home"], PgIcons.navHome)
        XCTAssertEqual(PgIcons.all["Shield"], "M12 2.8l7.5 3v5.4c0 4.7-3.1 8.6-7.5 10-4.4-1.4-7.5-5.3-7.5-10V5.8z")

        for (name, data) in PgIcons.all {
            let commands = try SvgPath.parse(data)
            XCTAssertFalse(commands.isEmpty, name)
            guard case .move = commands.first else {
                return XCTFail("\(name): контур не начинается с M")
            }

            // Концы отрезков — внутри поля 24×24.
            for command in commands {
                let end: PgPoint?
                switch command {
                case let .move(point), let .line(point), let .cubic(_, _, point), let .quad(_, point):
                    end = point
                case .close:
                    end = nil
                }

                if let end {
                    XCTAssertTrue((0...24).contains(end.x) && (0...24).contains(end.y), "\(name): \(end)")
                }
            }
        }
    }

    func testArcBecomesCurvesOnTheSameCircle() throws {
        // Две полуокружности радиуса 3 вокруг (12, 12) — по две кривые на каждую.
        let commands = try SvgPath.parse("M12 9a3 3 0 1 0 0 6a3 3 0 1 0 0-6z")
        XCTAssertEqual(commands.count, 6)
        XCTAssertEqual(commands.last, .close)

        var current = PgPoint(12, 9)
        for command in commands {
            guard case let .cubic(first, second, end) = command else {
                continue
            }

            for step in 0...10 {
                let t = Double(step) / 10
                let point = Self.cubic(current, first, second, end, t)
                XCTAssertEqual(((point.x - 12) * (point.x - 12) + (point.y - 12) * (point.y - 12)).squareRoot(), 3, accuracy: 0.002)
            }

            current = end
        }

        XCTAssertEqual(current, PgPoint(12, 9), "контур замкнулся точно")
    }

    func testQuarterArcHasControlPointsOnTangents() throws {
        let commands = try SvgPath.parse("M4 5.5A1.5 1.5 0 0 1 5.5 4")
        XCTAssertEqual(commands.count, 2)
        guard case let .cubic(first, second, end) = commands[1] else {
            return XCTFail("\(commands)")
        }

        // Четверть окружности радиуса 1,5 с центром (5,5; 5,5): точки управления — на касательных, на 4/3 · tg(π/8) радиуса.
        let k = 1.5 * 4 / 3 * tan(Double.pi / 8)
        XCTAssertEqual(first.x, 4, accuracy: 1e-9)
        XCTAssertEqual(first.y, 5.5 - k, accuracy: 1e-6)
        XCTAssertEqual(second.x, 5.5 - k, accuracy: 1e-6)
        XCTAssertEqual(second.y, 4, accuracy: 1e-9)
        XCTAssertEqual(end, PgPoint(5.5, 4))
    }

    func testCompactNumbersRelativeCommandsAndImplicitLines() throws {
        XCTAssertEqual(try SvgPath.parse("M1 2 3 4"), [.move(PgPoint(1, 2)), .line(PgPoint(3, 4))])
        XCTAssertEqual(try SvgPath.parse("m1 2 3 4"), [.move(PgPoint(1, 2)), .line(PgPoint(4, 6))])
        XCTAssertEqual(try SvgPath.parse("M1 1H5V5Zl1 1"), [
            .move(PgPoint(1, 1)), .line(PgPoint(5, 1)), .line(PgPoint(5, 5)), .close, .line(PgPoint(2, 2)),
        ])
        XCTAssertEqual(try SvgPath.parse("M0 0h.5.5v-1-1e1"), [
            .move(PgPoint(0, 0)), .line(PgPoint(0.5, 0)), .line(PgPoint(1, 0)), .line(PgPoint(1, -1)), .line(PgPoint(1, -11)),
        ])
        // S отражает вторую точку управления прошлой кривой, T — точку прошлой квадратичной.
        XCTAssertEqual(try SvgPath.parse("M0 0C0 1 1 1 1 0S2-1 2 0"), [
            .move(PgPoint(0, 0)), .cubic(PgPoint(0, 1), PgPoint(1, 1), PgPoint(1, 0)), .cubic(PgPoint(1, -1), PgPoint(2, -1), PgPoint(2, 0)),
        ])
        XCTAssertEqual(try SvgPath.parse("M0 0Q1 1 2 0T4 0"), [
            .move(PgPoint(0, 0)), .quad(PgPoint(1, 1), PgPoint(2, 0)), .quad(PgPoint(3, -1), PgPoint(4, 0)),
        ])
        // Флаги дуги слитно: «a1 1 0 011 1» — большая 0, направление 1, конец (1, 1).
        let arc = try SvgPath.parse("M0 0a1 1 0 011 1")
        guard case let .cubic(_, _, end)? = arc.last else {
            return XCTFail("\(arc)")
        }

        XCTAssertEqual(end, PgPoint(1, 1))
    }

    func testBrokenPathIsAnError() {
        for data in ["1 2", "M1", "M0 0Z 1", "M0 0X1", "M0 0a1 1 0 2 1 1 1"] {
            XCTAssertThrowsError(try SvgPath.parse(data), data)
        }
    }

    private static func cubic(_ p0: PgPoint, _ p1: PgPoint, _ p2: PgPoint, _ p3: PgPoint, _ t: Double) -> PgPoint {
        let m = 1 - t
        let a = m * m * m
        let b = 3 * m * m * t
        let c = 3 * m * t * t
        let d = t * t * t
        return PgPoint(a * p0.x + b * p1.x + c * p2.x + d * p3.x, a * p0.y + b * p1.y + c * p2.y + d * p3.y)
    }
}

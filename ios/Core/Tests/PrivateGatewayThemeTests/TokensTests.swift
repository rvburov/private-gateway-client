import Foundation
import XCTest
@testable import PrivateGatewayTheme

/// Тема iOS — та же, что у десктопа: каждый цвет и каждая длина Tokens.axaml (его генерирует scripts/gen-tokens.cs из того же
/// design-tokens.json) есть в PgTokens с тем же значением, и наоборот.
final class TokensTests: XCTestCase {
    /// ios/Core/Tests/PrivateGatewayThemeTests/этот файл → корень репозитория.
    static let root: URL = {
        var url = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 {
            url.deleteLastPathComponent()
        }

        return url
    }()

    func testColorsAreTheSameAsOnTheDesktop() throws {
        let desktop = try Self.desktop(tags: ["Color"]).filter { $0.key.hasPrefix("PgColor.") }
        XCTAssertGreaterThan(desktop.count, 50, "цвета десктопа найдены")
        XCTAssertEqual(Set(PgTokens.colors.keys), Set(desktop.keys))
        for (key, value) in desktop {
            XCTAssertEqual(PgTokens.colors[key]?.description, value, key)
        }
    }

    func testLengthsAreTheSameAsOnTheDesktop() throws {
        let prefixes = ["PgSpace.", "PgRadius.", "PgLayout.", "PgGlass."]
        let desktop = try Self.desktop(tags: ["x:Double", "CornerRadius"]).filter { entry in prefixes.contains { entry.key.hasPrefix($0) } }
        XCTAssertGreaterThan(desktop.count, 25, "длины десктопа найдены")
        XCTAssertEqual(Set(PgTokens.lengths.keys), Set(desktop.keys))
        for (key, value) in desktop {
            XCTAssertEqual(PgTokens.lengths[key], Double(value), key)
        }
    }

    func testNamedTokensKeepTheirValues() {
        XCTAssertEqual(PgColor.Bg.window, PgRGBA(argb: 0xFFEE_F2F8))
        // 0.70 × 255 = 178.5 → 179 (B3), как у десктопа и Android, а не 178 из двоичной дроби.
        XCTAssertEqual(PgColor.Bg.glass, PgRGBA(argb: 0xB3F6_F8FC))
        XCTAssertEqual(PgState.Connected.ring, PgRGBA(argb: 0xFF16_A34A))
        XCTAssertEqual(PgSpace.card, 16)
        XCTAssertEqual(PgRadius.roundPercent, 50)
        XCTAssertEqual(PgLayout.Island.height, 68)
        XCTAssertEqual(PgLayout.PowerButton.size, 80)
        XCTAssertEqual(PgGlass.saturate, 1.7)
        XCTAssertEqual(PgFont.ui, "SF Pro")
        XCTAssertEqual(PgFont.mono, "SF Mono")
    }

    func testTextStylesKeepSizeWeightAndVariants() {
        XCTAssertEqual(PgType.body, PgTextStyle(size: 14, weight: 400, lineHeight: 1.45))
        XCTAssertEqual(PgType.display, PgTextStyle(size: 28, weight: 700, tracking: -0.02))
        XCTAssertEqual(PgType.overline, PgTextStyle(size: 11, weight: 600, tracking: 0.06, uppercase: true))
        XCTAssertEqual(PgType.mono, PgTextStyle(size: 13, weight: 500, mono: true))
        XCTAssertEqual(PgType.nav, PgTextStyle(size: 11.5, weight: 500))
        XCTAssertEqual(PgType.navActive, PgTextStyle(size: 11.5, weight: 600))
        XCTAssertEqual(PgType.navMobile, PgTextStyle(size: 10.5, weight: 500))
        XCTAssertEqual(PgType.navMobileActive, PgTextStyle(size: 10.5, weight: 600))
    }

    func testShadowsKeepLayersInOrder() {
        // Как у десктопа: «0 1 2 0 #0A0F172A, 0 8 24 0 #0F0F172A».
        XCTAssertEqual(
            PgShadow.card,
            [
                PgShadowLayer(x: 0, y: 1, blur: 2, color: PgRGBA(argb: 0x0A0F_172A)),
                PgShadowLayer(x: 0, y: 8, blur: 24, color: PgRGBA(argb: 0x0F0F_172A)),
            ]
        )
        XCTAssertEqual(PgShadow.sheet, [PgShadowLayer(x: 0, y: -12, blur: 40, color: PgRGBA(argb: 0x330F_172A))])
    }

    func testColorChannelsAndText() {
        let color = PgRGBA(argb: 0x8020_40FF)
        XCTAssertEqual(color.alpha, 128.0 / 255, accuracy: 1e-9)
        XCTAssertEqual(color.red, 32.0 / 255, accuracy: 1e-9)
        XCTAssertEqual(color.green, 64.0 / 255, accuracy: 1e-9)
        XCTAssertEqual(color.blue, 1)
        XCTAssertEqual(color.description, "#802040FF")
        XCTAssertEqual(PgRGBA(argb: 0x0A0F_172A).description, "#0A0F172A")
    }

    /// Ресурсы Tokens.axaml: `<Тег x:Key="Имя">значение</Тег>` → имя → значение.
    static func desktop(tags: [String]) throws -> [String: String] {
        let text = try String(contentsOf: root.appendingPathComponent("desktop/src/Client.UI/Themes/Tokens.axaml"), encoding: .utf8)
        var result: [String: String] = [:]
        for tag in tags {
            for piece in text.components(separatedBy: "<\(tag) x:Key=\"").dropFirst() {
                guard let keyEnd = piece.range(of: "\">"), let valueEnd = piece.range(of: "</\(tag)>") else {
                    continue
                }

                result[String(piece[..<keyEnd.lowerBound])] = String(piece[keyEnd.upperBound..<valueEnd.lowerBound])
            }
        }

        return result
    }
}

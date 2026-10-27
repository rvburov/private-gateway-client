import Foundation
import XCTest
@testable import SharedGenKit

/// Генератор темы и текстов: цвета CSS, литералы и имена Swift, отказ на несовпадающих текстах и иконках.
final class SharedWriterTests: XCTestCase {
    func testCssColorsBecomeArgbLikeOnTheDesktop() throws {
        XCTAssertEqual(try Css.argb("#2563EB"), 0xFF25_63EB)
        XCTAssertEqual(try Css.argb("rgba(246, 248, 252, 0.70)"), 0xB3F6_F8FC)
        XCTAssertEqual(try Css.argb("rgba(15, 23, 42, 0.03)"), 0x080F_172A)
        XCTAssertEqual(try Css.argb("rgba(0,0,0,1)"), 0xFF00_0000)
        XCTAssertEqual(try Css.argb("rgba(0, 0, 0, .5)"), 0x8000_0000)
        for css in ["red", "#12345", "#GG0000", "rgba(1, 2, 3)", "rgba(256, 0, 0, 1)", "rgba(0, 0, 0, 1.5)", "rgba(0, 0, 0, -1)"] {
            XCTAssertThrowsError(try Css.argb(css), css)
        }
    }

    func testSwiftLiteralsAndNames() throws {
        XCTAssertEqual(SwiftCode.literal("a\"b\\c\n\\(x)\u{1}"), #""a\"b\\c\n\\(x)\u{1}""#)
        XCTAssertEqual(SwiftCode.number(16), "16")
        XCTAssertEqual(SwiftCode.number(10.5), "10.5")
        XCTAssertEqual(SwiftCode.number(-0.02), "-0.02")
        XCTAssertEqual(try SwiftCode.camel("status.connected.title"), "statusConnectedTitle")
        XCTAssertEqual(try SwiftCode.camel("error.PGC-SVC-001"), "errorPGCSVC001")
        XCTAssertEqual(try SwiftCode.camel("check.dns.lan_dns"), "checkDnsLanDns")
        XCTAssertEqual(try SwiftCode.camel("EyeOff"), "eyeOff")
        XCTAssertEqual(try SwiftCode.camel("default"), "`default`")
        XCTAssertEqual(try SwiftCode.pascal("desktopWindow"), "DesktopWindow")
        XCTAssertThrowsError(try SwiftCode.camel("1st.key"))
        XCTAssertThrowsError(try SwiftCode.camel("key.with space"))
    }

    func testTextsBecomePropertiesAndFunctions() throws {
        let code = try TextsWriter.write(
            russian: json(#"{"a.title": "Заголовок \"в кавычках\"", "a.count": "{1} из {0}"}"#),
            english: json(#"{"a.title": "Title", "a.count": "{1} of {0}"}"#)
        )

        XCTAssertTrue(code.contains(#"public var aTitle: String { line(1) }"#), code)
        XCTAssertTrue(
            code.contains(#"public func aCount(_ p0: any CustomStringConvertible, _ p1: any CustomStringConvertible) -> String { format(0, [p0, p1]) }"#),
            code
        )
        XCTAssertTrue(code.contains(#""Заголовок \"в кавычках\"","#), code)
    }

    func testTextsThatDoNotMatchAreRefused() {
        let cases = [
            (#"{"a.b": "x"}"#, #"{"a.c": "x"}"#, "разные ключи"),
            (#"{"a.b": "{0}"}"#, #"{"a.b": "x"}"#, "разные подстановки"),
            (#"{"a.b_c": "x", "a.b.c": "y"}"#, #"{"a.b_c": "x", "a.b.c": "y"}"#, "одно имя у двух ключей"),
            (#"{"text": "x"}"#, #"{"text": "x"}"#, "имя занято у PgTexts"),
            (#"{"a.b": 1}"#, #"{"a.b": 1}"#, "не строка"),
        ]
        for (russian, english, reason) in cases {
            XCTAssertThrowsError(try TextsWriter.write(russian: json(russian), english: json(english)), reason)
        }
    }

    func testIconsKeepFileOrderAndSkipComments() throws {
        let xaml = """
            <!-- <StreamGeometry x:Key="Icon.Hidden">M0 0</StreamGeometry> -->
            <ResourceDictionary xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <StreamGeometry x:Key="Icon.Nav.Home">M1 1h2</StreamGeometry>
              <StreamGeometry x:Key="Icon.A">
                M2 2v2
              </StreamGeometry>
            </ResourceDictionary>
            """
        let icons = try IconsWriter.parse(xaml)
        XCTAssertEqual(icons.map { $0.0 }, ["Nav.Home", "A"])
        XCTAssertEqual(icons.map { $0.1 }, ["M1 1h2", "M2 2v2"])

        let code = try IconsWriter.write(xaml)
        XCTAssertTrue(code.contains(#"public static let navHome = "M1 1h2""#), code)
        XCTAssertTrue(code.contains(#""Nav.Home": navHome,"#), code)
        XCTAssertThrowsError(try IconsWriter.write(#"<StreamGeometry x:Key="Shield">M0 0</StreamGeometry>"#), "без Icon.")
        XCTAssertThrowsError(try IconsWriter.write(#"<StreamGeometry x:Key="Icon.A-b">M0 0</StreamGeometry><StreamGeometry x:Key="Icon.A.b">M0 0</StreamGeometry>"#))
    }

    func testRepositoryFilesGiveAllThreeFiles() throws {
        let files = try SharedWriter.write(root: TokensTests.root)
        XCTAssertEqual(Set(files.keys), ["Tokens.swift", "Texts.swift", "Icons.swift"])
        XCTAssertTrue(files["Tokens.swift"]?.contains("public static let window = PgRGBA(argb: 0xFFEEF2F8)") == true)
    }

    private func json(_ text: String) throws -> JsonValue {
        try JsonValue.parse(Data(text.utf8), "тест")
    }
}

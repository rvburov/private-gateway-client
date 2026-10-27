import Foundation
import XCTest
@testable import PrivateGatewayTheme

/// Тексты iOS — ровно shared/specifications/localization: каждый ключ на обоих языках, подстановки «{N}» по порядку.
final class TextsTests: XCTestCase {
    func testEveryKeyHasItsTextInBothLanguages() throws {
        for language in PgTexts.Language.allCases {
            let url = TokensTests.root.appendingPathComponent("shared/specifications/localization/\(language.rawValue).json")
            let json = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(contentsOf: url)) as? [String: String])
            let texts = PgTexts(language)

            XCTAssertEqual(PgTexts.count, json.count, language.rawValue)
            for (key, value) in json {
                XCTAssertEqual(texts.text(key: key), value, "\(language.rawValue): \(key)")
            }
        }
    }

    func testPropertiesAndFunctionsReadTheirKeys() {
        XCTAssertEqual(PgTexts(.russian).appTitle, "Private Gateway")
        XCTAssertEqual(PgTexts(.russian).statusConnectedTitle, "IP скрыт")
        XCTAssertEqual(PgTexts(.english).statusConnectedTitle, "IP hidden")
        XCTAssertEqual(PgTexts(.english).coreSecondEngine("1.12.3"), "sing-box 1.12.3")
        XCTAssertNil(PgTexts(.english).text(key: "no.such.key"))
    }

    func testPlaceholdersAreReplacedOnceInOrder() {
        XCTAssertEqual(PgTexts.substitute("«{0}» в «{1}»", ["A", "B"]), "«A» в «B»")
        XCTAssertEqual(PgTexts.substitute("{1} и {0}", ["A", "B"]), "B и A")
        // Подставленное не разбирается повторно; номер без аргумента и не номер — остаются.
        XCTAssertEqual(PgTexts.substitute("{0} {1}", ["{1}", "x"]), "{1} x")
        XCTAssertEqual(PgTexts.substitute("{2} {a} { {", ["A"]), "{2} {a} { {")
        XCTAssertEqual(PgTexts.substitute("{10}", Array(repeating: "-", count: 10) + ["десять"]), "десять")
    }

    func testKeyedTextTakesArguments() {
        let ru = PgTexts(.russian)
        XCTAssertEqual(ru.text(key: "import.add.few", ["3"]), "Добавить 3 сервера")
        XCTAssertEqual(ru.text(key: "import.add.few", ["3"]), ru.importAddFew(3), "то же, что функция по ключу")
        XCTAssertEqual(PgTexts(.english).text(key: "server.port", ["443"]), PgTexts(.english).serverPort(443))
        XCTAssertNil(ru.text(key: "no.such.key", ["1"]))
    }

    func testLanguageFollowsTheFirstKnownPreferredLanguage() {
        XCTAssertEqual(PgTexts.language(for: ["ru-RU", "en-US"]), .russian)
        XCTAssertEqual(PgTexts.language(for: ["ru_RU"]), .russian)
        XCTAssertEqual(PgTexts.language(for: ["de-DE", "ru"]), .russian)
        XCTAssertEqual(PgTexts.language(for: ["en-GB", "ru"]), .english)
        XCTAssertEqual(PgTexts.language(for: ["rue", "fr"]), .english, "русинский — не русский")
        XCTAssertEqual(PgTexts.language(for: []), .english)
    }
}

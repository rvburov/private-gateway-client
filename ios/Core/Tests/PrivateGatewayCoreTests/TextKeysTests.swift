import Foundation
import XCTest
@testable import PrivateGatewayCore

/// Формы чисел — те же случаи, что `PluralTest` Android и `LocalizationTests` десктопа; каждый ключ, который дает `TextKeys`,
/// есть в обоих файлах текстов.
final class TextKeysTests: XCTestCase {
    func testRussianFollowsTheLastDigits() {
        let cases: [(Int, String)] = [
            (1, "one"), (2, "few"), (4, "few"), (5, "many"), (11, "many"), (12, "many"), (14, "many"), (21, "one"), (22, "few"), (25, "many"),
            (101, "one"), (111, "many"), (112, "many"), (0, "many"),
        ]
        for (count, form) in cases {
            XCTAssertEqual(Plural.form(count, "ru"), form, "\(count)")
        }
    }

    func testEnglishHasOneAndOther() {
        for (count, form) in [(1, "one"), (2, "many"), (21, "many"), (0, "many")] {
            XCTAssertEqual(Plural.form(count, "en"), form, "\(count)")
        }
    }

    func testUnknownCodesFallBack() {
        XCTAssertEqual(TextKeys.error("PGC-XXX-999"), "error.PGC-SVC-002")
        XCTAssertEqual(TextKeys.error(nil), "error.PGC-SVC-002")
        XCTAssertEqual(TextKeys.importError("future"), "import.error.bad_link")
        XCTAssertEqual(TextKeys.importFormat("future"), "import.format.links")
        XCTAssertEqual(TextKeys.failure(.forbidden(403)).value, "403")
        XCTAssertNil(TextKeys.failure(.notFound).value)
    }

    func testEveryKeyIsInBothLanguages() throws {
        let folder = LinkParserTests.testData.deletingLastPathComponent().appendingPathComponent("specifications/localization")
        var keys = [TunnelErrors.coreFailed, TunnelErrors.noServer, TunnelErrors.unsupported, TunnelErrors.serverNotResponding, TunnelErrors.otherVpn]
            .map(TextKeys.error)
        keys += [
            ImportErrors.badLink, ImportErrors.unknownScheme, ImportErrors.badHost, ImportErrors.badPort, ImportErrors.noCredentials,
            ImportErrors.badEncoding, ImportErrors.unsupportedTransport, ImportErrors.unsupportedMethod, ImportErrors.unsupportedParameter,
        ].map(TextKeys.importError)
        keys += ImportParser.formats.map(TextKeys.importFormat)
        let reasons: [FailureReason] = [
            .notFound, .forbidden(401), .status(500), .tooLarge, .timeout, .nameResolution, .connection, .tls, .noServers, .noAddress, .network,
            .protectedPathUnavailable,
            .protectedTransportUnsupported,
        ]
        keys += reasons.map { TextKeys.failure($0).key }
        for count in [1, 2, 5] {
            for language in ["ru", "en"] {
                keys += [TextKeys.importAdd(count, language), TextKeys.imported(count, language), TextKeys.importErrors(count, language)]
            }
        }

        for language in ["ru", "en"] {
            let data = try Data(contentsOf: folder.appendingPathComponent("\(language).json"))
            let texts = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
            for key in Set(keys) {
                XCTAssertNotNil(texts[key], "\(language): нет \(key)")
            }
        }
    }
}

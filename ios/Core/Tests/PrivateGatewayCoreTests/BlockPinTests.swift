import Foundation
import XCTest
@testable import PrivateGatewayCore

/// PIN-код блока сайтов на iOS — тот же хеш и те же правила, что `BlockPinTest` Android и `BlockPinTests` десктопа.
final class BlockPinTests: XCTestCase {
    func testPbkdf2MatchesRfc7914() {
        let expected = "55ac046e56e3089fec1691c22544b605f94185216dde0465e68b9d57c20dacbc49ca9cccf179b645991664b39d77ef317c71b845b1e30bd509112041d3a19783"
        XCTAssertEqual(hex(Pbkdf2.sha256(Array("passwd".utf8), salt: Array("salt".utf8), iterations: 1, length: 64)), expected)
        XCTAssertEqual(hex(Pbkdf2.derive(Array("passwd".utf8), salt: Array("salt".utf8), iterations: 1, length: 64)), expected)
        XCTAssertEqual(hex(Sha256.hash(Array("abc".utf8))), "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")
        XCTAssertEqual(hex(Sha256.hash([])), "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")
        XCTAssertEqual(
            hex(Sha256.hash(Array("abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq".utf8))),
            "248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1", "два блока"
        )
    }

    /// Тот же код и соль дают тот же хеш, что `Rfc2898DeriveBytes` десктопа и PBKDF2WithHmacSHA256 Android: вектор на 1000
    /// повторов (своя реализация на Linux считает 120 000 в отладочной сборке слишком долго) и число повторов — как у них.
    func testHashIsTheSameAsOnOtherPlatforms() {
        let pin = BlockPin(salt: "AAECAwQFBgcICQoLDA0ODw==", hash: "hdqJm5mLWbCO0Uw/jpVW0QJQAgZLbTYOjkTs67KHQ20=")
        XCTAssertTrue(pin.matches("2580", iterations: 1000))
        XCTAssertFalse(pin.matches("2581", iterations: 1000))
        let salt = [UInt8](0 ..< 16)
        XCTAssertEqual(
            Pbkdf2.derive(Array("2580".utf8), salt: salt, iterations: 1000, length: 32), Pbkdf2.sha256(Array("2580".utf8), salt: salt, iterations: 1000),
            "своя реализация и системная"
        )
        XCTAssertEqual(BlockPin.iterations, 120_000)
        #if canImport(CommonCrypto)
        // На iPhone и Mac — системный PBKDF2: полный вектор (120 000 повторов) быстрый.
        XCTAssertTrue(BlockPin(salt: "AAECAwQFBgcICQoLDA0ODw==", hash: "wv5WJivEpAlRDeHXLC5Mepq8WvvzAHqWH+jlyHtOAts=").matches("2580"))
        #endif
    }

    func testCreateCheckAndStore() throws {
        XCTAssertTrue(BlockPin.valid("1234"))
        XCTAssertTrue(BlockPin.valid("12345678"))
        for bad in [nil, "", "123", "123456789", "12a4", "١٢٣٤", "12 34"] {
            XCTAssertFalse(BlockPin.valid(bad), bad ?? "nil")
        }

        XCTAssertNil(BlockPin.create("12"))
        let pin = try XCTUnwrap(BlockPin.create("2468", iterations: 10))
        XCTAssertTrue(pin.matches("2468", iterations: 10))
        XCTAssertFalse(pin.matches("2469", iterations: 10))
        XCTAssertFalse(pin.matches(nil, iterations: 10))
        XCTAssertNotEqual(pin, BlockPin.create("2468", iterations: 10), "соль каждый раз новая")
        XCTAssertFalse(pin.stored.contains("2468"), "кода в записи нет")
        XCTAssertEqual(BlockPin.parse(pin.stored), pin)
        for bad in [nil, "", "abc", ":", "a:", ":b", "a:b:c"] {
            XCTAssertNil(BlockPin.parse(bad), bad ?? "nil")
        }

        XCTAssertFalse(BlockPin(salt: "!!", hash: "??").matches("2468"), "испорченная запись не пускает и не падает")
        let vault = MemoryVault()
        let store = BlockPinStore(vault: vault)
        XCTAssertNil(store.read())
        try store.save(pin)
        XCTAssertEqual(store.read(), pin)
        try store.save(nil)
        XCTAssertNil(store.read())
        XCTAssertTrue(vault.all.isEmpty)
    }

    func testFiveWrongCodesPauseForAMinute() {
        var now = Date(timeIntervalSince1970: 1_700_000_000)
        let attempts = BlockPinAttempts(now: { now })
        let right: (String) -> Bool = { $0 == "2468" }
        XCTAssertEqual(attempts.check("2468", stored: nil), .right, "кода нет — разрешено")
        for _ in 1 ..< BlockPinAttempts.attempts {
            XCTAssertEqual(attempts.check("0000", matches: right), .wrong)
        }

        XCTAssertEqual(attempts.check("0000", matches: right), .paused)
        XCTAssertEqual(attempts.check("2468", matches: right), .paused, "в паузе и верный код ждет")
        now += BlockPinAttempts.pause
        XCTAssertEqual(attempts.check("2468", matches: right), .right)
        XCTAssertEqual(attempts.check("0000", matches: right), .wrong, "счет начался заново")
    }

    func testWhatWeakensTheBlock() {
        let block = SiteRule(pattern: "example.com", action: .block)
        let proxy = SiteRule(pattern: "example.org", action: .proxy)
        XCTAssertEqual(BlockGuard.forSave(original: block, rule: SiteRule(pattern: "example.com", action: .proxy), blockOthers: false), .rule("example.com"))
        XCTAssertEqual(BlockGuard.forSave(original: block, rule: SiteRule(pattern: "example.net", action: .block), blockOthers: false), .rule("example.com"))
        XCTAssertNil(BlockGuard.forSave(original: block, rule: block, blockOthers: false), "без изменений")
        XCTAssertNil(BlockGuard.forSave(original: nil, rule: block, blockOthers: true), "новый блок — без кода")
        XCTAssertNil(BlockGuard.forSave(original: nil, rule: proxy, blockOthers: false))
        XCTAssertEqual(BlockGuard.forSave(original: nil, rule: proxy, blockOthers: true), .allow("example.org"))
        XCTAssertNil(BlockGuard.forSave(original: proxy, rule: SiteRule(pattern: "example.org", action: .direct), blockOthers: true), "тот же сайт")
        XCTAssertEqual(BlockGuard.forSave(original: proxy, rule: SiteRule(pattern: "example.net", action: .proxy), blockOthers: true), .allow("example.net"))
        XCTAssertEqual(BlockGuard.forDelete(block), .rule("example.com"))
        XCTAssertNil(BlockGuard.forDelete(proxy))
        XCTAssertEqual(BlockGuard.forOthers(from: true, to: false), .others)
        XCTAssertNil(BlockGuard.forOthers(from: false, to: true))
        XCTAssertEqual(BlockGuard.rule("example.com").pattern, "example.com")
        XCTAssertNil(BlockGuard.restore.pattern)
    }

    func testGuardTextsAreInBothLanguages() throws {
        let folder = LinkParserTests.testData.deletingLastPathComponent().appendingPathComponent("specifications/localization")
        let guards: [BlockGuard] = [.others, .rule("a"), .allow("a"), .restore]
        for language in ["ru", "en"] {
            let texts = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(contentsOf: folder.appendingPathComponent("\(language).json"))) as? [String: String])
            for item in guards {
                XCTAssertNotNil(texts[item.titleKey], item.titleKey)
                XCTAssertNotNil(texts[item.textKey], item.textKey)
                XCTAssertEqual(texts[item.titleKey]?.contains("{0}"), item.pattern != nil, item.titleKey)
            }
        }
    }

    private func hex(_ bytes: [UInt8]) -> String {
        bytes.map { String(format: "%02x", $0) }.joined()
    }
}

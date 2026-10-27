import Foundation
import XCTest
@testable import PrivateGatewayCore

/// Разбор сертификата и проверка по пунктам — случаи `ProbeRunnerTest` Android (имена, срок, цепочка) на сертификатах,
/// собранных для теста: адреса и имена — из диапазонов документации, ключ одноразовый.
final class CertificateTests: XCTestCase {
    /// v3: «C=NL, O=Example "Test", Inc., CN=www.example.com», SAN — два имени, IPv4, IPv6 и e-mail, срок
    /// 2026-01-01 00:00:00 — 2027-01-01 12:30:05 (UTCTime).
    private static let leaf = """
    MIIBxzCCAW6gAwIBAgICEjQwCgYIKoZIzj0EAwIwLDEQMA4GA1UECgwHRXhhbXBsZTEYMBYGA1UEAwwPRXhhbXBsZSBUZXN0IENBMB4XDTI2MDEwMTAwMDAwMFoXDTI3\
    MDEwMTEyMzAwNVowRjELMAkGA1UEBhMCTkwxHTAbBgNVBAoMFEV4YW1wbGUgIlRlc3QiLCBJbmMuMRgwFgYDVQQDDA93d3cuZXhhbXBsZS5jb20wWTATBgcqhkjOPQIB\
    BggqhkjOPQMBBwNCAATlN3OubZhAP7yZjhQyGhmOnk2+G9M8gOUDfsIoRE8ogX9odXZn/8X9jkRbI391m1tjPLtnWytDJ1rZsAWPeuWXo2YwZDAMBgNVHRMBAf8EAjAA\
    MFQGA1UdEQRNMEuCD3d3dy5leGFtcGxlLmNvbYINKi5leGFtcGxlLm9yZ4cEwAACCocQIAENuAAAAAAAAAAAAAAAAYERYWRtaW5AZXhhbXBsZS5jb20wCgYIKoZIzj0E\
    AwIDRwAwRAIgaWoQLIpx60Jc0w9jzMrl6lqihvMpupWqGvn71YiRzsgCIC+0p8EWYbkC/yJmSe2o8Ahb5lebz5d7yJ8Rsr1cRu9+
    """

    /// Без SAN: часть имени из двух значений (OU и O), e-mail (тип без короткого названия), CN по-русски в BMPString; срок
    /// 1999-12-31 23:59:59 (UTCTime) — 2051-03-04 05:06:07 (GeneralizedTime).
    private static let plain = """
    MIIBgzCCASmgAwIBAgICEjQwCgYIKoZIzj0EAwIwLDEQMA4GA1UECgwHRXhhbXBsZTEYMBYGA1UEAwwPRXhhbXBsZSBUZXN0IENBMCAXDTk5MTIzMTIzNTk1OVoYDzIw\
    NTEwMzA0MDUwNjA3WjBnMRwwCgYDVQQLDANMYWIwDgYDVQQKDAdFeGFtcGxlMSAwHgYJKoZIhvcNAQkBFhFhZG1pbkBleGFtcGxlLmNvbTElMCMGA1UEAx4cBD8EQAQ4\
    BDwENQRAAC4AZQB4AGEAbQBwAGwAZTBZMBMGByqGSM49AgEGCCqGSM49AwEHA0IABOU3c65tmEA/vJmOFDIaGY6eTb4b0zyA5QN+wihETyiBf2h1dmf/xf2ORFsjf3Wb\
    W2M8u2dbK0MnWtmwBY965ZcwCgYIKoZIzj0EAwIDSAAwRQIhALnrQJYWHaNyd7eWPxwVvHJ+EObm6dUj3BXsHIrhOiNkAiB0r9BXQOcs107DG4Wi0gddZZjwSYC6HWit\
    3MfATb5mEw==
    """

    private static func der(_ base64: String) throws -> [UInt8] {
        try [UInt8](XCTUnwrap(Data(base64Encoded: base64)))
    }

    private static func facts(names: [String], notBefore: TimeInterval = 0, notAfter: TimeInterval = 4_000_000_000) -> CertificateFacts {
        CertificateFacts(
            subject: "CN=example.com", issuer: "CN=Example Test CA", notBefore: Date(timeIntervalSince1970: notBefore),
            notAfter: Date(timeIntervalSince1970: notAfter), names: names
        )
    }

    private let now = Date(timeIntervalSince1970: 1_800_000_000)

    func testSubjectIssuerTermAndSanAreRead() throws {
        let facts = try XCTUnwrap(Certificate.parse(Self.der(Self.leaf)))
        XCTAssertEqual(facts.subject, #"CN=www.example.com, O=Example \"Test\"\, Inc., C=NL"#, "от частного к общему, особые знаки — через «\\»")
        XCTAssertEqual(facts.issuer, "CN=Example Test CA, O=Example")
        XCTAssertEqual(facts.notBefore, Date(timeIntervalSince1970: 1_767_225_600))
        XCTAssertEqual(facts.notAfter, Date(timeIntervalSince1970: 1_798_806_605))
        XCTAssertEqual(facts.names, ["www.example.com", "*.example.org", "192.0.2.10", "2001:db8::1"], "e-mail из SAN — не имя узла")
    }

    func testWithoutSanTheNameIsTakenFromCnAndOtherStringTypesAndTimesAreRead() throws {
        let facts = try XCTUnwrap(Certificate.parse(Self.der(Self.plain)))
        XCTAssertEqual(facts.subject, "CN=пример.example, 1.2.840.113549.1.9.1=#161161646d696e406578616d706c652e636f6d, OU=Lab+O=Example")
        XCTAssertEqual(facts.names, ["пример.example"])
        XCTAssertEqual(facts.notBefore, Date(timeIntervalSince1970: 946_684_799), "UTCTime 99 — 1999 год")
        XCTAssertEqual(facts.notAfter, Date(timeIntervalSince1970: 2_561_519_167), "GeneralizedTime после 2049 года")
    }

    func testAnythingButACertificateIsNotRead() throws {
        let leaf = try Self.der(Self.leaf)
        XCTAssertNil(Certificate.parse([]))
        XCTAssertNil(Certificate.parse(Array(leaf.dropLast())), "обрезан")
        XCTAssertNil(Certificate.parse(leaf + [0x00]), "лишнее после сертификата")
        XCTAssertNil(Certificate.parse(Array("-----BEGIN CERTIFICATE-----".utf8)), "PEM, а не DER")
        XCTAssertNil(Certificate.parse([0x30, 0x03, 0x02, 0x01, 0x01]), "не сертификат")
    }

    func testTimeOtherThanUtcIsNotADate() {
        let local = Der(tag: 0x17, content: Array("260101000000+0300".utf8)[...], raw: [])
        XCTAssertNil(DerTime.date(local))
        XCTAssertNil(DerTime.date(Der(tag: 0x17, content: Array("261301000000Z".utf8)[...], raw: [])), "тринадцатый месяц")
        XCTAssertEqual(DerTime.date(Der(tag: 0x17, content: Array("4912312359Z".utf8)[...], raw: [])), Date(timeIntervalSince1970: 2_524_607_940), "без секунд, 49 — 2049")
        XCTAssertEqual(DerTime.date(Der(tag: 0x18, content: Array("20000229120000.5Z".utf8)[...], raw: [])), Date(timeIntervalSince1970: 951_825_600), "доли секунды отбрасываются")
    }

    func testNameIsMatchedLikeAndroid() {
        let names = ["www.example.com", "*.example.org", "192.0.2.10", "2001:db8::1"]
        XCTAssertTrue(TlsCheck.matches(names, "www.example.com"))
        XCTAssertTrue(TlsCheck.matches(names, "WWW.Example.COM"))
        XCTAssertTrue(TlsCheck.matches(names, "api.example.org"))
        XCTAssertFalse(TlsCheck.matches(names, "example.org"), "звездочка — ровно одна метка")
        XCTAssertFalse(TlsCheck.matches(names, "a.b.example.org"), "звездочка — не две метки")
        XCTAssertFalse(TlsCheck.matches(names, "example.com"))
        XCTAssertTrue(TlsCheck.matches(names, "192.0.2.10"))
        XCTAssertTrue(TlsCheck.matches(names, "2001:DB8:0::1"), "IPv6 — тем же адресом в любой записи")
        XCTAssertFalse(TlsCheck.matches(names, "192.0.2.11"))
        XCTAssertFalse(TlsCheck.matches(["10.example.com"], "10.0.0.1"))
    }

    func testCertificateInOrderHasNoProblems() {
        XCTAssertEqual(TlsCheck.problems(Self.facts(names: ["example.com"]), target: "example.com", now: now, chain: .trusted), [])
    }

    func testEachProblemIsNamed() {
        let expired = Self.facts(names: ["example.com"], notAfter: 1_700_000_000)
        XCTAssertEqual(TlsCheck.problems(expired, target: "example.com", now: now, chain: .failed(dated: true)), [.expired], "срок уже сказан")
        let early = Self.facts(names: ["example.com"], notBefore: 1_900_000_000)
        XCTAssertEqual(TlsCheck.problems(early, target: "example.com", now: now, chain: .failed(dated: true)), [.notYetValid])
        XCTAssertEqual(TlsCheck.problems(Self.facts(names: ["example.org"]), target: "example.com", now: now, chain: .trusted), [.nameMismatch])
        XCTAssertEqual(TlsCheck.problems(Self.facts(names: ["example.com"]), target: "example.com", now: now, chain: .failed(dated: false)), [.untrusted])
        XCTAssertEqual(
            TlsCheck.problems(Self.facts(names: ["example.com"]), target: "example.com", now: now, chain: .failed(dated: true)), [.other],
            "свой срок в порядке — истек промежуточный"
        )
        XCTAssertEqual(
            TlsCheck.problems(Self.facts(names: ["example.org"], notAfter: 1_700_000_000), target: "example.com", now: now, chain: .failed(dated: false)),
            [.expired, .nameMismatch, .untrusted]
        )
    }

    func testVersionAndCipherAreNamedLikeTheDesktop() {
        XCTAssertEqual(TlsCheck.version(0x0304), "TLS 1.3")
        XCTAssertEqual(TlsCheck.version(0x0303), "TLS 1.2")
        XCTAssertEqual(TlsCheck.version(0x0301), "TLS 1.0")
        XCTAssertEqual(TlsCheck.version(0x7F1C), "0x7F1C")
        XCTAssertEqual(TlsCheck.cipher(0x1301), "TLS_AES_128_GCM_SHA256")
        XCTAssertEqual(TlsCheck.cipher(0xC02F), "TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256")
        XCTAssertEqual(TlsCheck.cipher(0xCCA9), "TLS_ECDHE_ECDSA_WITH_CHACHA20_POLY1305_SHA256")
        XCTAssertEqual(TlsCheck.cipher(0x00FF), "0x00FF")
    }

    func testProbeKeysHaveATextInBothLanguages() throws {
        var keys: [String] = ProbeKind.allCases.flatMap { [TextKeys.probeKind($0), TextKeys.probeMethod($0)] }
        keys += [ProbeOutcome.ok, .timedOut, .refused, .unreachable, .notFound, .failed].map { TextKeys.probeOutcome($0) }
        keys += [TlsProblem.nameMismatch, .expired, .notYetValid, .untrusted, .other].map { TextKeys.tlsProblem($0) }
        let localization = LinkParserTests.testData.deletingLastPathComponent().appendingPathComponent("specifications/localization")
        for language in ["ru", "en"] {
            let texts = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(contentsOf: localization.appendingPathComponent("\(language).json"))) as? [String: Any])
            XCTAssertEqual(keys.filter { texts[$0] == nil }, [], language)
        }
    }
}

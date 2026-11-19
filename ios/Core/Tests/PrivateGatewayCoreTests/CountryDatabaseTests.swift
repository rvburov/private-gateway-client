import Foundation
import XCTest
@testable import PrivateGatewayCore

/// Геобаза PGCTRY1 — те же случаи, что у десктопа (CountryDatabaseTests) и Android (CountryDatabaseTest).
final class CountryDatabaseTests: XCTestCase {
    private static let root = LinkParserTests.testData.deletingLastPathComponent().deletingLastPathComponent()

    func testFindsOnlyTheContainingRangeIncludingBothEdges() throws {
        let database = try CountryDatabase(data: Self.ranges())
        XCTAssertEqual(database.count, 3)
        let cases: [(String, String?)] = [
            ("192.0.2.0", nil), ("192.0.2.1", "JP"), ("192.0.2.9", "JP"), ("192.0.2.10", nil), ("198.51.100.1", "NL"),
            ("198.51.100.255", "NL"), ("203.0.113.1", nil), ("::ffff:192.0.2.1", "JP"), ("2001:db8::1", "DE"),
            ("2001:db8::ffff", "DE"), ("2001:db8::1:0", nil), ("2001:db8::1%3", nil),
        ]
        for (ip, country) in cases {
            XCTAssertEqual(database.find(try XCTUnwrap(IpLiteral.parse(ip), ip)), country, ip)
        }
    }

    func testShippedDatabaseNeverAssignsACountryToSpecialAddressesOrNames() throws {
        let database = try Self.shipped()
        for value in [
            "example.com", "127.0.0.1", "10.1.2.3", "192.0.2.1", "198.51.100.10", "203.0.113.1", "2001:db8::1", "::ffff:192.0.2.1",
            "fe80::1%4", "fc00::1", "::1", "ff02::1", "", "1.2.3", "256.1.1.1", "1:2:3", "[2001:db8::1]",
        ] {
            XCTAssertNil(database.lookup(host: value), value)
        }
    }

    func testShippedDatabaseIsValidAndKnowsPublicAddresses() throws {
        let database = try Self.shipped()
        XCTAssertGreaterThan(database.count, 600_000)
        // Адреса серверов из отчета владельца 2026-11-19: десктоп показывает флаги DE и FI.
        XCTAssertEqual(database.lookup(host: "187.124.2.129"), "DE")
        XCTAssertEqual(database.lookup(host: "80.47.227.44"), "FI")
        XCTAssertEqual(database.lookup(host: "[80.47.227.44]"), "FI")
    }

    func testRejectsCorruptFormatCountCountryOverlapAndLength() throws {
        for offset in [0, 8, 44] {
            var bytes = Self.ranges()
            bytes[offset] = 255
            XCTAssertThrowsError(try CountryDatabase(data: bytes), "байт \(offset)")
        }

        var overlapping = Self.ranges()
        overlapping.replaceSubrange((12 + 34)..<(12 + 34 + 32), with: overlapping[12..<(12 + 32)])
        XCTAssertThrowsError(try CountryDatabase(data: overlapping)) { XCTAssertEqual($0 as? CountryDatabaseError, .range) }
        XCTAssertThrowsError(try CountryDatabase(data: Self.ranges().dropLast())) { XCTAssertEqual($0 as? CountryDatabaseError, .truncated) }
        XCTAssertThrowsError(try CountryDatabase(data: Self.ranges() + Data([0]))) { XCTAssertEqual($0 as? CountryDatabaseError, .extraData) }
    }

    func testPublicAddressesMatchTheDesktopRules() throws {
        let cases: [(String, Bool)] = [
            ("1.2.3.4", true), ("8.8.8.8", true), ("100.63.0.1", true), ("100.64.0.1", false), ("172.32.0.1", true), ("172.31.0.1", false),
            ("2a00:1450::1", true), ("2001:db8::1", false), ("2001:0:1::1", false), ("3fff::1", false), ("fe80::1", false), ("::ffff:8.8.8.8", true),
        ]
        for (ip, isPublic) in cases {
            XCTAssertEqual(try XCTUnwrap(IpLiteral.parse(ip), ip).isPublic, isPublic, ip)
        }
    }

    func testNamesAndBrokenAddressesAreNotLiterals() {
        for value in ["example.com", "1.2.3.4.5", "01.2.3.4x", ":::", "1::2::3", "12345::1", "g::1", "1:2:3:4:5:6:7:8:9", "::1.2.3", "１.2.3.4"] {
            XCTAssertNil(IpLiteral.parse(value), value)
        }
    }

    /// Встроенная база — распакованная gunzip, как ее распаковывает приложение.
    static func shipped() throws -> CountryDatabase {
        try CountryDatabase(data: shippedPlain())
    }

    /// Файл геобазы приложения (`country.bin.gz`) и он же распакованный.
    static let shippedFile = root.appendingPathComponent("shared/countries/country.bin.gz")

    static func shippedPlain() throws -> Data {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/env")
        process.arguments = ["gunzip", "-c", shippedFile.path]
        let pipe = Pipe()
        process.standardOutput = pipe
        try process.run()
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        return data
    }

    static func ranges() -> Data {
        var data = Data("PGCTRY1\0".utf8)
        data += Data([0, 0, 0, 3])
        for (start, end, code) in [("192.0.2.1", "192.0.2.9", "JP"), ("198.51.100.1", "198.51.100.255", "NL"), ("2001:db8::1", "2001:db8::ffff", "DE")] {
            data += Data(IpLiteral.parse(start)!.mapped)
            data += Data(IpLiteral.parse(end)!.mapped)
            data += Data(code.utf8)
        }

        return data
    }
}

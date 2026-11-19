import Foundation
import XCTest
@testable import PrivateGatewayCore

/// Обертка gzip геобазы: заголовок с необязательными полями, окончание с длиной и CRC-32; порча и обрыв — ошибка, а не чужая база.
final class GzipTests: XCTestCase {
    /// Пустое DEFLATE: последний блок с фиксированными кодами и сразу конец блока — 03 00.
    private static let empty: [UInt8] = [0x03, 0x00]

    private func file(flags: UInt8, fields: [UInt8], deflate: [UInt8] = empty, crc: UInt32 = 0, size: UInt32 = 0) -> Data {
        let header: [UInt8] = [0x1F, 0x8B, 8, flags, 0, 0, 0, 0, 0, 3]
        let tail = [crc, size].flatMap { value in (0 ..< 4).map { UInt8(truncatingIfNeeded: value >> ($0 * 8)) } }
        return Data(header + fields + deflate + tail)
    }

    func testCrcMatchesTheStandardCheckValue() {
        XCTAssertEqual(Gzip.crc32(Data("123456789".utf8)), 0xCBF4_3926)
        XCTAssertEqual(Gzip.crc32(Data()), 0)
    }

    func testOptionalHeaderFieldsAreSkipped() throws {
        let cases: [(UInt8, [UInt8])] = [
            (0, []),
            (1, []),
            (4, [3, 0, 7, 7, 7]),
            (8, Array("country.bin".utf8) + [0]),
            (16, Array("note".utf8) + [0]),
            (2, [0xAB, 0xCD]),
            (4 | 8 | 16 | 2, [1, 0, 9] + Array("a".utf8) + [0] + Array("b".utf8) + [0] + [0xAB, 0xCD]),
        ]
        for (flags, fields) in cases {
            let member = try Gzip.member(file(flags: flags, fields: fields))
            XCTAssertEqual([UInt8](member.deflate), Self.empty, "флаги \(flags)")
            XCTAssertEqual(member.size, 0)
            XCTAssertNoThrow(try Gzip.check(Data(), member))
        }
    }

    func testBrokenHeaderOrShortFileIsRejected() {
        let good = file(flags: 0, fields: [])
        for (offset, value) in [(0, UInt8(0)), (1, 0), (2, 7), (3, 0x20)] {
            var bytes = good
            bytes[offset] = value
            XCTAssertThrowsError(try Gzip.member(bytes)) { XCTAssertEqual($0 as? Gzip.Failure, .format, "байт \(offset)") }
        }

        XCTAssertThrowsError(try Gzip.member(good.prefix(17))) { XCTAssertEqual($0 as? Gzip.Failure, .truncated) }
        XCTAssertThrowsError(try Gzip.member(file(flags: 8, fields: Array("без конца".utf8), deflate: [], crc: 1, size: 1))) {
            XCTAssertEqual($0 as? Gzip.Failure, .truncated, "имя без нулевого байта")
        }
        XCTAssertThrowsError(try Gzip.member(file(flags: 4, fields: [200, 0], deflate: []))) {
            XCTAssertEqual($0 as? Gzip.Failure, .truncated, "доп. поле длиннее файла")
        }
    }

    /// Файл приложения: распакованное gunzip совпадает с окончанием, испорченный байт или обрыв — нет.
    func testShippedDatabasePassesTheCheckAndCorruptionFails() throws {
        let member = try Gzip.member(Data(contentsOf: CountryDatabaseTests.shippedFile))
        var plain = try CountryDatabaseTests.shippedPlain()
        XCTAssertGreaterThan(member.deflate.count, 1_000_000)
        XCTAssertNoThrow(try Gzip.check(plain, member))
        XCTAssertThrowsError(try Gzip.check(plain.dropLast(), member)) { XCTAssertEqual($0 as? Gzip.Failure, .checksum) }
        plain[plain.count / 2] ^= 1
        XCTAssertThrowsError(try Gzip.check(plain, member)) { XCTAssertEqual($0 as? Gzip.Failure, .checksum) }
    }
}

import Foundation

/// Файл gzip (RFC 1952) с одной частью — геобаза `country.bin.gz`: заголовок, сжатое DEFLATE и проверка распакованного.
/// Распаковывает само DEFLATE приложение (`NSData.decompressed(using: .zlib)` iOS — DEFLATE без обертки gzip); здесь — только
/// разбор обертки и CRC-32, их проверяют тесты на Linux.
public enum Gzip {
    public enum Failure: Error, Equatable {
        /// Не gzip, не DEFLATE или заняты зарезервированные биты флагов.
        case format
        /// Файл кончился раньше заголовка или окончания.
        case truncated
        /// Распакованное не совпало с длиной или CRC-32 из окончания файла.
        case checksum
    }

    /// Сжатые данные и что должно получиться: длина (по модулю 2³²) и CRC-32.
    public struct Member: Sendable {
        public let deflate: Data
        public let size: UInt32
        public let crc: UInt32
    }

    private static let headerCrc: UInt8 = 2
    private static let extra: UInt8 = 4
    private static let name: UInt8 = 8
    private static let comment: UInt8 = 16

    public static func member(_ file: Data) throws -> Member {
        let bytes = [UInt8](file)
        guard bytes.count >= 18 else {
            throw Failure.truncated
        }

        // ID1, ID2, CM = 8 (DEFLATE); старшие три бита флагов зарезервированы.
        guard bytes[0] == 0x1F, bytes[1] == 0x8B, bytes[2] == 8, bytes[3] & 0xE0 == 0 else {
            throw Failure.format
        }

        let flags = bytes[3]
        var at = 10
        if flags & extra != 0 {
            guard at + 2 <= bytes.count else {
                throw Failure.truncated
            }

            at += 2 + Int(bytes[at]) + Int(bytes[at + 1]) << 8
        }

        for flag in [name, comment] where flags & flag != 0 {
            guard let end = bytes[min(at, bytes.count)...].firstIndex(of: 0) else {
                throw Failure.truncated
            }

            at = end + 1
        }

        if flags & headerCrc != 0 {
            at += 2
        }

        let tail = bytes.count - 8
        guard at <= tail else {
            throw Failure.truncated
        }

        return Member(deflate: Data(bytes[at ..< tail]), size: little(bytes, tail + 4), crc: little(bytes, tail))
    }

    /// Распакованное совпадает с окончанием файла: длина и CRC-32 (обрыв или порча не дают чужую геобазу).
    public static func check(_ plain: Data, _ member: Member) throws {
        guard UInt32(truncatingIfNeeded: plain.count) == member.size, crc32(plain) == member.crc else {
            throw Failure.checksum
        }
    }

    /// CRC-32 (IEEE 802.3, многочлен 0xEDB88320) — как в gzip и zlib.
    public static func crc32(_ data: Data) -> UInt32 {
        var crc: UInt32 = 0xFFFF_FFFF
        data.withUnsafeBytes { raw in
            for byte in raw {
                crc = table[Int((crc ^ UInt32(byte)) & 0xFF)] ^ (crc >> 8)
            }
        }

        return crc ^ 0xFFFF_FFFF
    }

    private static let table: [UInt32] = (0 ..< 256).map { index in
        var value = UInt32(index)
        for _ in 0 ..< 8 {
            value = value & 1 == 1 ? 0xEDB8_8320 ^ (value >> 1) : value >> 1
        }

        return value
    }

    private static func little(_ bytes: [UInt8], _ at: Int) -> UInt32 {
        UInt32(bytes[at]) | UInt32(bytes[at + 1]) << 8 | UInt32(bytes[at + 2]) << 16 | UInt32(bytes[at + 3]) << 24
    }
}

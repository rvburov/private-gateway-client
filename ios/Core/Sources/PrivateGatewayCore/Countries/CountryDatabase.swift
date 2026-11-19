import Foundation

/// Ошибка геобазы: формат, размер или порядок диапазонов не те — база не читается, флагов нет.
public enum CountryDatabaseError: Error, Equatable {
    case format
    case tooLarge
    case truncated
    case extraData
    case range
}

/// Локальная оценка страны IP — формат PGCTRY1, общий с десктопом и Android (shared/countries/country.bin.gz, DB-IP Country
/// Lite): заголовок «PGCTRY1\0» и число записей (4 байта, big-endian), затем записи по 34 байта — начало и конец диапазона
/// (16 + 16 байт, IPv4 — как ::ffff:a.b.c.d) и код ISO 3166-1 из двух латинских букв. Диапазоны идут по возрастанию и не
/// пересекаются — иначе база не читается. Нет записи — нет флага. В сеть не ходит: DNS и названия серверов не используются.
public struct CountryDatabase: Sendable {
    static let headerSize = 12
    static let recordSize = 34
    static let maxRecords = 2_000_000
    private static let magic = Array("PGCTRY1\0".utf8)

    private let data: Data
    public let count: Int

    /// База из распакованных байтов; приложение отдает файл, отображенный в память (`Data(contentsOf:options: .alwaysMapped)`).
    public init(data: Data) throws {
        guard data.count >= Self.headerSize else {
            throw CountryDatabaseError.truncated
        }

        let bytes = [UInt8](data.prefix(Self.headerSize))
        guard Array(bytes[0..<8]) == Self.magic else {
            throw CountryDatabaseError.format
        }

        let count = bytes[8..<12].reduce(0) { $0 << 8 | Int($1) }
        guard count <= Self.maxRecords else {
            throw CountryDatabaseError.tooLarge
        }

        let expected = Self.headerSize + count * Self.recordSize
        guard data.count >= expected else {
            throw CountryDatabaseError.truncated
        }

        guard data.count == expected else {
            throw CountryDatabaseError.extraData
        }

        // Каждый диапазон: начало ≤ конца, код — две буквы A–Z, начало строго после конца предыдущего.
        try data.withUnsafeBytes { (raw: UnsafeRawBufferPointer) in
            let base = raw.bindMemory(to: UInt8.self)
            var previous = -1
            for index in 0..<count {
                let at = Self.headerSize + index * Self.recordSize
                let letters = (65...90).contains(base[at + 32]) && (65...90).contains(base[at + 33])
                if Self.compare(base, at, base, at + 16) > 0 || !letters || (previous >= 0 && Self.compare(base, previous, base, at) >= 0) {
                    throw CountryDatabaseError.range
                }

                previous = at + 16
            }
        }

        self.data = data
        self.count = count
    }

    /// Код страны адреса без фильтра служебных сетей (тестовые базы на адресах документации); адрес с зоной — nil.
    public func find(_ address: IpLiteral) -> String? {
        address.scoped ? nil : find(address.mapped)
    }

    /// Страна адреса сервера: только адрес цифрами и только публичный; частные, служебные и адреса документации — nil.
    public func lookup(host: String) -> String? {
        guard let address = IpLiteral.parse(host), address.isPublic else {
            return nil
        }

        return find(address)
    }

    private func find(_ key: [UInt8]) -> String? {
        data.withUnsafeBytes { (raw: UnsafeRawBufferPointer) -> String? in
            let base = raw.bindMemory(to: UInt8.self)
            var low = 0
            var high = count - 1
            while low <= high {
                let middle = (low + high) / 2
                let at = Self.headerSize + middle * Self.recordSize
                if Self.compare(key, base, at) < 0 {
                    high = middle - 1
                } else if Self.compare(key, base, at + 16) > 0 {
                    low = middle + 1
                } else {
                    return String(decoding: [base[at + 32], base[at + 33]], as: UTF8.self)
                }
            }

            return nil
        }
    }

    private static func compare(_ a: UnsafeBufferPointer<UInt8>, _ left: Int, _ b: UnsafeBufferPointer<UInt8>, _ right: Int) -> Int {
        for offset in 0..<16 where a[left + offset] != b[right + offset] {
            return Int(a[left + offset]) - Int(b[right + offset])
        }

        return 0
    }

    private static func compare(_ key: [UInt8], _ b: UnsafeBufferPointer<UInt8>, _ right: Int) -> Int {
        for offset in 0..<16 where key[offset] != b[right + offset] {
            return Int(key[offset]) - Int(b[right + offset])
        }

        return 0
    }
}

/// Адрес, записанный цифрами: IPv4 «a.b.c.d» или IPv6 (с «::», IPv4 в конце и зоной «%…»), в квадратных скобках тоже. Разбор
/// без системных функций: имя сервера в DNS телефон не спрашивает. `scoped` — у IPv6 есть зона: адрес локальный.
public struct IpLiteral: Equatable, Sendable {
    /// 4 байта IPv4 или 16 байт IPv6.
    public let bytes: [UInt8]
    public let scoped: Bool

    /// IPv4 — как ::ffff:a.b.c.d, IPv6 — как есть.
    var mapped: [UInt8] {
        bytes.count == 16 ? bytes : [UInt8](repeating: 0, count: 10) + [0xff, 0xff] + bytes
    }

    public static func parse(_ text: String) -> IpLiteral? {
        var value = text.trimmingCharacters(in: .whitespaces)
        if value.hasPrefix("["), value.hasSuffix("]") {
            value = String(value.dropFirst().dropLast())
        }

        if let v4 = v4(value) {
            return IpLiteral(bytes: v4, scoped: false)
        }

        guard value.contains(":") else {
            return nil
        }

        var address = value
        var scoped = false
        if let percent = value.firstIndex(of: "%") {
            let zone = value[value.index(after: percent)...]
            guard !zone.isEmpty, zone.allSatisfy({ $0.isASCII && ($0.isLetter || $0.isNumber || "_.-".contains($0)) }) else {
                return nil
            }

            address = String(value[..<percent])
            scoped = true
        }

        guard address.allSatisfy({ $0.isHexDigit || $0 == ":" || $0 == "." }), let bytes = v6(address) else {
            return nil
        }

        return IpLiteral(bytes: bytes, scoped: scoped)
    }

    /// Публичный адрес: не частный, не служебный, не документации; IPv6 — только глобальный 2000::/3 без зоны (как у десктопа).
    public var isPublic: Bool {
        let mappedV4 = bytes.count == 16 && bytes[0..<10].allSatisfy { $0 == 0 } && bytes[10] == 0xff && bytes[11] == 0xff
        if bytes.count == 4 || mappedV4 {
            let v4 = bytes.count == 4 ? bytes : Array(bytes[12..<16])
            return !Self.specialV4.contains { $0.contains(v4) }
        }

        return !scoped && Self.globalV6.contains(bytes) && !Self.specialV6.contains { $0.contains(bytes) }
    }

    private struct Network: Sendable {
        let prefix: [UInt8]
        let bits: Int

        init(_ cidr: String) {
            let parts = cidr.split(separator: "/")
            prefix = IpLiteral.parse(String(parts[0]))!.bytes
            bits = Int(parts[1])!
        }

        func contains(_ address: [UInt8]) -> Bool {
            guard address.count == prefix.count else {
                return false
            }

            for bit in 0..<bits {
                let mask = UInt8(0x80) >> UInt8(bit % 8)
                if address[bit / 8] & mask != prefix[bit / 8] & mask {
                    return false
                }
            }

            return true
        }
    }

    private static let specialV4 = [
        "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16", "172.16.0.0/12",
        "192.0.0.0/24", "192.0.2.0/24", "192.168.0.0/16", "198.18.0.0/15", "198.51.100.0/24", "203.0.113.0/24", "224.0.0.0/3",
    ].map(Network.init)
    private static let globalV6 = Network("2000::/3")
    private static let specialV6 = ["2001::/23", "2001:db8::/32", "3fff::/20"].map(Network.init)

    private static func v4(_ text: String) -> [UInt8]? {
        let parts = text.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 4 else {
            return nil
        }

        var bytes: [UInt8] = []
        for part in parts {
            guard (1...3).contains(part.count), part.allSatisfy({ $0.isASCII && $0.isNumber }), let value = Int(part), value <= 255 else {
                return nil
            }

            bytes.append(UInt8(value))
        }

        return bytes
    }

    // Восемь групп, «::» не больше одного раза, IPv4 в конце — две группы.
    private static func v6(_ text: String) -> [UInt8]? {
        let halves = text.components(separatedBy: "::")
        guard halves.count <= 2, let head = groups(halves[0]) else {
            return nil
        }

        let tail: [Int]
        if halves.count == 2 {
            guard let parsed = groups(halves[1]) else {
                return nil
            }

            tail = parsed
        } else {
            tail = []
        }

        let missing = 8 - head.count - tail.count
        guard halves.count == 1 ? missing == 0 : missing >= 1 else {
            return nil
        }

        let all = head + [Int](repeating: 0, count: halves.count == 2 ? missing : 0) + tail
        return all.flatMap { [UInt8($0 >> 8), UInt8($0 & 0xff)] }
    }

    private static func groups(_ text: String) -> [Int]? {
        guard !text.isEmpty else {
            return []
        }

        let parts = text.split(separator: ":", omittingEmptySubsequences: false)
        var result: [Int] = []
        for (index, part) in parts.enumerated() {
            if index == parts.count - 1, part.contains(".") {
                guard let v4 = v4(String(part)) else {
                    return nil
                }

                result += [Int(v4[0]) << 8 | Int(v4[1]), Int(v4[2]) << 8 | Int(v4[3])]
            } else {
                guard (1...4).contains(part.count), let value = Int(part, radix: 16) else {
                    return nil
                }

                result.append(value)
            }
        }

        return result
    }
}

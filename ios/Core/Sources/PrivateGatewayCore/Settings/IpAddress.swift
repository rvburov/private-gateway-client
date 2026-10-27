import Foundation

/// Запись адреса IP — строгий разбор без поиска имен, как `IpAddress` Android. IPv4 — ровно четыре числа 0–255 без ведущих
/// нулей («1.2» и «010.0.0.1» не адреса); IPv6 — группы по 1–4 шестнадцатеричные цифры, восемь или меньше с одним «::»,
/// последние две можно записать как IPv4 («::ffff:192.0.2.1»). Зона («fe80::1%wlan0») не принимается: ядру она не нужна.
public enum IpAddress {
    /// Байты адреса: 4 — IPv4, 16 — IPv6; nil — не адрес.
    public static func parse(_ text: String) -> [UInt8]? {
        ipv4(text) ?? ipv6(text)
    }

    private static func ipv4(_ text: Substring) -> [UInt8]? {
        let parts = text.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 4 else {
            return nil
        }

        var bytes: [UInt8] = []
        for part in parts {
            guard (1...3).contains(part.count), part.allSatisfy({ $0.isASCII && $0.isNumber }), part.count == 1 || part.first != "0",
                  let value = Int(part), value <= 255
            else {
                return nil
            }

            bytes.append(UInt8(value))
        }

        return bytes
    }

    private static func ipv4(_ text: String) -> [UInt8]? {
        ipv4(Substring(text))
    }

    private static func ipv6(_ text: String) -> [UInt8]? {
        // «::» — не больше одного: второй («1::2::3», «::::») оставляет пустую группу, и такой адрес не проходит.
        let gap = text.range(of: "::")
        guard let head = groups(gap.map { text[..<$0.lowerBound] } ?? Substring(text), last: gap == nil) else {
            return nil
        }

        var tail: [UInt16] = []
        if let gap {
            guard let words = groups(text[gap.upperBound...], last: true) else {
                return nil
            }

            tail = words
        }

        // «::» заменяет хотя бы одну группу нулей; без него групп ровно восемь.
        let count = head.count + tail.count
        guard gap != nil ? count <= 7 : count == 8 else {
            return nil
        }

        let words = head + Array(repeating: 0, count: 8 - count) + tail
        return words.flatMap { [UInt8($0 >> 8), UInt8($0 & 0xFF)] }
    }

    /// Группы части адреса по обе стороны «::»; IPv4 — только в конце адреса (`last`) и дает две группы.
    private static func groups(_ part: Substring, last: Bool) -> [UInt16]? {
        if part.isEmpty {
            return []
        }

        let pieces = part.split(separator: ":", omittingEmptySubsequences: false)
        var words: [UInt16] = []
        for (index, piece) in pieces.enumerated() {
            if last, index == pieces.count - 1, piece.contains(".") {
                guard let v4 = ipv4(piece) else {
                    return nil
                }

                words.append(UInt16(v4[0]) << 8 | UInt16(v4[1]))
                words.append(UInt16(v4[2]) << 8 | UInt16(v4[3]))
            } else {
                guard (1...4).contains(piece.count), piece.allSatisfy({ $0.isASCII && $0.isHexDigit }), let word = UInt16(piece, radix: 16) else {
                    return nil
                }

                words.append(word)
            }
        }

        return words
    }
}

/// Частные сети — те же, что открывает «Разрешить локальную сеть» на десктопе и Android (`PrivateNetwork`): 10/8, 172.16/12,
/// 192.168/16, 169.254/16, fc00::/7, fe80::/10. IPv4 в IPv6 («::ffff:192.168.1.53») — как IPv4.
public enum PrivateNetwork {
    /// Адрес из частной сети; не адрес — false.
    public static func contains(_ address: String) -> Bool {
        guard let parsed = IpAddress.parse(address) else {
            return false
        }

        let bytes = parsed.count == 16 && mapped(parsed) ? Array(parsed[12..<16]) : parsed
        let first = bytes[0]
        let second = bytes[1]
        if bytes.count == 4 {
            return first == 10 || (first == 172 && second & 0xF0 == 16) || (first == 192 && second == 168) || (first == 169 && second == 254)
        }

        return first & 0xFE == 0xFC || (first == 0xFE && second & 0xC0 == 0x80)
    }

    /// ::ffff:a.b.c.d — десять нулевых байтов и два 0xFF.
    private static func mapped(_ bytes: [UInt8]) -> Bool {
        bytes[0..<10].allSatisfy { $0 == 0 } && bytes[10] == 0xFF && bytes[11] == 0xFF
    }
}

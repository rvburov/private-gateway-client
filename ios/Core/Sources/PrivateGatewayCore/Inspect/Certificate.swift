import Foundation

/// Что видно в сертификате узла: кому и кем выдан (как у десктопа: «CN=example.com, O=Example»), срок и имена для проверки.
public struct CertificateFacts: Equatable, Sendable {
    public var subject: String
    public var issuer: String
    public var notBefore: Date?
    public var notAfter: Date?
    /// DNS и IP из SAN; SAN нет — имя из CN, как делают старые проверки.
    public var names: [String]

    public init(subject: String, issuer: String, notBefore: Date?, notAfter: Date?, names: [String]) {
        self.subject = subject
        self.issuer = issuer
        self.notBefore = notBefore
        self.notAfter = notAfter
        self.names = names
    }
}

/// Сертификат X.509 в DER — разбор того, что нужно замеру TLS. iOS отдает приложению только байты сертификата и краткое
/// имя (`SecCertificateCopySubjectSummary`): ни издателя, ни срока, ни SAN — поэтому разбор свой, без проверки подписи
/// (цепочку проверяет iOS, `SecTrust`).
public enum Certificate {
    /// Сведения сертификата; не сертификат — nil.
    public static func parse(_ der: [UInt8]) -> CertificateFacts? {
        guard let top = Der.items(der[...]), top.count == 1, top[0].tag == Der.sequence, let certificate = top[0].children,
              let first = certificate.first, first.tag == Der.sequence, var tbs = first.children
        else {
            return nil
        }

        // Версия — [0] в начале; у сертификатов v1 ее нет.
        if tbs.first?.tag == 0xA0 {
            tbs.removeFirst()
        }

        guard tbs.count >= 6, tbs[2].tag == Der.sequence, tbs[3].tag == Der.sequence, tbs[4].tag == Der.sequence,
              let issuer = DistinguishedName(tbs[2]), let subject = DistinguishedName(tbs[4]), let validity = tbs[3].children,
              validity.count == 2
        else {
            return nil
        }

        let san = tbs.dropFirst(6).first { $0.tag == 0xA3 }.map(alternativeNames) ?? []
        return CertificateFacts(
            subject: subject.text, issuer: issuer.text, notBefore: DerTime.date(validity[0]), notAfter: DerTime.date(validity[1]),
            names: san.isEmpty ? subject.values(of: DistinguishedName.commonName) : san
        )
    }

    /// Имена из расширения SAN (2.5.29.17): dNSName — [2], iPAddress — [7] (4 или 16 байт).
    private static func alternativeNames(_ extensions: Der) -> [String] {
        guard let list = extensions.children?.first?.children else {
            return []
        }

        for item in list {
            guard let parts = item.children, parts.count >= 2, parts[0].tag == Der.oid, Der.oidText(parts[0].content) == "2.5.29.17",
                  let value = parts.last, value.tag == Der.octetString, let names = Der.items(value.content)?.first?.children
            else {
                continue
            }

            return names.compactMap { name in
                switch name.tag {
                case 0x82: return String(decoding: name.content, as: UTF8.self)
                case 0x87 where name.content.count == 4 || name.content.count == 16: return AddressText.of(Array(name.content))
                default: return nil
                }
            }
        }

        return []
    }
}

/// Имя X.500 строкой — как у десктопа: от частного к общему («CN=…, O=…, C=…»), несколько значений одной части — через
/// «+». Известные типы — короткими названиями (как RFC 4514), прочие — номером OID и значением в шестнадцатеричном виде.
struct DistinguishedName {
    static let commonName = "2.5.4.3"
    private static let short = [
        "2.5.4.3": "CN", "2.5.4.6": "C", "2.5.4.7": "L", "2.5.4.8": "ST", "2.5.4.9": "STREET", "2.5.4.10": "O", "2.5.4.11": "OU",
        "0.9.2342.19200300.100.1.25": "DC", "0.9.2342.19200300.100.1.1": "UID",
    ]

    /// Значение части имени: строка или, если тип не строковый или OID неизвестен, весь узел DER в шестнадцатеричном виде.
    private struct Pair {
        let oid: String
        let value: String
        let hex: Bool

        var text: String {
            (DistinguishedName.short[oid] ?? oid) + "=" + (hex ? "#" + value : DistinguishedName.escape(value))
        }
    }

    /// Части имени в порядке сертификата (от общего к частному).
    private let parts: [[Pair]]

    init?(_ name: Der) {
        guard let sets = name.children else {
            return nil
        }

        var parts: [[Pair]] = []
        for set in sets {
            guard set.tag == Der.set, let pairs = set.children else {
                return nil
            }

            var part: [Pair] = []
            for pair in pairs {
                guard let items = pair.children, items.count == 2, items[0].tag == Der.oid, let oid = Der.oidText(items[0].content) else {
                    return nil
                }

                if Self.short[oid] != nil, let value = Der.string(items[1]) {
                    part.append(Pair(oid: oid, value: value, hex: false))
                } else {
                    part.append(Pair(oid: oid, value: Der.hex(items[1].raw), hex: true))
                }
            }
            parts.append(part)
        }

        self.parts = parts
    }

    var text: String {
        parts.reversed().map { part in part.map(\.text).joined(separator: "+") }.joined(separator: ", ")
    }

    func values(of oid: String) -> [String] {
        parts.reversed().flatMap { part in part.filter { $0.oid == oid && !$0.hex }.map(\.value) }
    }

    /// Особые знаки значения — через «\» (RFC 4514): запятая, плюс, кавычка, обратная черта, угловые скобки, точка с запятой;
    /// «#» и пробел в начале, пробел в конце.
    static func escape(_ value: String) -> String {
        var result = ""
        for (index, character) in value.enumerated() {
            let edge = (index == 0 && (character == "#" || character == " ")) || (index == value.count - 1 && character == " ")
            if edge || ",+\"\\<>;".contains(character) {
                result.append("\\")
            }

            result.append(character)
        }

        return result
    }
}

/// Время сертификата: UTCTime («YYMMDDHHMMSSZ», годы 50–99 — двадцатый век) и GeneralizedTime («YYYYMMDDHHMMSSZ», доли
/// секунды отбрасываются). Только UTC — так требует RFC 5280; другое — nil.
enum DerTime {
    static func date(_ item: Der) -> Date? {
        let text = String(decoding: item.content, as: UTF8.self)
        guard text.hasSuffix("Z") else {
            return nil
        }

        let digits = text.dropLast().prefix { $0 != "." }
        guard digits.allSatisfy({ ("0"..."9").contains($0) }) else {
            return nil
        }

        let numbers = Array(digits).map { Int(String($0)) ?? 0 }
        func number(_ from: Int, _ count: Int) -> Int {
            numbers[from..<from + count].reduce(0) { $0 * 10 + $1 }
        }

        let year: Int
        let rest: Int
        switch (item.tag, numbers.count) {
        case (0x17, 10), (0x17, 12):
            let short = number(0, 2)
            year = short < 50 ? 2000 + short : 1900 + short
            rest = 2
        case (0x18, 12), (0x18, 14):
            year = number(0, 4)
            rest = 4
        default:
            return nil
        }

        let month = number(rest, 2)
        let day = number(rest + 2, 2)
        let hour = number(rest + 4, 2)
        let minute = number(rest + 6, 2)
        let second = numbers.count - rest == 10 ? number(rest + 8, 2) : 0
        guard (1...12).contains(month), (1...31).contains(day), hour < 24, minute < 60, second < 61 else {
            return nil
        }

        let seconds = days(year, month, day) * 86400 + hour * 3600 + minute * 60 + second
        return Date(timeIntervalSince1970: TimeInterval(seconds))
    }

    /// Дней от 1970-01-01 до даты григорианского календаря (алгоритм Х. Хиннанта).
    private static func days(_ year: Int, _ month: Int, _ day: Int) -> Int {
        let y = month <= 2 ? year - 1 : year
        let era = (y >= 0 ? y : y - 399) / 400
        let yearOfEra = y - era * 400
        let dayOfYear = (153 * (month + (month > 2 ? -3 : 9)) + 2) / 5 + day - 1
        let dayOfEra = yearOfEra * 365 + yearOfEra / 4 - yearOfEra / 100 + dayOfYear
        return era * 146_097 + dayOfEra - 719_468
    }
}

/// Узел DER: тег, содержимое и весь узел с заголовком (срезы исходных байт).
struct Der {
    static let sequence: UInt8 = 0x30
    static let set: UInt8 = 0x31
    static let oid: UInt8 = 0x06
    static let octetString: UInt8 = 0x04

    let tag: UInt8
    let content: ArraySlice<UInt8>
    let raw: ArraySlice<UInt8>

    /// Вложенные узлы составного; простой узел или ошибка разбора — nil.
    var children: [Der]? {
        tag & 0x20 != 0 ? Der.items(content) : nil
    }

    /// Узлы подряд; ошибка разбора — nil. Длина — до 4 байт, теги — однобайтовые (других в сертификате нет).
    static func items(_ bytes: ArraySlice<UInt8>) -> [Der]? {
        var result: [Der] = []
        var index = bytes.startIndex
        while index < bytes.endIndex {
            let start = index
            let tag = bytes[index]
            index += 1
            guard tag & 0x1F != 0x1F, index < bytes.endIndex else {
                return nil
            }

            var length = Int(bytes[index])
            index += 1
            if length & 0x80 != 0 {
                let count = length & 0x7F
                guard (1...4).contains(count), bytes.endIndex - index >= count else {
                    return nil
                }

                length = 0
                for _ in 0..<count {
                    length = length << 8 | Int(bytes[index])
                    index += 1
                }
            }

            guard bytes.endIndex - index >= length else {
                return nil
            }

            result.append(Der(tag: tag, content: bytes[index..<index + length], raw: bytes[start..<index + length]))
            index += length
        }

        return result
    }

    /// OID точками: «2.5.29.17»; ошибка — nil.
    static func oidText(_ bytes: ArraySlice<UInt8>) -> String? {
        var arcs: [Int] = []
        var value = 0
        for byte in bytes {
            guard value < Int.max >> 8 else {
                return nil
            }

            value = value << 7 | Int(byte & 0x7F)
            if byte & 0x80 == 0 {
                if arcs.isEmpty {
                    let first = min(value / 40, 2)
                    arcs += [first, value - first * 40]
                } else {
                    arcs.append(value)
                }
                value = 0
            }
        }

        guard !arcs.isEmpty, bytes.last.map({ $0 & 0x80 == 0 }) == true else {
            return nil
        }

        return arcs.map(String.init).joined(separator: ".")
    }

    /// Строка ASN.1 любого из типов, что бывают в именах; другой тип — nil.
    static func string(_ item: Der) -> String? {
        let bytes = Array(item.content)
        switch item.tag {
        case 0x0C, 0x13, 0x16, 0x1A:
            return String(decoding: bytes, as: UTF8.self)
        case 0x14:
            return String(bytes.map { Character(Unicode.Scalar($0)) })
        case 0x1E:
            guard bytes.count % 2 == 0 else {
                return nil
            }

            let units = stride(from: 0, to: bytes.count, by: 2).map { UInt16(bytes[$0]) << 8 | UInt16(bytes[$0 + 1]) }
            return String(decoding: units, as: UTF16.self)
        case 0x1C:
            guard bytes.count % 4 == 0 else {
                return nil
            }

            var text = ""
            for index in stride(from: 0, to: bytes.count, by: 4) {
                guard let scalar = Unicode.Scalar(bytes[index..<index + 4].reduce(UInt32(0)) { $0 << 8 | UInt32($1) }) else {
                    return nil
                }

                text.unicodeScalars.append(scalar)
            }
            return text
        default:
            return nil
        }
    }

    static func hex(_ bytes: ArraySlice<UInt8>) -> String {
        bytes.map { String(format: "%02x", $0) }.joined()
    }
}

/// Что сказала проверка цепочки доверенными корнями iPhone (`SecTrust` в приложении).
public enum ChainCheck: Equatable, Sendable {
    case trusted
    /// Цепочка не прошла; `dated` — из-за срока (своего или промежуточного сертификата).
    case failed(dated: Bool)
}

/// Проверка сертификата узла по пунктам — как `ProbeRunner.problems` Android: срок — по часам iPhone, имя — по SAN, цепочка
/// — доверенными корнями iPhone. Так видно, что именно с сертификатом не так, а не только «не прошел».
public enum TlsCheck {
    public static func problems(_ leaf: CertificateFacts, target: String, now: Date, chain: ChainCheck) -> [TlsProblem] {
        var problems: [TlsProblem] = []
        if let notAfter = leaf.notAfter, now > notAfter {
            problems.append(.expired)
        } else if let notBefore = leaf.notBefore, now < notBefore {
            problems.append(.notYetValid)
        }

        if !matches(leaf.names, target) {
            problems.append(.nameMismatch)
        }

        // Цепочка не прошла из-за срока — это уже сказано; иначе — корня нет или другая ошибка цепочки.
        if case let .failed(dated) = chain, !dated || !problems.contains(where: { $0 == .expired || $0 == .notYetValid }) {
            problems.append(dated ? .other : .untrusted)
        }

        return problems
    }

    /// Подходит ли сертификат узлу: IP — равным адресом из SAN, имя — точным или «*.example.com» ровно на одну метку слева
    /// (сам example.com под него не подходит).
    public static func matches(_ names: [String], _ target: String) -> Bool {
        if let ip = ProbeTarget.ip(target) {
            return names.contains { ProbeTarget.ip($0) == ip }
        }

        let host = target.lowercased()
        return names.contains { name in
            let pattern = name.lowercased()
            guard pattern.hasPrefix("*.") else {
                return pattern == host
            }

            let suffix = String(pattern.dropFirst())
            return host.hasSuffix(suffix) && host.count > suffix.count && !host.dropLast(suffix.count).contains(".")
        }
    }

    /// Версия TLS по номеру протокола — как у десктопа: «TLS 1.3».
    public static func version(_ number: UInt16) -> String {
        switch number {
        case 0x0301: return "TLS 1.0"
        case 0x0302: return "TLS 1.1"
        case 0x0303: return "TLS 1.2"
        case 0x0304: return "TLS 1.3"
        case 0xFEFF: return "DTLS 1.0"
        case 0xFEFD: return "DTLS 1.2"
        default: return String(format: "0x%04X", number)
        }
    }

    /// Набор шифров по номеру — имя IANA, как у десктопа и Android; незнакомый — номером.
    public static func cipher(_ number: UInt16) -> String {
        ciphers[number] ?? String(format: "0x%04X", number)
    }

    private static let ciphers: [UInt16: String] = [
        0x1301: "TLS_AES_128_GCM_SHA256", 0x1302: "TLS_AES_256_GCM_SHA384", 0x1303: "TLS_CHACHA20_POLY1305_SHA256",
        0xC02B: "TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256", 0xC02C: "TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384",
        0xC02F: "TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256", 0xC030: "TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384",
        0xCCA8: "TLS_ECDHE_RSA_WITH_CHACHA20_POLY1305_SHA256", 0xCCA9: "TLS_ECDHE_ECDSA_WITH_CHACHA20_POLY1305_SHA256",
        0xC023: "TLS_ECDHE_ECDSA_WITH_AES_128_CBC_SHA256", 0xC024: "TLS_ECDHE_ECDSA_WITH_AES_256_CBC_SHA384",
        0xC027: "TLS_ECDHE_RSA_WITH_AES_128_CBC_SHA256", 0xC028: "TLS_ECDHE_RSA_WITH_AES_256_CBC_SHA384",
        0xC009: "TLS_ECDHE_ECDSA_WITH_AES_128_CBC_SHA", 0xC00A: "TLS_ECDHE_ECDSA_WITH_AES_256_CBC_SHA",
        0xC013: "TLS_ECDHE_RSA_WITH_AES_128_CBC_SHA", 0xC014: "TLS_ECDHE_RSA_WITH_AES_256_CBC_SHA",
        0x009C: "TLS_RSA_WITH_AES_128_GCM_SHA256", 0x009D: "TLS_RSA_WITH_AES_256_GCM_SHA384",
        0x003C: "TLS_RSA_WITH_AES_128_CBC_SHA256", 0x003D: "TLS_RSA_WITH_AES_256_CBC_SHA256",
        0x002F: "TLS_RSA_WITH_AES_128_CBC_SHA", 0x0035: "TLS_RSA_WITH_AES_256_CBC_SHA", 0x000A: "TLS_RSA_WITH_3DES_EDE_CBC_SHA",
    ]
}

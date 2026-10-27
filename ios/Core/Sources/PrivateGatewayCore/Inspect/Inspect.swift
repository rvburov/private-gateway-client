import Foundation

/// Вид сети iPhone — как `PhoneNetworkKind` Android: то, что называет iOS (`NWInterface.InterfaceType`).
public enum PhoneNetworkKind: String, CaseIterable, Sendable {
    case wifi
    case cellular
    case ethernet
    /// VPN — свой туннель или чужой.
    case vpn
    case other
}

/// Сеть iPhone, как ее видит iOS (`NWPathMonitor`): вид, интерфейс, сеть ли это по умолчанию, дорогая ли (сотовая или
/// «Экономия данных»), есть ли выход в интернет, поддерживает ли IPv4, IPv6 и DNS. Адресов, шлюзов и DNS приложению iOS не
/// дает — их нет и на экране (решение 465). Только чтение.
public struct PhoneNetwork: Equatable, Sendable {
    public var kind: PhoneNetworkKind
    public var name: String?
    public var isDefault: Bool
    public var expensive: Bool
    public var constrained: Bool
    public var satisfied: Bool
    public var ipv4: Bool
    public var ipv6: Bool
    public var dns: Bool

    public init(
        kind: PhoneNetworkKind, name: String?, isDefault: Bool, expensive: Bool = false, constrained: Bool = false, satisfied: Bool = true,
        ipv4: Bool = true, ipv6: Bool = false, dns: Bool = true
    ) {
        self.kind = kind
        self.name = name
        self.isDefault = isDefault
        self.expensive = expensive
        self.constrained = constrained
        self.satisfied = satisfied
        self.ipv4 = ipv4
        self.ipv6 = ipv6
        self.dns = dns
    }
}

public enum PhoneNetworks {
    private static let rank: [PhoneNetworkKind] = [.vpn, .wifi, .cellular, .ethernet, .other]

    /// Порядок на экране — как у Android: сеть по умолчанию первой, затем VPN, Wi-Fi, сотовая, кабель и прочие; внутри — по
    /// имени.
    public static func order(_ networks: [PhoneNetwork]) -> [PhoneNetwork] {
        networks.sorted { left, right in
            if left.isDefault != right.isDefault {
                return left.isDefault
            }

            let leftRank = rank.firstIndex(of: left.kind) ?? rank.count
            let rightRank = rank.firstIndex(of: right.kind) ?? rank.count
            if leftRank != rightRank {
                return leftRank < rightRank
            }

            return (left.name ?? "") < (right.name ?? "")
        }
    }
}

/// Замер инспектора на телефоне: поиск имени, соединение TCP, рукопожатие TLS. Эхо ICMP и трассировка приложению iOS
/// недоступны: сырых сокетов у него нет — как у Android (решение 448).
public enum ProbeKind: String, CaseIterable, Sendable {
    case dns
    case tcp
    case tls
}

/// Чем кончилась попытка замера — как у десктопа и Android.
public enum ProbeOutcome: String, Sendable {
    /// Ответил: адрес имени, открытый порт, рукопожатие.
    case ok
    /// Ответа не было за отведенное время.
    case timedOut
    /// Порт закрыт: узел отказал в соединении.
    case refused
    /// Узел или сеть недоступны: так сказала ОС.
    case unreachable
    /// Имя не найдено или у него нет адресов.
    case notFound
    /// Другой отказ: рукопожатие TLS оборвано и подобное.
    case failed
}

/// Что не так с сертификатом узла.
public enum TlsProblem: String, Sendable {
    case nameMismatch
    case expired
    case notYetValid
    case untrusted
    case other
}

/// Что показало рукопожатие TLS: версия («TLSv1.3»), набор шифров, согласованный протокол приложения, кому и кем выдан
/// сертификат, срок, имена из SAN и что не так (все в порядке — пусто).
public struct TlsFacts: Equatable, Sendable {
    public var `protocol`: String
    public var cipher: String
    public var alpn: String?
    public var subject: String
    public var issuer: String
    public var notBefore: Date?
    public var notAfter: Date?
    public var names: [String]
    public var problems: [TlsProblem]

    public init(
        protocol: String, cipher: String, alpn: String?, subject: String, issuer: String, notBefore: Date?, notAfter: Date?, names: [String],
        problems: [TlsProblem]
    ) {
        self.protocol = `protocol`
        self.cipher = cipher
        self.alpn = alpn
        self.subject = subject
        self.issuer = issuer
        self.notBefore = notBefore
        self.notAfter = notAfter
        self.names = names
        self.problems = problems
    }
}

/// Строка хода замера: номер (адрес имени по порядку; у TCP и TLS — 1), кто ответил, за сколько мс, чем кончилось.
public struct ProbeStep: Equatable, Sendable {
    public var index: Int
    public var address: String?
    public var timeMs: Int?
    public var outcome: ProbeOutcome
    public var tls: TlsFacts?

    public init(index: Int, address: String?, timeMs: Int?, outcome: ProbeOutcome, tls: TlsFacts? = nil) {
        self.index = index
        self.address = address
        self.timeMs = timeMs
        self.outcome = outcome
        self.tls = tls
    }
}

/// Замер целиком: что, до какого узла и порта, по шагам.
public struct ProbeResult: Equatable, Sendable {
    public var kind: ProbeKind
    public var target: String
    public var port: Int?
    public var steps: [ProbeStep]

    public init(kind: ProbeKind, target: String, port: Int?, steps: [ProbeStep]) {
        self.kind = kind
        self.target = target
        self.port = port
        self.steps = steps
    }

    /// Итог строкой: ключ текста (probe.summary.*) и подстановки — те же правила, что у десктопа и Android.
    public func summary() -> (key: String, arguments: [String]) {
        guard let first = steps.first else {
            return ("probe.summary.failed", [])
        }

        if first.outcome == .notFound {
            return ("probe.summary.notFound", [])
        }

        if kind == .dns {
            return ("probe.summary.dns", [String(steps.count), String(first.timeMs ?? 0)])
        }

        let port = port.map(String.init) ?? ""
        switch (first.outcome, first.tls) {
        case let (.ok, tls?) where !tls.problems.isEmpty: return ("probe.summary.tls.problems", [])
        case let (.ok, tls?): return ("probe.summary.tls", [tls.protocol])
        case (.ok, nil) where kind == .tls: return ("probe.summary.failed", [])
        case (.ok, nil): return ("probe.summary.tcp", [port, String(first.timeMs ?? 0)])
        case (.refused, _): return ("probe.summary.refused", [port])
        case (.timedOut, _): return ("probe.summary.timedOut", [])
        case (.unreachable, _): return ("probe.summary.unreachable", [])
        default: return ("probe.summary.failed", [])
        }
    }
}

/// Узел замера: IP или имя хоста — правила десктопа и Android (`ProbeTarget`). Имя — буквы, цифры, дефис и точки
/// (международное — в виде punycode), не длиннее 253 знаков, последняя метка — не одни цифры; IPv4 — только четыре числа,
/// IPv6 — и в скобках.
public enum ProbeTarget {
    public static let maxPort = 65535

    /// Узел в виде для замера; не узел — nil. Пробелы по краям и точка в конце имени отбрасываются.
    public static func normalize(_ text: String?) -> String? {
        guard var value = text?.trimmingCharacters(in: .whitespacesAndNewlines) else {
            return nil
        }

        while value.hasSuffix(".") {
            value.removeLast()
        }

        if value.isEmpty {
            return nil
        }

        if let v4 = IpAddress.parse(value), v4.count == 4 {
            return AddressText.of(v4)
        }

        // IPv6 — и в скобках, как в адресной строке; имя в скобках не пройдет проверку меток ниже.
        if value.contains(":") {
            return ip(value)
        }

        let labels = value.lowercased().split(separator: ".", omittingEmptySubsequences: false).map { label -> String? in
            label.allSatisfy(\.isASCII) ? String(label) : Punycode.encode(String(label)).map { "xn--" + $0 }
        }
        guard labels.allSatisfy({ $0 != nil }) else {
            return nil
        }

        let ascii = labels.compactMap { $0 }
        let name = ascii.joined(separator: ".")
        guard name.count <= 253, ascii.allSatisfy(label), let last = ascii.last, !last.allSatisfy(\.isNumber) else {
            return nil
        }

        return name
    }

    /// Только IP-литерал (IPv4 четырьмя числами или IPv6, и в скобках) в каноническом виде; имя и другое — nil.
    public static func ip(_ text: String?) -> String? {
        guard let trimmed = text?.trimmingCharacters(in: .whitespacesAndNewlines), !trimmed.isEmpty else {
            return nil
        }

        let bracketed = trimmed.hasPrefix("[") && trimmed.hasSuffix("]")
        let value = bracketed ? String(trimmed.dropFirst().dropLast()) : trimmed
        guard let bytes = IpAddress.parse(value), !bracketed || bytes.count == 16 else {
            return nil
        }

        return AddressText.of(bytes)
    }

    /// Порт замера: число от 1 до 65535; другое — nil.
    public static func port(_ text: String?) -> Int? {
        text.flatMap { Int($0.trimmingCharacters(in: .whitespacesAndNewlines)) }.flatMap { (1...maxPort).contains($0) ? $0 : nil }
    }

    private static func label(_ label: String) -> Bool {
        (1...63).contains(label.count) && label.first != "-" && label.last != "-"
            && label.allSatisfy { ("a"..."z").contains($0) || ("0"..."9").contains($0) || $0 == "-" }
    }
}

/// Punycode (RFC 3492) — для меток международных имен: «пример» → «e1afmkfd». Только запись, без проверки состава символов.
public enum Punycode {
    private static let base = 36
    private static let tMin = 1
    private static let tMax = 26
    private static let skew = 38
    private static let damp = 700

    public static func encode(_ input: String) -> String? {
        let points = input.unicodeScalars.map { Int($0.value) }
        var output = points.filter { $0 < 128 }.compactMap { Unicode.Scalar($0).map(Character.init) }
        let basic = output.count
        var handled = basic
        if basic > 0 {
            output.append("-")
        }

        var n = 128
        var delta = 0
        var bias = 72
        while handled < points.count {
            guard let next = points.filter({ $0 >= n }).min() else {
                return nil
            }

            let (step, overflow) = (next - n).multipliedReportingOverflow(by: handled + 1)
            if overflow {
                return nil
            }

            delta += step
            n = next
            for point in points {
                if point < n {
                    delta += 1
                }

                if point == n {
                    var q = delta
                    var k = base
                    while true {
                        let t = k <= bias ? tMin : (k >= bias + tMax ? tMax : k - bias)
                        if q < t {
                            break
                        }

                        output.append(digit(t + (q - t) % (base - t)))
                        q = (q - t) / (base - t)
                        k += base
                    }

                    output.append(digit(q))
                    bias = adapt(delta, handled + 1, first: handled == basic)
                    delta = 0
                    handled += 1
                }
            }

            delta += 1
            n += 1
        }

        return String(output)
    }

    private static func digit(_ value: Int) -> Character {
        Character(Unicode.Scalar(UInt8(value < 26 ? 97 + value : 22 + value)))
    }

    private static func adapt(_ delta: Int, _ points: Int, first: Bool) -> Int {
        var delta = first ? delta / damp : delta / 2
        delta += delta / points
        var k = 0
        while delta > ((base - tMin) * tMax) / 2 {
            delta /= base - tMin
            k += base
        }

        return k + (base - tMin + 1) * delta / (delta + skew)
    }
}

/// Адрес текстом: IPv4 — как есть, IPv6 — короткой записью RFC 5952 (как у десктопа и Android); IPv4 в IPv6
/// («::ffff:1.1.1.1») — как IPv4, так его видит и Android.
public enum AddressText {
    public static func of(_ bytes: [UInt8]) -> String {
        if let v4 = ipv4(bytes) {
            return v4.map(String.init).joined(separator: ".")
        }

        let groups = (0..<8).map { Int(bytes[$0 * 2]) << 8 | Int(bytes[$0 * 2 + 1]) }
        // Самая длинная серия нулевых групп (от двух) сворачивается в «::»; при равных — первая.
        var bestStart = -1
        var bestLength = 1
        var index = 0
        while index < 8 {
            if groups[index] != 0 {
                index += 1
                continue
            }

            let start = index
            while index < 8, groups[index] == 0 {
                index += 1
            }

            if index - start > bestLength {
                bestStart = start
                bestLength = index - start
            }
        }

        let hex = { (range: Range<Int>) in range.map { String(groups[$0], radix: 16) }.joined(separator: ":") }
        if bestStart < 0 {
            return hex(0..<8)
        }

        return hex(0..<bestStart) + "::" + hex((bestStart + bestLength)..<8)
    }

    /// Четыре байта IPv4: сам IPv4 или IPv4 в IPv6 (::ffff:0:0/96); иначе nil.
    static func ipv4(_ bytes: [UInt8]) -> [UInt8]? {
        if bytes.count == 4 {
            return bytes
        }

        if bytes.count == 16, bytes[0..<10].allSatisfy({ $0 == 0 }), bytes[10] == 0xFF, bytes[11] == 0xFF {
            return Array(bytes[12..<16])
        }

        return nil
    }
}

/// Почему сведений об адресе нет — как у десктопа и Android.
public enum AddressFailure: String, Sendable {
    /// Адрес частный или служебный: в интернете о нем ничего нет.
    case `private`
    /// Источник не ответил: нет сети, сеть закрыта, отказ сервиса.
    case unreachable
    /// Источник ответил, но об адресе ничего не знает.
    case notFound
}

/// Что известно об адресе в интернете — автономная система и ее владелец, объявленная сеть (RIPEstat), примерное место
/// (MaxMind GeoLite2 через RIPEstat).
public struct AddressInfo: Equatable, Sendable {
    public var address: String
    public var asn: Int?
    public var holder: String?
    public var prefix: String?
    public var country: String?
    public var city: String?
    public var failure: AddressFailure?

    public init(address: String, asn: Int?, holder: String?, prefix: String?, country: String?, city: String?, failure: AddressFailure?) {
        self.address = address
        self.asn = asn
        self.holder = holder
        self.prefix = prefix
        self.country = country
        self.city = city
        self.failure = failure
    }

    public static func failed(_ address: String, _ failure: AddressFailure) -> AddressInfo {
        AddressInfo(address: address, asn: nil, holder: nil, prefix: nil, country: nil, city: nil, failure: failure)
    }
}

/// Публичный адрес и что о нем знает RIPEstat — правила `PublicAddress` Android и `AddressLookup` десктопа: о частном,
/// служебном и адресе документации не спрашивать; в запросе — только сам адрес в каноническом виде.
public enum PublicAddress {
    /// Открытый API RIPE NCC: без ключа и учетной записи.
    public static let source = "https://stat.ripe.net/data/"
    /// Внешний IP: Cloudflare отвечает строкой «ip=…» — тот же источник, что у десктопа и Android.
    public static let trace = "https://1.1.1.1/cdn-cgi/trace"

    // Не публичные IPv4 (RFC 6890) — тот же список, что у десктопа.
    private static let special4 = [
        "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16", "172.16.0.0/12", "192.0.0.0/24", "192.0.2.0/24",
        "192.168.0.0/16", "198.18.0.0/15", "198.51.100.0/24", "203.0.113.0/24", "224.0.0.0/3",
    ].map(network)

    // IPv6 публичен только в 2000::/3 — кроме служебных IETF (и Teredo) и документации.
    private static let global6 = network("2000::/3")
    private static let special6 = ["2001::/23", "2001:db8::/32", "3fff::/20"].map(network)

    public static func isPublic(_ address: String) -> Bool {
        guard let bytes = IpAddress.parse(address) else {
            return false
        }

        if let v4 = AddressText.ipv4(bytes) {
            return !special4.contains { contains($0, v4) }
        }

        return contains(global6, bytes) && !special6.contains { contains($0, bytes) }
    }

    /// Адреса запросов RIPEstat об адресе: обзор сети и место.
    public static func requests(_ address: String) -> [String] {
        guard let resource = ProbeTarget.ip(address) else {
            return []
        }

        return [
            "\(source)prefix-overview/data.json?resource=\(resource)&sourceapp=private-gateway",
            "\(source)maxmind-geo-lite/data.json?resource=\(resource)&sourceapp=private-gateway",
        ]
    }

    /// Внешний IP из ответа Cloudflare trace (строка «ip=…»); нет или не адрес — nil.
    public static func traceIp(_ body: String?) -> String? {
        guard let line = body?.split(whereSeparator: \.isNewline).first(where: { $0.hasPrefix("ip=") }) else {
            return nil
        }

        return ProbeTarget.ip(String(line.dropFirst(3)))
    }

    /// Сведения из ответов RIPEstat. Объявленной сети нет — ни ASN, ни владельца; страна «??» или пустая — неизвестна. Нет ни
    /// одного ответа — `unreachable`; ответы есть, сведений нет — `notFound`.
    public static func parse(_ address: String, overview: String?, geo: String?) -> AddressInfo {
        let (asn, holder, prefix) = self.overview(overview)
        let (country, city) = location(geo)
        if overview == nil, geo == nil {
            return .failed(address, .unreachable)
        }

        if asn == nil, country == nil {
            return .failed(address, .notFound)
        }

        return AddressInfo(address: address, asn: asn, holder: holder, prefix: prefix, country: country, city: city, failure: nil)
    }

    private static func overview(_ json: String?) -> (Int?, String?, String?) {
        guard let data = data(json) else {
            return (nil, nil, nil)
        }

        var first: [String: ConfigNode]?
        if case let .array(asns, _, _)? = data["asns"], case let .object(item, _)? = asns.first {
            first = item
        }

        var asn: Int?
        if case let .number(text)? = first?["asn"], let value = Int(text), value > 0 {
            asn = value
        }

        let holder = asn != nil ? text(first, "holder") : nil
        // Сеть — только объявленная: без объявления RIPEstat возвращает сам адрес.
        var prefix: String?
        if case .bool(true)? = data["announced"], let resource = text(data, "resource"), resource.contains("/") {
            prefix = resource
        }

        return (asn, holder, prefix)
    }

    private static func location(_ json: String?) -> (String?, String?) {
        guard case let .array(located, _, _)? = data(json)?["located_resources"] else {
            return (nil, nil)
        }

        for resource in located {
            guard case let .object(fields, _) = resource, case let .array(locations, _, _)? = fields["locations"] else {
                continue
            }

            for case let .object(location, _) in locations {
                if let country = text(location, "country"), country.count == 2, country.allSatisfy({ $0.isASCII && $0.isLetter }) {
                    return (country.uppercased(), text(location, "city"))
                }
            }
        }

        return (nil, nil)
    }

    // «data» ответа со статусом «ok»; другое или не JSON — nil.
    private static func data(_ json: String?) -> [String: ConfigNode]? {
        guard let json, let parsed = try? JsonTree.parse(json), case let .object(root, _) = parsed.root, text(root, "status") == "ok",
              case let .object(data, _)? = root["data"]
        else {
            return nil
        }

        return data
    }

    private static func text(_ node: [String: ConfigNode]?, _ key: String) -> String? {
        guard case let .string(value)? = node?[key] else {
            return nil
        }

        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty ? nil : trimmed
    }

    private struct Network {
        let base: [UInt8]
        let bits: Int
    }

    private static func network(_ cidr: String) -> Network {
        let parts = cidr.split(separator: "/")
        return Network(base: IpAddress.parse(String(parts[0])) ?? [], bits: Int(parts[1]) ?? 0)
    }

    private static func contains(_ network: Network, _ bytes: [UInt8]) -> Bool {
        guard bytes.count == network.base.count else {
            return false
        }

        for bit in 0..<network.bits {
            let mask = UInt8(0x80 >> (bit % 8))
            if bytes[bit / 8] & mask != network.base[bit / 8] & mask {
                return false
            }
        }

        return true
    }
}

import Foundation
#if canImport(CommonCrypto)
import CommonCrypto
#endif

/// PIN-код снятия блока сайтов (решение 742) — `BlockPin` десктопа и Android: 4–8 цифр, хранится солью и хешем PBKDF2-SHA256
/// (120 000 повторов, 32 байта) — сам код не записывается. Без него нельзя убрать или изменить правило «Блокировать», открыть
/// остальные сайты и разрешить сайт при «Остальные сайты: Блокировать». Забыли — сброс входом в аккаунт.
public struct BlockPin: Equatable, Sendable {
    public static let minLength = 4
    public static let maxLength = 8
    /// Сколько раз считать хеш — как у десктопа и Android.
    static let iterations = 120_000

    public let salt: String
    public let hash: String

    public init(salt: String, hash: String) {
        self.salt = salt
        self.hash = hash
    }

    /// Запись для хранилища: «соль:хеш» (base64).
    public var stored: String { salt + ":" + hash }

    public static func valid(_ pin: String?) -> Bool {
        guard let pin else { return false }
        return (minLength ... maxLength).contains(pin.count) && pin.allSatisfy { ("0" ... "9").contains($0) }
    }

    /// Новый код со свежей солью 16 байт; не 4–8 цифр — nil.
    public static func create(_ pin: String) -> BlockPin? {
        create(pin, iterations: iterations)
    }

    static func create(_ pin: String, iterations: Int) -> BlockPin? {
        guard valid(pin) else { return nil }
        var generator = SystemRandomNumberGenerator()
        let salt = (0 ..< 16).map { _ in UInt8.random(in: .min ... .max, using: &generator) }
        return BlockPin(salt: Data(salt).base64EncodedString(), hash: Data(Pbkdf2.sha256(Array(pin.utf8), salt: salt, iterations: iterations)).base64EncodedString())
    }

    /// Из записи хранилища; пусто или испорчено — nil (кода нет).
    public static func parse(_ text: String?) -> BlockPin? {
        let parts = text?.split(separator: ":", omittingEmptySubsequences: false) ?? []
        guard parts.count == 2, parts.allSatisfy({ !$0.isEmpty }) else { return nil }
        return BlockPin(salt: String(parts[0]), hash: String(parts[1]))
    }

    /// Совпадает ли код; сравнение — за одинаковое время. Испорченная запись не пускает.
    public func matches(_ pin: String?) -> Bool {
        matches(pin, iterations: Self.iterations)
    }

    func matches(_ pin: String?, iterations: Int) -> Bool {
        guard let pin, Self.valid(pin), let salt = Data(base64Encoded: salt), let expected = Data(base64Encoded: hash), !salt.isEmpty else {
            return false
        }

        let actual = Pbkdf2.sha256(Array(pin.utf8), salt: Array(salt), iterations: iterations)
        guard actual.count == expected.count else { return false }
        return zip(actual, expected).reduce(UInt8(0)) { $0 | ($1.0 ^ $1.1) } == 0
    }
}

/// Ответ на введенный код.
public enum PinCheck: Equatable, Sendable {
    case right
    case wrong
    case paused
}

/// Попытки ввода PIN-кода (решение 742): пять неверных подряд — минута без попыток, как у десктопа и Android. Живет вместе с
/// окном: уход с экрана счетчик не сбрасывает. Не потокобезопасен — им владеет главный поток.
public final class BlockPinAttempts {
    public static let attempts = 5
    public static let pause: TimeInterval = 60

    private let now: () -> Date
    private var failures = 0
    private var pausedUntil = Date.distantPast

    public init(now: @escaping () -> Date = Date.init) {
        self.now = now
    }

    /// Проверить код против сохраненного; кода нет — верно.
    public func check(_ pin: String, stored: BlockPin?) -> PinCheck {
        check(pin) { stored?.matches($0) ?? true }
    }

    func check(_ pin: String, matches: (String) -> Bool) -> PinCheck {
        let time = now()
        if time < pausedUntil {
            return .paused
        }

        if matches(pin) {
            failures = 0
            return .right
        }

        failures += 1
        if failures >= Self.attempts {
            failures = 0
            pausedUntil = time.addingTimeInterval(Self.pause)
            return .paused
        }

        return .wrong
    }
}

/// Где лежит PIN-код: Связка ключей «только это устройство» — в резервную копию настроек и на другой iPhone он не уходит, как
/// у Android (решение 742). Связка ключей не отвечает — кода нет для чтения и ошибка для записи.
public struct BlockPinStore: Sendable {
    static let key = "block.pin"
    private let vault: any SecretVault

    public init(vault: any SecretVault) {
        self.vault = vault
    }

    public func read() -> BlockPin? {
        BlockPin.parse((try? vault.load(Self.key)) ?? nil)
    }

    public func save(_ pin: BlockPin?) throws {
        if let pin {
            try vault.store(pin.stored, for: Self.key)
        } else {
            try vault.remove(Self.key)
        }
    }
}

/// Что ослабляет блок сайтов и потому спрашивает PIN-код (решение 742), — правила `RoutingScreen` Android и десктопа.
public enum BlockGuard: Equatable, Sendable {
    /// «Остальные сайты: Открывать».
    case others
    /// Правило «Блокировать» меняется или удаляется.
    case rule(String)
    /// При «Остальные сайты: Блокировать» сайт начинает открываться: новое разрешение или другой адрес.
    case allow(String)
    /// Восстановление копии с настройками: в ней блок может быть слабее.
    case restore

    /// Заголовок и текст листа «Снять блок…».
    public var titleKey: String {
        switch self {
        case .others: return "block.guard.others.title"
        case .rule: return "block.guard.rule.title"
        case .allow: return "block.guard.allow.title"
        case .restore: return "block.guard.restore.title"
        }
    }

    public var textKey: String {
        switch self {
        case .others: return "block.guard.others.text"
        case .rule: return "block.guard.rule.text"
        case .allow: return "block.guard.allow.text"
        case .restore: return "block.guard.restore.text"
        }
    }

    /// Адрес для «{0}» заголовка.
    public var pattern: String? {
        switch self {
        case let .rule(pattern), let .allow(pattern): return pattern
        default: return nil
        }
    }

    /// Сохранение правила `rule` на месте `original` (nil — новое): правило «Блокировать» меняется — `.rule`; при блоке
    /// остальных сайт начинает открываться — `.allow`; иначе спрашивать нечего.
    public static func forSave(original: SiteRule?, rule: SiteRule, blockOthers: Bool) -> BlockGuard? {
        if let original, original.action == .block, original != rule {
            return .rule(original.pattern)
        }

        let allowsMore = blockOthers && rule.action != .block
            && (original == nil || original?.action == .block || original?.pattern != rule.pattern || original?.isExact != rule.isExact)
        return allowsMore ? .allow(rule.pattern) : nil
    }

    /// Удаление правила: только «Блокировать» спрашивает код.
    public static func forDelete(_ rule: SiteRule) -> BlockGuard? {
        rule.action == .block ? .rule(rule.pattern) : nil
    }

    /// Смена «Остальные сайты»: только «Открывать» после «Блокировать» спрашивает код.
    public static func forOthers(from blockOthers: Bool, to next: Bool) -> BlockGuard? {
        blockOthers && !next ? .others : nil
    }
}

/// PBKDF2-HMAC-SHA256: на iPhone и Mac — CommonCrypto, на Linux (тесты общего модуля) — своя реализация SHA-256 по FIPS 180-4.
/// Обе сверяются с векторами RFC 7914 и с хешем десктопа и Android.
enum Pbkdf2 {
    static func sha256(_ password: [UInt8], salt: [UInt8], iterations: Int, length: Int = 32) -> [UInt8] {
        #if canImport(CommonCrypto)
        var out = [UInt8](repeating: 0, count: length)
        let status = CCKeyDerivationPBKDF(
            CCPBKDFAlgorithm(kCCPBKDF2), password.map { CChar(bitPattern: $0) }, password.count, salt, salt.count,
            CCPseudoRandomAlgorithm(kCCPRFHmacAlgSHA256), UInt32(iterations), &out, length
        )
        return status == kCCSuccess ? out : []
        #else
        return derive(password, salt: salt, iterations: iterations, length: length)
        #endif
    }

    /// Своя реализация: состояния HMAC после ключа считаются один раз, каждый повтор — два сжатия SHA-256 над готовым блоком
    /// (сообщение 32 байта с дополнением), без новых массивов.
    static func derive(_ password: [UInt8], salt: [UInt8], iterations: Int, length: Int) -> [UInt8] {
        var key = password.count > 64 ? Sha256.hash(password) : password
        key += [UInt8](repeating: 0, count: 64 - key.count)
        var inner = Sha256()
        inner.update(key.map { $0 ^ 0x36 })
        var outer = Sha256()
        outer.update(key.map { $0 ^ 0x5c })
        let innerState = inner.words
        let outerState = outer.words

        var out: [UInt8] = []
        var block: UInt32 = 1
        var state = [UInt32](repeating: 0, count: 8)
        var w = [UInt32](repeating: 0, count: 64)
        while out.count < length {
            var first = inner
            first.update(salt + [UInt8(block >> 24), UInt8(block >> 16 & 0xff), UInt8(block >> 8 & 0xff), UInt8(block & 0xff)])
            var second = outer
            second.update(first.finish())
            var u = Sha256.words(second.finish())
            var t = u
            if iterations > 1 {
                for _ in 1 ..< iterations {
                    // HMAC от 32 байт: блок — сообщение, 0x80, нули и длина 768 бит (64 байта ключа + 32 байта).
                    for index in 0 ..< 8 {
                        w[index] = u[index]
                        state[index] = innerState[index]
                    }

                    Self.pad(&w)
                    Sha256.rounds(&state, &w)
                    for index in 0 ..< 8 {
                        w[index] = state[index]
                        state[index] = outerState[index]
                    }

                    Self.pad(&w)
                    Sha256.rounds(&state, &w)
                    for index in 0 ..< 8 {
                        u[index] = state[index]
                        t[index] ^= state[index]
                    }
                }
            }

            out += t.flatMap { word in [UInt8(word >> 24), UInt8(word >> 16 & 0xff), UInt8(word >> 8 & 0xff), UInt8(word & 0xff)] }
            block += 1
        }

        return Array(out.prefix(length))
    }

    private static func pad(_ w: inout [UInt32]) {
        w[8] = 0x8000_0000
        for index in 9 ..< 15 {
            w[index] = 0
        }

        w[15] = 768
    }
}

/// SHA-256 (FIPS 180-4) для PBKDF2 на Linux.
struct Sha256 {
    private static let k: [UInt32] = [
        0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5, 0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3,
        0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174, 0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
        0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967, 0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13,
        0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85, 0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
        0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3, 0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208,
        0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
    ]

    private var state: [UInt32] = [0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19]
    private var pending: [UInt8] = []
    private var length: UInt64 = 0

    static func hash(_ data: [UInt8]) -> [UInt8] {
        var sha = Sha256()
        sha.update(data)
        return sha.finish()
    }

    mutating func update(_ data: [UInt8]) {
        length += UInt64(data.count)
        pending += data
        var offset = 0
        while pending.count - offset >= 64 {
            compress(pending, offset)
            offset += 64
        }

        pending.removeFirst(offset)
    }

    mutating func finish() -> [UInt8] {
        let bits = length * 8
        var tail: [UInt8] = [0x80]
        tail += [UInt8](repeating: 0, count: (55 - Int(length % 64) + 64) % 64)
        tail += (0 ..< 8).map { UInt8(bits >> (56 - 8 * UInt64($0)) & 0xff) }
        update(tail)
        return state.flatMap { word in [UInt8(word >> 24), UInt8(word >> 16 & 0xff), UInt8(word >> 8 & 0xff), UInt8(word & 0xff)] }
    }

    /// Состояние после целых блоков — для HMAC, где ключ занимает ровно один блок.
    var words: [UInt32] { state }

    static func words(_ bytes: [UInt8]) -> [UInt32] {
        stride(from: 0, to: bytes.count, by: 4).map { UInt32(bytes[$0]) << 24 | UInt32(bytes[$0 + 1]) << 16 | UInt32(bytes[$0 + 2]) << 8 | UInt32(bytes[$0 + 3]) }
    }

    private mutating func compress(_ data: [UInt8], _ offset: Int) {
        var w = [UInt32](repeating: 0, count: 64)
        for index in 0 ..< 16 {
            let base = offset + index * 4
            w[index] = UInt32(data[base]) << 24 | UInt32(data[base + 1]) << 16 | UInt32(data[base + 2]) << 8 | UInt32(data[base + 3])
        }

        Self.rounds(&state, &w)
    }

    /// Сжатие одного блока: первые 16 слов `w` — блок, остальные заполняются здесь.
    static func rounds(_ state: inout [UInt32], _ w: inout [UInt32]) {
        state.withUnsafeMutableBufferPointer { state in
            w.withUnsafeMutableBufferPointer { w in
                k.withUnsafeBufferPointer { k in
                    for index in 16 ..< 64 {
                        let x = w[index - 15]
                        let y = w[index - 2]
                        let s0 = (x >> 7 | x << 25) ^ (x >> 18 | x << 14) ^ (x >> 3)
                        let s1 = (y >> 17 | y << 15) ^ (y >> 19 | y << 13) ^ (y >> 10)
                        w[index] = w[index - 16] &+ s0 &+ w[index - 7] &+ s1
                    }

                    var a = state[0], b = state[1], c = state[2], d = state[3], e = state[4], f = state[5], g = state[6], h = state[7]
                    for index in 0 ..< 64 {
                        let s1 = (e >> 6 | e << 26) ^ (e >> 11 | e << 21) ^ (e >> 25 | e << 7)
                        let choice = (e & f) ^ (~e & g)
                        let t1 = h &+ s1 &+ choice &+ k[index] &+ w[index]
                        let s0 = (a >> 2 | a << 30) ^ (a >> 13 | a << 19) ^ (a >> 22 | a << 10)
                        let majority = (a & b) ^ (a & c) ^ (b & c)
                        h = g
                        g = f
                        f = e
                        e = d &+ t1
                        d = c
                        c = b
                        b = a
                        a = t1 &+ s0 &+ majority
                    }

                    state[0] &+= a
                    state[1] &+= b
                    state[2] &+= c
                    state[3] &+= d
                    state[4] &+= e
                    state[5] &+= f
                    state[6] &+= g
                    state[7] &+= h
                }
            }
        }
    }
}

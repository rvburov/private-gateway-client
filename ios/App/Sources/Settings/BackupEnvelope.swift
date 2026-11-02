import CommonCrypto
import CryptoKit
import Security
import Foundation
import PrivateGatewayCore

/// Файл .pgbackup — `BackupFile` десктопа и `BackupEnvelope` Android, байт в байт тот же формат (решение 559): JSON-обертка,
/// AES-256-GCM, ключ из пароля через PBKDF2-SHA256 (600 000 повторов, соль 16 байт) или готовый ключ (автокопия: ключ — в
/// Связке ключей). Параметры обертки и время создания входят в проверку подлинности. Общий эталон `shared/testdata/backup`.
enum BackupEnvelope {
    static let format = "private-gateway-backup"
    static let version = 1
    static let iterations = 600_000

    private static let passwordKdf = "pbkdf2-sha256"
    private static let storedKey = "os-key"

    /// Запечатать паролем; долго (PBKDF2) — не в главном потоке.
    static func seal(_ content: Data, password: String, created: Date) throws -> String {
        let salt = random(16)
        return try seal(content, kdf: passwordKdf, iterations: iterations, salt: salt, key: derive(password, salt: salt, iterations: iterations), created: created)
    }

    /// Запечатать готовым ключом (автокопия).
    static func seal(_ content: Data, key: Data, created: Date) throws -> String {
        try seal(content, kdf: storedKey, iterations: 0, salt: Data(), key: key, created: created)
    }

    /// Открыть паролем; долго (PBKDF2) — не в главном потоке.
    static func open(_ text: String, password: String) throws -> Data {
        try open(text, kdf: passwordKdf) { salt, iterations in derive(password, salt: salt, iterations: iterations) }
    }

    static func open(_ text: String, key: Data) throws -> Data {
        try open(text, kdf: storedKey) { _, _ in key }
    }

    /// Время создания из обертки — без пароля; nil — не копия.
    static func created(_ text: String) -> Date? {
        envelope(text).flatMap { MobileBackup.parseTime($0.created) }
    }

    private struct Envelope {
        var version: Int
        var kdf: String
        var iterations: Int
        var salt: String
        var nonce: String
        var created: String
        var data: String

        /// Все, кроме самих данных, — под защитой тега GCM, как у десктопа.
        var associatedData: Data {
            Data([format, String(version), kdf, String(iterations), salt, nonce, created].joined(separator: "|").utf8)
        }
    }

    private static func seal(_ content: Data, kdf: String, iterations: Int, salt: Data, key: Data, created: Date) throws -> String {
        let nonce = random(12)
        let time = ISO8601DateFormatter()
        time.formatOptions = [.withInternetDateTime]
        let envelope = Envelope(
            version: version, kdf: kdf, iterations: iterations, salt: salt.base64EncodedString(), nonce: nonce.base64EncodedString(),
            created: time.string(from: created), data: ""
        )
        let box = try AES.GCM.seal(content, using: SymmetricKey(data: key), nonce: AES.GCM.Nonce(data: nonce), authenticating: envelope.associatedData)
        let data = box.ciphertext + box.tag
        return JsonText.write(.object([
            ("format", .string(format)), ("version", .int(envelope.version)), ("kdf", .string(kdf)), ("iterations", .int(iterations)),
            ("salt", .string(envelope.salt)), ("nonce", .string(envelope.nonce)), ("created", .string(envelope.created)),
            ("data", .string(data.base64EncodedString())),
        ]))
    }

    private static func open(_ text: String, kdf: String, key: (Data, Int) throws -> Data) throws -> Data {
        guard let envelope = envelope(text) else {
            throw BackupError.notBackup
        }

        if envelope.version > version {
            throw BackupError.newerVersion
        }

        guard let salt = Data(base64Encoded: envelope.salt), let nonce = Data(base64Encoded: envelope.nonce),
              let data = Data(base64Encoded: envelope.data)
        else {
            throw BackupError.notBackup
        }

        let iterationsOk = kdf == storedKey || (100_000...10_000_000).contains(envelope.iterations)
        guard envelope.kdf == kdf, iterationsOk, nonce.count == 12, data.count >= 16 else {
            throw BackupError.notBackup
        }

        do {
            let box = try AES.GCM.SealedBox(nonce: AES.GCM.Nonce(data: nonce), ciphertext: data.dropLast(16), tag: data.suffix(16))
            return try AES.GCM.open(box, using: SymmetricKey(data: key(salt, envelope.iterations)), authenticating: envelope.associatedData)
        } catch {
            throw BackupError.wrongPassword
        }
    }

    private static func envelope(_ text: String) -> Envelope? {
        guard let root = (try? JSONSerialization.jsonObject(with: Data(text.utf8))) as? [String: Any], root["format"] as? String == format,
              let version = (root["version"] as? NSNumber)?.intValue, let kdf = root["kdf"] as? String,
              let iterations = (root["iterations"] as? NSNumber)?.intValue, let salt = root["salt"] as? String, let nonce = root["nonce"] as? String,
              let created = root["created"] as? String, let data = root["data"] as? String
        else {
            return nil
        }

        return Envelope(version: version, kdf: kdf, iterations: iterations, salt: salt, nonce: nonce, created: created, data: data)
    }

    private static func derive(_ password: String, salt: Data, iterations: Int) throws -> Data {
        var derived = Data(count: 32)
        let secret = Array(password.utf8)
        let status = derived.withUnsafeMutableBytes { out in
            salt.withUnsafeBytes { saltBytes in
                CCKeyDerivationPBKDF(
                    CCPBKDFAlgorithm(kCCPBKDF2), secret.map { CChar(bitPattern: $0) }, secret.count,
                    saltBytes.bindMemory(to: UInt8.self).baseAddress, salt.count, CCPseudoRandomAlgorithm(kCCPRFHmacAlgSHA256), UInt32(iterations),
                    out.bindMemory(to: UInt8.self).baseAddress, 32
                )
            }
        }
        guard status == kCCSuccess else {
            throw BackupError.notBackup
        }

        return derived
    }

    private static func random(_ count: Int) -> Data {
        var bytes = [UInt8](repeating: 0, count: count)
        _ = SecRandomCopyBytes(kSecRandomDefault, count, &bytes)
        return Data(bytes)
    }
}

import Foundation
#if canImport(Security)
import Security
#endif

/// Где лежат секреты — ссылка сервера (адрес и ключи) и адрес подписки (токен): на iPhone — Связка ключей с доступом
/// приложения и расширения туннеля (`KeychainVault`), в тестах — память. Открытым текстом на диск они не попадают.
public protocol SecretVault: Sendable {
    func store(_ secret: String, for key: String) throws
    /// Секрет по ключу; нет записи — nil.
    func load(_ key: String) throws -> String?
    func remove(_ key: String) throws
}

/// Где лежат открытые данные хранилища (подписи списка, группы, замеры) — файл в общей папке App Group (`StateFile.disk`)
/// или память в тестах.
public protocol StateFile: Sendable {
    /// Содержимое; файла еще нет — nil.
    func read() throws -> Data?
    func write(_ data: Data) throws
}

/// Ошибка Связки ключей: код OSStatus — без секрета и ключа записи.
public struct VaultError: Error, Equatable, Sendable {
    public let status: Int32
}

/// Секреты в памяти — для тестов и предпросмотра.
public final class MemoryVault: SecretVault, @unchecked Sendable {
    private let lock = NSLock()
    private var items: [String: String] = [:]

    public init() {}

    public func store(_ secret: String, for key: String) throws {
        locked { items[key] = secret }
    }

    public func load(_ key: String) throws -> String? {
        locked { items[key] }
    }

    public func remove(_ key: String) throws {
        locked { _ = items.removeValue(forKey: key) }
    }

    /// Все секреты — тестам: проверить, что их нет в открытых данных.
    public var all: [String: String] {
        locked { items }
    }

    private func locked<T>(_ body: () -> T) -> T {
        lock.lock()
        defer { lock.unlock() }
        return body()
    }
}

/// Файл в памяти — для тестов и предпросмотра.
public final class MemoryFile: StateFile, @unchecked Sendable {
    private let lock = NSLock()
    private var data: Data?

    public init() {}

    public func read() throws -> Data? {
        lock.lock()
        defer { lock.unlock() }
        return data
    }

    public func write(_ data: Data) throws {
        lock.lock()
        defer { lock.unlock() }
        self.data = data
    }
}

/// Файл на диске: запись целиком через временный файл (атомарно); на iPhone — с защитой данных «до первой разблокировки»:
/// расширение туннеля читает его и при заблокированном экране после перезагрузки и разблокировки.
public struct DiskFile: StateFile {
    public let url: URL

    public init(url: URL) {
        self.url = url
    }

    public func read() throws -> Data? {
        guard FileManager.default.fileExists(atPath: url.path) else {
            return nil
        }

        return try Data(contentsOf: url)
    }

    public func write(_ data: Data) throws {
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        #if os(iOS)
        try data.write(to: url, options: [.atomic, .completeFileProtectionUntilFirstUserAuthentication])
        #else
        try data.write(to: url, options: .atomic)
        #endif
    }
}

#if canImport(Security)
/// Связка ключей: записи «пароль» службы `service`, доступны после первой разблокировки и только на этом устройстве (в
/// резервную копию и на другой iPhone не уходят). Общая группа доступа (App Group) — чтобы расширение туннеля открыло
/// ссылку выбранного сервера; без группы — группа приложения по умолчанию.
public struct KeychainVault: SecretVault {
    public let service: String
    public let accessGroup: String?

    public init(service: String, accessGroup: String?) {
        self.service = service
        self.accessGroup = accessGroup
    }

    public func store(_ secret: String, for key: String) throws {
        let data = Data(secret.utf8)
        let updated = SecItemUpdate(query(key) as CFDictionary, [kSecValueData as String: data] as CFDictionary)
        if updated == errSecSuccess {
            return
        }

        guard updated == errSecItemNotFound else {
            throw VaultError(status: updated)
        }

        var item = query(key)
        item[kSecValueData as String] = data
        item[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        let added = SecItemAdd(item as CFDictionary, nil)
        guard added == errSecSuccess else {
            throw VaultError(status: added)
        }
    }

    public func load(_ key: String) throws -> String? {
        var request = query(key)
        request[kSecReturnData as String] = true
        request[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: CFTypeRef?
        let status = SecItemCopyMatching(request as CFDictionary, &result)
        if status == errSecItemNotFound {
            return nil
        }

        guard status == errSecSuccess, let data = result as? Data else {
            throw VaultError(status: status)
        }

        return String(data: data, encoding: .utf8)
    }

    public func remove(_ key: String) throws {
        let status = SecItemDelete(query(key) as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else {
            throw VaultError(status: status)
        }
    }

    private func query(_ key: String) -> [String: Any] {
        var query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: key,
            kSecUseDataProtectionKeychain as String: true,
        ]
        if let accessGroup {
            query[kSecAttrAccessGroup as String] = accessGroup
        }

        return query
    }
}
#endif

import Foundation

/// Почему копию не открыть — `BackupError` Android и десктопа.
public enum BackupError: Error, Equatable, Sendable {
    /// Это не файл резервной копии или он испорчен так, что не читается.
    case notBackup
    /// Копия из более новой версии программы.
    case newerVersion
    /// Пароль не подходит или содержимое изменено: AES-GCM эти случаи не различает.
    case wrongPassword
    /// Копия десктопа: серверы в ней — описанием десктопа, телефон восстанавливает копии телефона (решение 559).
    case fromComputer
}

/// Сервер в копии телефона: ссылка с ключами, свое название, избранное и профиль (null — «Мои серверы»).
public struct BackupServer: Equatable, Sendable {
    public var link: String
    public var name: String?
    public var favorite: Bool
    public var group: String?

    public init(link: String, name: String? = nil, favorite: Bool = false, group: String? = nil) {
        self.link = link
        self.name = name
        self.favorite = favorite
        self.group = group
    }
}

/// Подписка в копии: адрес и название группы.
public struct BackupSubscription: Equatable, Sendable {
    public var url: String
    public var name: String

    public init(url: String, name: String) {
        self.url = url
        self.name = name
    }
}

/// Содержимое копии телефона — `MobileBackup` Android, тот же JSON (решение 559): копию Android открывает iPhone и наоборот.
/// Обертка .pgbackup с шифрованием — у приложения (CryptoKit); этот разбор собирается и на Linux.
public struct MobileBackup: Equatable, Sendable {
    public static let format = "private-gateway-mobile"
    public static let version = 1

    public var createdAt: Date
    public var servers: [BackupServer]?
    public var subscriptions: [BackupSubscription]?
    public var settings: [String: String]?

    public init(createdAt: Date, servers: [BackupServer]? = nil, subscriptions: [BackupSubscription]? = nil, settings: [String: String]? = nil) {
        self.createdAt = createdAt
        self.servers = servers
        self.subscriptions = subscriptions
        self.settings = settings
    }

    /// В копии есть ключи доступа — ссылки серверов.
    public var keys: Bool { servers != nil }

    public func write() -> Data {
        func optional<T>(_ value: T?, _ body: (T) -> JsonText.Value) -> JsonText.Value {
            value.map(body) ?? .null
        }

        let root = JsonText.Value.object([
            ("format", .string(Self.format)),
            ("version", .int(Self.version)),
            ("createdAt", .string(Self.time.string(from: createdAt))),
            ("servers", optional(servers) { list in
                .array(list.map { .object([("link", .string($0.link)), ("name", $0.name.map(JsonText.Value.string) ?? .null), ("favorite", .bool($0.favorite)), ("group", $0.group.map(JsonText.Value.string) ?? .null)]) })
            }),
            ("subscriptions", optional(subscriptions) { list in .array(list.map { .object([("url", .string($0.url)), ("name", .string($0.name))]) }) }),
            ("settings", optional(settings) { values in .object(values.keys.sorted().map { ($0, .string(values[$0] ?? "")) }) }),
        ])
        return Data(JsonText.write(root).utf8)
    }

    /// Разобрать открытую копию; копия десктопа — `BackupError.fromComputer`.
    public static func read(_ content: Data) throws -> MobileBackup {
        guard let root = (try? JSONSerialization.jsonObject(with: content)) as? [String: Any] else {
            throw BackupError.notBackup
        }

        guard root["format"] as? String == format else {
            // Копия десктопа: BackupPayload с CreatedAt и Settings/Groups (имена с заглавной).
            throw root["CreatedAt"] != nil ? BackupError.fromComputer : BackupError.notBackup
        }

        guard let version = (root["version"] as? NSNumber)?.intValue else {
            throw BackupError.notBackup
        }

        if version > Self.version {
            throw BackupError.newerVersion
        }

        guard let created = (root["createdAt"] as? String).flatMap(parseTime) else {
            throw BackupError.notBackup
        }

        let servers = (root["servers"] as? [Any])?.compactMap { item -> BackupServer? in
            guard let server = item as? [String: Any], let link = server["link"] as? String else {
                return nil
            }

            return BackupServer(link: link, name: server["name"] as? String, favorite: server["favorite"] as? Bool ?? false, group: server["group"] as? String)
        }
        let subscriptions = (root["subscriptions"] as? [Any])?.compactMap { item -> BackupSubscription? in
            guard let subscription = item as? [String: Any], let url = subscription["url"] as? String else {
                return nil
            }

            return BackupSubscription(url: url, name: subscription["name"] as? String ?? "")
        }
        let settings = (root["settings"] as? [String: Any])?.compactMapValues { $0 as? String }
        return MobileBackup(createdAt: created, servers: servers, subscriptions: subscriptions, settings: settings)
    }

    /// Время создания: десктоп пишет «2026-11-02T12:00:00.0000000+00:00», Android — «2026-11-02T12:00:00Z».
    public static func parseTime(_ text: String) -> Date? {
        if let date = time.date(from: text) {
            return date
        }

        let fractional = ISO8601DateFormatter()
        fractional.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if let date = fractional.date(from: text) {
            return date
        }

        // Семь знаков дробной части десктопа ISO8601DateFormatter не берет — без дробной части.
        if let dot = text.firstIndex(of: "."), let zone = text[dot...].firstIndex(where: { $0 == "+" || $0 == "-" || $0 == "Z" }) {
            return time.date(from: String(text[..<dot]) + String(text[zone...]))
        }

        return nil
    }

    /// Новый на каждый вызов: ISO8601DateFormatter не Sendable, общий статический Swift 6 не пропустит.
    private static var time: ISO8601DateFormatter {
        let format = ISO8601DateFormatter()
        format.formatOptions = [.withInternetDateTime]
        return format
    }
}

import Foundation

/// Почему экран входа показан снова: сайт отозвал доступ или срок входа истек.
public enum AccountNotice: String, Codable, Sendable {
    case revoked
    case expired

    /// Плашка над формой входа.
    public var textKey: String { "account." + rawValue }
}

/// Сохраненный вход без токена: для «Настроек», проверки срока и расширения туннеля (оно отказывает в подключении без входа).
public struct AccountRecord: Equatable, Codable, Sendable {
    public var email: String
    public var displayName: String
    public var deviceName: String
    public var expiresAt: Date
    /// Когда сайт последний раз подтвердил доступ; nil — после входа еще не проверяли.
    public var checkedAt: Date?

    public init(email: String, displayName: String, deviceName: String, expiresAt: Date, checkedAt: Date?) {
        self.email = email
        self.displayName = displayName
        self.deviceName = deviceName
        self.expiresAt = expiresAt
        self.checkedAt = checkedAt
    }
}

/// Вход: сохраненный вход (nil — входа нет) и почему вход нужен снова (до следующего входа).
public struct AccountState: Equatable, Sendable {
    public var record: AccountRecord?
    public var notice: AccountNotice?

    public init(record: AccountRecord? = nil, notice: AccountNotice? = nil) {
        self.record = record
        self.notice = notice
    }

    public var signedIn: Bool { record != nil }

    /// Можно ли подключаться сейчас: вход есть и срок не прошел.
    public func allowed(now: Date) -> Bool {
        AccountGate.allowed(record?.expiresAt, now: now)
    }
}

/// Где лежит вход (решения 617 и 621, как у Android): токен устройства и uid установки — только в Связке ключей («только на
/// этом устройстве», в резервную копию не уходят); срок, почта, имя и имя устройства — файлом общей папки App Group, чтобы
/// расширение туннеля проверило срок без приложения. Файл не прочитать или он испорчен — «входа нет»: подключаться нельзя,
/// а войти — можно. Резервная копия настроек вход не переносит. Не потокобезопасен: им владеет один актор процесса.
public struct AccountStore: Sendable {
    static let tokenKey = "account.token"
    static let uidKey = "account.device.uid"

    private let file: any StateFile
    private let vault: any SecretVault

    public init(file: any StateFile, vault: any SecretVault) {
        self.file = file
        self.vault = vault
    }

    private struct Content: Codable {
        var record: AccountRecord?
        var notice: AccountNotice?
    }

    /// Вход из файла; нет файла, не прочитать или мусор — входа нет.
    public func read() -> AccountState {
        guard let data = try? file.read(), let content = try? Self.decoder.decode(Content.self, from: data) else {
            return AccountState()
        }

        return AccountState(record: content.record, notice: content.record == nil ? content.notice : nil)
    }

    /// Токен этого входа; входа нет — nil. Связка ключей не отвечает — ошибка.
    public func token() throws -> String? {
        try vault.load(Self.tokenKey)
    }

    /// Сохранить вход: сначала токен в Связку ключей, потом запись. Не вышло — ошибка, вход не сохранен (токен стерт).
    public func save(_ session: AccountSession, now: Date) throws {
        try vault.store(session.token, for: Self.tokenKey)
        let record = AccountRecord(
            email: session.user.email, displayName: session.user.displayName, deviceName: session.device.name, expiresAt: session.expiresAt,
            checkedAt: now
        )
        do {
            try write(Content(record: record, notice: nil))
        } catch {
            try? vault.remove(Self.tokenKey)
            throw error
        }
    }

    /// Сайт подтвердил или не ответил: новый срок, время подтверждения и, если сайт прислал, участник и устройство.
    public func update(_ record: AccountRecord) throws {
        try write(Content(record: record, notice: nil))
    }

    /// Выход, отзыв или истечение: запись — «входа нет» с причиной (`notice`), токен стерт. Сначала файл: без него расширение
    /// туннеля не подключится, даже если Связка ключей сейчас не отвечает. true — оба шага удались.
    @discardableResult
    public func clear(_ notice: AccountNotice?) -> Bool {
        let written = (try? write(Content(record: nil, notice: notice))) != nil
        let removed = (try? vault.remove(Self.tokenKey)) != nil
        return written && removed
    }

    /// uid устройства для сайта: создается один раз на установку (UUID), выход его не стирает. Связка ключей не отвечает — ошибка.
    public func deviceUid() throws -> String {
        if let uid = try vault.load(Self.uidKey), DeviceInfo.validUid(uid) {
            return uid
        }

        let uid = UUID().uuidString.lowercased()
        try vault.store(uid, for: Self.uidKey)
        return uid
    }

    private func write(_ content: Content) throws {
        try file.write(try Self.encoder.encode(content))
    }

    private static let encoder: JSONEncoder = {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .millisecondsSince1970
        encoder.outputFormatting = [.sortedKeys]
        return encoder
    }()

    private static let decoder: JSONDecoder = {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .millisecondsSince1970
        return decoder
    }()
}

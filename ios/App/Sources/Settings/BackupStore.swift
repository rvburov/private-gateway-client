import Foundation
import PrivateGatewayCore
import Security

/// Что положить в копию: серверы и подписки, настройки, ключи доступа (только вместе с серверами) — доска 14.
struct BackupParts: Equatable {
    var servers = true
    var settings = true
    var keys = false
}

/// Итог восстановления: сколько серверов добавлено, сколько подписок вернулось, вернулись ли настройки.
struct BackupRestored: Equatable {
    let servers: Int
    let subscriptions: Int
    let settings: Bool
}

/// Резервная копия iPhone (доска 14, решение 559) — `BackupRepository` Android: файл .pgbackup под паролем (формат десктопа) и
/// автокопия перед каждым восстановлением — со всем, включая ключи, под случайным ключом из Связки ключей; файл — в папке
/// приложения. Долгое (PBKDF2) — не в главном потоке.
extension AppModel {
    private static let autoKey = "backup.autocopy"

    private var autoCopy: URL { container.directory.appendingPathComponent("autocopy.pgbackup") }

    private var vault: KeychainVault { KeychainVault(service: SharedContainer.keychainService, accessGroup: nil) }

    /// Когда сделана последняя автокопия; nil — ее нет.
    func lastAutoCopy() -> Date? {
        (try? String(contentsOf: autoCopy, encoding: .utf8)).flatMap(BackupEnvelope.created)
    }

    /// Текст файла копии под паролем.
    func backupCreate(_ parts: BackupParts, password: String) async throws -> String {
        let copy = try await collect(parts)
        return try await Task.detached { try BackupEnvelope.seal(copy.write(), password: password, created: copy.createdAt) }.value
    }

    /// Открыть копию паролем — что в ней; не та копия — `BackupError`.
    func backupOpen(_ text: String, password: String) async throws -> MobileBackup {
        try await Task.detached { try MobileBackup.read(BackupEnvelope.open(text, password: password)) }.value
    }

    /// Вернуть серверы, подписки и настройки из копии; перед этим текущее — в автокопию: восстановление можно отменить.
    func backupRestore(_ copy: MobileBackup) async throws -> BackupRestored {
        try await saveAutoCopy()
        return try await apply(copy)
    }

    /// Вернуть состояние из автокопии; текущее перед этим само станет автокопией.
    func backupRestoreAuto() async throws -> BackupRestored {
        guard let stored = try vault.load(Self.autoKey), let key = Data(base64Encoded: stored),
              let text = try? String(contentsOf: autoCopy, encoding: .utf8)
        else {
            throw BackupError.notBackup
        }

        return try await backupRestore(MobileBackup.read(BackupEnvelope.open(text, key: key)))
    }

    private func collect(_ parts: BackupParts) async throws -> MobileBackup {
        let groups = try await container.servers.groups()
        // Профиль — группа из ссылок без адреса: ее серверы идут в копию вместе с названием группы.
        var profiles: [String: String] = [:]
        for group in groups where group.maskedUrl == nil {
            profiles[group.id] = group.name
        }

        var subscriptions: [BackupSubscription]?
        if parts.servers {
            var list: [BackupSubscription] = []
            for group in groups where group.maskedUrl != nil {
                if let url = try await container.servers.subscriptionUrl(group.id)?.string {
                    list.append(BackupSubscription(url: url, name: group.name))
                }
            }
            subscriptions = list
        }

        var servers: [BackupServer]?
        if parts.servers, parts.keys {
            var list: [BackupServer] = []
            for item in try await container.servers.servers() where item.subscriptionId == nil || profiles[item.subscriptionId ?? ""] != nil {
                if let link = try await container.servers.shareLink(item.id) {
                    list.append(BackupServer(link: link, name: item.customName, favorite: item.favorite, group: item.subscriptionId.flatMap { profiles[$0] }))
                }
            }
            servers = list
        }

        let settings = parts.settings ? try await container.settings.backupValues() : nil
        return MobileBackup(createdAt: Date(), servers: servers, subscriptions: subscriptions, settings: settings)
    }

    private func apply(_ copy: MobileBackup) async throws -> BackupRestored {
        var added = 0
        for (group, items) in Dictionary(grouping: copy.servers ?? [], by: \.group) {
            let definitions = items.compactMap { LinkParser.parse($0.link).server }
            added += try await container.servers.add(definitions, group: group).added
        }

        var restored = 0
        for subscription in copy.subscriptions ?? [] {
            guard let url = URLComponents(string: subscription.url) else {
                continue
            }

            if try await container.servers.subscriptionFor(url) == nil {
                _ = try await container.servers.add([], group: subscription.name.isEmpty ? (url.host ?? subscription.url) : subscription.name, url: url)
                restored += 1
            }
        }

        if let settings = copy.settings {
            try await container.settings.restore(settings)
        }

        await reload()
        // Серверы подписок загрузятся заново по их адресам — в фоне: восстановление их не ждет.
        let addresses = Set((copy.subscriptions ?? []).compactMap { URLComponents(string: $0.url) })
        let groups = (try? await container.servers.groups()) ?? []
        Task {
            for group in groups where group.maskedUrl != nil {
                if let url = try? await container.servers.subscriptionUrl(group.id), addresses.contains(url) {
                    await updateSubscription(group)
                }
            }
        }

        return BackupRestored(servers: added, subscriptions: restored, settings: copy.settings != nil)
    }

    private func saveAutoCopy() async throws {
        let copy = try await collect(BackupParts(servers: true, settings: true, keys: true))
        let key: Data
        if let stored = try vault.load(Self.autoKey), let saved = Data(base64Encoded: stored) {
            key = saved
        } else {
            var bytes = [UInt8](repeating: 0, count: 32)
            _ = SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes)
            key = Data(bytes)
            try vault.store(key.base64EncodedString(), for: Self.autoKey)
        }

        let text = try BackupEnvelope.seal(copy.write(), key: key, created: copy.createdAt)
        try Data(text.utf8).write(to: autoCopy, options: .atomic)
    }
}

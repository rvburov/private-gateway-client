import Foundation

/// Общее у приложения и расширения туннеля: папка с файлами серверов, настроек и состояния туннеля и секреты. Приложение и
/// расширение — разные процессы, поэтому каждый держит один экземпляр на процесс, а файлы читаются заново на каждое действие.
public struct SharedContainer: Sendable {
    /// App Group приложения и расширения (`ios/project.yml`).
    public static let appGroup = "group.com.privategateway.client"

    /// Служба записей Связки ключей.
    public static let keychainService = "com.privategateway.client"

    /// Имена файлов в общей папке.
    public static let serversFile = "servers.json"
    public static let settingsFile = "settings.json"
    public static let statusFile = "tunnel-status.json"
    public static let coreVersionFile = "core-version.txt"
    public static let languageFile = "language.txt"
    public static let logFile = "tunnel.log"

    public let directory: URL
    public let servers: ServerStore
    public let settings: SettingsStore
    public let status: TunnelStatusFile
    /// Версия Xray-core — ее пишет расширение туннеля после запуска ядра: в процессе приложения ядра нет.
    public let coreVersion: TextFile
    /// Язык текстов приложения («ru», «en») — его пишет приложение: расширению туннеля iOS язык приложения не говорит, а
    /// уведомления расширения должны быть на нем.
    public let language: TextFile
    /// Журнал расширения туннеля для «Проверки» — без секретов.
    public let log: TunnelLog

    public init(directory: URL, vault: any SecretVault) {
        self.directory = directory
        servers = ServerStore(file: DiskFile(url: directory.appendingPathComponent(Self.serversFile)), vault: vault)
        settings = SettingsStore(file: DiskFile(url: directory.appendingPathComponent(Self.settingsFile)))
        status = TunnelStatusFile(file: DiskFile(url: directory.appendingPathComponent(Self.statusFile)))
        coreVersion = TextFile(file: DiskFile(url: directory.appendingPathComponent(Self.coreVersionFile)))
        language = TextFile(file: DiskFile(url: directory.appendingPathComponent(Self.languageFile)))
        log = TunnelLog(file: DiskFile(url: directory.appendingPathComponent(Self.logFile)))
    }

    #if canImport(Security)
    /// Папка App Group и Связка ключей с ее группой доступа. Без прав App Group (сборка без подписи: симулятор, тесты) iOS
    /// папку группы не дает — тогда папка Application Support самого процесса и группа Связки ключей по умолчанию: приложение
    /// работает, но расширение его серверов не видит — как и туннеля без подписи нет.
    public static func current() -> SharedContainer {
        if let group = FileManager.default.containerURL(forSecurityApplicationGroupIdentifier: appGroup) {
            return SharedContainer(
                directory: group.appendingPathComponent("PrivateGateway", isDirectory: true),
                vault: KeychainVault(service: keychainService, accessGroup: appGroup)
            )
        }

        let support = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        return SharedContainer(
            directory: support.appendingPathComponent("PrivateGateway", isDirectory: true),
            vault: KeychainVault(service: keychainService, accessGroup: nil)
        )
    }
    #endif
}

/// Короткий текст в файле общей папки (версия ядра): нет файла или не прочитать — nil.
public struct TextFile: Sendable {
    private let file: any StateFile

    public init(file: any StateFile) {
        self.file = file
    }

    public func read() -> String? {
        guard let data = try? file.read() else {
            return nil
        }

        let text = String(decoding: data, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
        return text.isEmpty ? nil : text
    }

    public func write(_ text: String) throws {
        try file.write(Data(text.utf8))
    }
}

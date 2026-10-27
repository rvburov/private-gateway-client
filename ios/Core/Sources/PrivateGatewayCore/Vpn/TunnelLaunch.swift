import Foundation

/// Отказ туннеля с кодом ошибки (PGC-…, тексты — error.<код>) и подробностью для журнала расширения — уже без секретов
/// (`SecretMasker`).
public struct TunnelFailure: LocalizedError, Equatable, Sendable {
    public let code: String
    public let detail: String?

    public init(_ code: String, detail: String? = nil) {
        self.code = code
        self.detail = detail.map(SecretMasker.mask)
    }

    public var errorDescription: String? { code }
}

/// Что расширению туннеля нужно до маршрутов и ядра — шаги `start` Android: выбранный сервер, его ссылка из Связки ключей,
/// настройки ядра и план туннеля. Расширение собирает конфиг Xray само из описания сервера — готового текста конфига оно не
/// принимает (модель угроз). Сервер, за который ядро не возьмется, отклоняется здесь, до маршрутов: туннель без ядра держал бы
/// трафик впустую.
public struct TunnelLaunch: Sendable {
    public let serverId: String
    public let server: ServerDefinition
    public let core: CoreSettings
    public let plan: TunPlan

    /// Сервер не выбран или удален, его ссылки нет в Связке ключей — PGC-SVC-003; ядро не умеет с ним работать — PGC-SVC-005;
    /// файлы не прочитать (данные под защитой до первой разблокировки) — PGC-SVC-002.
    public static func prepare(settings: SettingsStore, servers: ServerStore) async throws -> TunnelLaunch {
        let app: AppSettings
        do {
            app = try await settings.load()
        } catch {
            throw TunnelFailure(TunnelErrors.coreFailed, detail: "settings: \(error)")
        }

        guard let id = app.selectedServerId else {
            throw TunnelFailure(TunnelErrors.noServer)
        }

        let loaded: ServerDefinition?
        do {
            loaded = try await servers.load(id)
        } catch {
            throw TunnelFailure(TunnelErrors.coreFailed, detail: "servers: \(error)")
        }

        guard let server = loaded else {
            throw TunnelFailure(TunnelErrors.noServer)
        }

        let core = app.tunnel.core()
        if XrayConfig.build(server, core, tunFd: 0).rejection != nil {
            throw TunnelFailure(TunnelErrors.unsupported)
        }

        return TunnelLaunch(serverId: id, server: server, core: core, plan: TunPlan.of(core))
    }

    /// Конфиг Xray с дескриптором интерфейса туннеля.
    public func config(tunFd: Int32) throws -> String {
        guard let json = XrayConfig.build(server, core, tunFd: tunFd).json else {
            throw TunnelFailure(TunnelErrors.unsupported)
        }

        return json
    }

    /// Ответ libXray на запуск: не удалось — PGC-SVC-002 с текстом ядра для журнала.
    public static func started(_ response: String?) throws {
        let parsed = XrayResponse.parse(response)
        guard parsed.success else {
            throw TunnelFailure(TunnelErrors.coreFailed, detail: parsed.error)
        }
    }
}

import Foundation
import PrivateGatewayCore
import PrivateGatewayTheme

/// Состояние окна и действия — как `MainViewModel` Android: серверы, группы и выбор — из общей папки (их же читает расширение
/// туннеля), состояние туннеля — от системы и из отчета расширения (`TunnelView`). Файлы читаются заново на каждое действие и
/// при каждой смене состояния VPN.
@MainActor
final class AppModel: ObservableObject {
    @Published private(set) var home = HomeState()
    @Published private(set) var settings = AppSettings()

    let texts: PgTexts
    let container: SharedContainer
    private let tunnel: TunnelController
    private let actions: ServerActions

    /// Отказ самого приложения (iOS не дала сохранить профиль VPN): показывается как ошибка, пока туннель не поднимется.
    private var appError: String?

    init(container: SharedContainer = .current(), texts: PgTexts = .current) {
        self.container = container
        self.texts = texts
        tunnel = TunnelController(settings: container.settings)
        actions = ServerActions(servers: container.servers, settings: container.settings, control: tunnel)
        tunnel.onChange = { [weak self] in
            self?.refreshStatus()
        }
    }

    /// Окно открылось: профиль VPN, серверы, настройки.
    func start() async {
        await tunnel.load()
        await reload()
    }

    func reload() async {
        // Файл не прочитать (данные под защитой до первой разблокировки) — прежнее на экране остается.
        if let servers = try? await container.servers.servers(), let groups = try? await container.servers.subscriptionRefs() {
            home.servers = servers
            home.subscriptions = groups
        }

        if let loaded = try? await container.settings.load() {
            settings = loaded
            home.selectedId = loaded.selectedServerId
        }

        refreshStatus()
    }

    func refreshStatus() {
        if tunnel.system == .connected || tunnel.system == .connecting {
            appError = nil
        }

        home.status = TunnelView.status(system: tunnel.system, report: container.status.read(), appError: appError)
    }

    /// Кнопка включения: включить или выключить защиту. Подключаться не к чему — `home.power` скажет окну, куда вести.
    func togglePower() async {
        if home.off {
            await run { try await self.tunnel.connect() }
        } else {
            await tunnel.disconnect()
        }
    }

    func select(_ id: String) async {
        let state = home.status.state
        await run { try await self.actions.select(id, state: state) }
    }

    func connectTo(_ id: String) async {
        let state = home.status.state
        await run { try await self.actions.connectTo(id, state: state) }
    }

    func delete(_ id: String) async {
        let status = home.status
        await run { try await self.actions.delete(id, status: status) }
    }

    func setFavorite(_ id: String, _ favorite: Bool) async {
        await run { try await self.actions.setFavorite(id, favorite) }
    }

    func rename(_ id: String, _ name: String) async {
        await run { try await self.actions.rename(id, name) }
    }

    /// Адрес сервера для карточки («адрес:порт») — из ссылки в Связке ключей, только по просьбе открытой карточки.
    func address(_ id: String) async -> String? {
        try? await container.servers.address(id)
    }

    /// Ссылка сервера для «Поделиться» — только по нажатию; ключа нет — nil.
    func shareLink(_ id: String) async -> String? {
        try? await container.servers.shareLink(id)
    }

    /// Действие и перечитанное состояние. Отказ туннеля (iOS не дала поднять VPN) — ошибкой с кодом; файл не записался —
    /// перечитанное состояние покажет, что ничего не изменилось.
    private func run(_ body: () async throws -> Void) async {
        appError = nil
        do {
            try await body()
        } catch let failure as TunnelFailure {
            appError = failure.code
        } catch {}

        await reload()
    }
}

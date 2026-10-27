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
    /// Версия Xray-core — ее пишет расширение туннеля после запуска ядра; до первого подключения — nil.
    @Published private(set) var coreVersion: String?
    /// «Профили и подписки»: группы, сколько серверов в «Моих серверах», какие подписки обновляются кнопкой.
    @Published private(set) var groups: [GroupItem] = []
    @Published private(set) var manualCount = 0
    @Published private(set) var updating: Set<String> = []
    /// Короткое сообщение окна — итог действия, выбранный сервер пропал или изменился; окно показывает его и убирает.
    @Published var message: String?

    let texts: PgTexts
    let container: SharedContainer
    /// Версии и лицензии из пакета приложения — читаются один раз.
    private let bundleInfo: AboutInfo
    private let tunnel: TunnelController
    private let actions: ServerActions
    private let settingsActions: SettingsActions
    private let subscriptionActions: SubscriptionActions

    /// Отказ самого приложения (iOS не дала сохранить профиль VPN): показывается как ошибка, пока туннель не поднимется.
    private var appError: String?

    init(container: SharedContainer = .current(), texts: PgTexts = .current, source: any SubscriptionSource = SubscriptionDownloader()) {
        self.container = container
        self.texts = texts
        bundleInfo = .of(core: nil)
        tunnel = TunnelController(settings: container.settings)
        actions = ServerActions(servers: container.servers, settings: container.settings, control: tunnel)
        settingsActions = SettingsActions(settings: container.settings, control: tunnel)
        subscriptionActions = SubscriptionActions(
            servers: container.servers, settings: container.settings, control: tunnel,
            updater: SubscriptionUpdater(source: source, servers: container.servers, settings: container.settings)
        )
        tunnel.onChange = { [weak self] in
            self?.refreshStatus()
        }
    }

    /// Окно открылось: профиль VPN, серверы, настройки.
    func start() async {
        await tunnel.load()
        await reload()
    }

    /// «О программе»: пакет приложения и версия ядра от расширения туннеля.
    var about: AboutInfo {
        var info = bundleInfo
        info.core = coreVersion
        return info
    }

    func reload() async {
        // Файл не прочитать (данные под защитой до первой разблокировки) — прежнее на экране остается.
        if let servers = try? await container.servers.servers(), let refs = try? await container.servers.subscriptionRefs(),
           let groups = try? await container.servers.groups(), let manual = try? await container.servers.manualCount()
        {
            home.servers = servers
            home.subscriptions = refs
            self.groups = groups
            manualCount = manual
        }

        if let loaded = try? await container.settings.load() {
            settings = loaded
            home.selectedId = loaded.selectedServerId
        }

        coreVersion = container.coreVersion.read()
        refreshStatus()
    }

    func refreshStatus() {
        if tunnel.system == .connected || tunnel.system == .connecting {
            appError = nil
        }

        let before = home.status.state
        home.status = TunnelView.status(system: tunnel.system, report: container.status.read(), appError: appError)
        // Защита подключилась — подпискам «только через VPN» теперь можно обновиться.
        if before != .connected, home.status.state == .connected {
            Task { await checkSubscriptions() }
        }
    }

    private var connected: Bool { home.status.state == .connected }

    /// Не пора ли обновить подписки — окно открыто, защита подключилась, раз в 10 минут, пока окно на экране.
    func checkSubscriptions() async {
        let notices = await subscriptionActions.updater.updateDue(connected: connected)
        await reload()
        if let notice = notices.first {
            say(TextKeys.notice(notice))
        }
    }

    func setSubscriptionSettings(_ next: SubscriptionSettings) async {
        await run { try await self.settingsActions.setSubscriptions(next) }
        await checkSubscriptions()
    }

    /// «Обновить»: «обновлена: новых N, удалено M», «не обновилась», «сначала включите защиту».
    func updateSubscription(_ group: GroupItem) async {
        updating.insert(group.id)
        let report = await subscriptionActions.update(group.id, connected: connected)
        updating.remove(group.id)
        await reload()
        tell(report, name: group.name)
    }

    /// «Изменить»: false — адрес не http(s), ничего не сохранено. Новый адрес сразу проверяется загрузкой.
    func editSubscription(_ group: GroupItem, name: String, url: String) async -> Bool {
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        let result = try? await subscriptionActions.edit(group.id, name: name, url: url, connected: connected)
        await reload()
        if result == .badAddress {
            return false
        }

        if case let .saved(report?)? = result {
            tell(report, name: trimmed.isEmpty ? group.name : trimmed)
        }

        return true
    }

    /// Удалить группу со всеми серверами; nil — «Мои серверы»; `inUse` — защита идет через ее сервер.
    func removeGroup(_ id: String?, name: String, inUse: Bool) async {
        do {
            try await subscriptionActions.remove(group: id, inUse: inUse)
            say(("subs.removed", [name]))
        } catch {}

        await reload()
    }

    private func tell(_ report: UpdateReport, name: String) {
        if let notice = report.notices.first {
            say(TextKeys.notice(notice))
        } else if let text = TextKeys.updated(report.outcome, name: name) {
            say(text)
        }
    }

    private func say(_ text: (key: String, arguments: [String])) {
        message = texts.text(key: text.key, text.arguments)
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

    /// DNS, IPv6 и локальная сеть: под защитой — сразу в работу (ядро перечитывает настройки или туннель переподключается).
    func setTunnel(_ next: TunnelSettings) async {
        let state = home.status.state
        await run { try await self.settingsActions.setTunnel(next, state: state) }
    }

    /// Блок при обрыве — свойство профиля VPN: под защитой туннель переподключается с новым профилем.
    func setKillSwitch(_ on: Bool) async {
        let state = home.status.state
        await run { try await self.settingsActions.setKillSwitch(on, state: state) }
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

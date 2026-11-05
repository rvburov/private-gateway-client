import Foundation
import PrivateGatewayCore
import PrivateGatewayTheme

/// Куда вести окно по нажатию на уведомление.
enum AppRoute: Equatable {
    case servers
    case subscriptions
}

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
    /// Короткое сообщение окна — итог действия, выбранный сервер пропал или изменился; окно показывает его подсказкой сверху
    /// и сбрасывает.
    @Published var message: String?
    /// Сообщение — событие (выбранный сервер пропал из подписки или изменился), а не итог действия: подсказка держится дольше
    /// (6 с вместо 1,8 с), его надо успеть прочесть.
    @Published private(set) var messageIsEvent = false
    /// Нажали уведомление, которое ведет на свой экран; окно переходит туда и сбрасывает.
    @Published var route: AppRoute?
    /// Журнал расширения туннеля для «Проверки»: записи по порядку, последние — в конце.
    @Published private(set) var log: [TunnelLogEntry] = []
    /// Состояние профиля VPN в системе (`NEVPNStatus`) — строка «Туннель» на «Проверке».
    @Published private(set) var systemVpn = SystemVpnStatus.invalid
    /// «Блокировка»: расширение сдалось (сервер не отвечает), а туннель оставлен — блок при обрыве держит трафик закрытым.
    /// Состояние туннеля — «Ошибка», но трафик не идет напрямую; щит снимает защиту, «Повторить» — подключает снова.
    @Published private(set) var blocked = false
    /// Список серверов прочитан: до этого пустой список не значит «серверов нет» (первый запуск, доска 10).
    @Published private(set) var serversLoaded = false
    /// Первый запуск пройден (доска 10): появился первый сервер или выбрано «Позже». Только окну — в UserDefaults приложения.
    @Published private(set) var onboardingDone = UserDefaults.standard.bool(forKey: AppModel.onboardingKey)

    private static let onboardingKey = "onboarding_done"
    /// Дан ли ответ на наш вопрос об уведомлениях (DECISIONS.md, п. 7): больше не спрашивать.
    private static let notificationsAskedKey = "notifications_asked"

    /// Пауза для входа в Wi-Fi (решение 557): до какого времени туннель опущен; nil — паузы нет. Хранится в UserDefaults
    /// приложения: открыли приложение после конца паузы — защита возвращается сама.
    @Published private(set) var pausedUntil = UserDefaults.standard.object(forKey: AppModel.pauseKey) as? Date
    private static let pauseKey = "pause_until"
    private var pauseTask: Task<Void, Never>?

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
    /// «Подключаться при запуске» уже решено за этот запуск приложения: окно, открытое заново, не подключает второй раз.
    private var launchHandled = false

    /// Замер задержки серверов через ядро в процессе приложения — только без защиты (решения 430 и 468).
    private lazy var meter = LatencyMeter(
        servers: container.servers, settings: container.settings, off: { [weak self] in await self?.protectionOff ?? false },
        ping: { await LatencyEngine.ping($0) }
    )

    init(container: SharedContainer = .current(), texts: PgTexts = .current, source: (any SubscriptionSource)? = nil) {
        self.container = container
        self.texts = texts
        bundleInfo = .of(core: nil)
        let tunnel = TunnelController(settings: container.settings)
        self.tunnel = tunnel
        actions = ServerActions(servers: container.servers, settings: container.settings, control: tunnel)
        settingsActions = SettingsActions(settings: container.settings, control: tunnel)
        subscriptionActions = SubscriptionActions(
            servers: container.servers, settings: container.settings, control: tunnel,
            updater: SubscriptionUpdater(
                source: source ?? SubscriptionDownloader(protectedProxy: { await tunnel.subscriptionProxy() }),
                servers: container.servers, settings: container.settings
            )
        )
        tunnel.onChange = { [weak self] in
            self?.refreshStatus()
        }
    }

    /// Окно открылось: язык текстов и кнопки уведомлений (расширению туннеля iOS язык приложения не говорит), профиль VPN,
    /// серверы, настройки.
    func start() async {
        try? container.language.write(texts.language.rawValue)
        Notifications.register(texts)
        AppNotifications.shared.attach(self)
        await tunnel.load()
        // Группы одной подписки, созданные до решения 528 повторным добавлением ссылки, — одна группа.
        try? await container.servers.mergeSameSubscriptions(selectedId: try? await container.settings.load().selectedServerId)
        await reload()
    }

    /// Нажатие на уведомление или его кнопку: «Сменить сервер», «Выбрать сервер», «Выбрать другой» — «Серверы», «Открыть» у
    /// подписок — «Профили и подписки», «Повторить» и «Подключить» — включить защиту; остальное — просто окно.
    /// Первый запуск пройден: приветствие больше не показывается.
    func finishOnboarding() {
        guard !onboardingDone else {
            return
        }

        UserDefaults.standard.set(true, forKey: AppModel.onboardingKey)
        onboardingDone = true
    }

    /// Приветствие первого запуска вместо окна: флаг не стоит, список прочитан и пуст.
    var welcome: Bool {
        !onboardingDone && serversLoaded && home.servers.isEmpty
    }

    func notificationTapped(_ action: String) {
        switch NoticeAction(rawValue: action) {
        case .changeServer?, .chooseServer?, .chooseOther?:
            route = .servers
        case .openSubscriptions?:
            route = .subscriptions
        case .retry?, .connect?:
            Task {
                // «Подключить» в уведомлении о конце паузы для входа в Wi-Fi — конец паузы (решение 557).
                if pausedUntil != nil {
                    await endPause()
                } else if home.off {
                    await retry()
                }
            }
        default:
            break
        }
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
            serversLoaded = true
            if !servers.isEmpty {
                finishOnboarding()
            }
        }

        if let loaded = try? await container.settings.load() {
            settings = loaded
            home.selectedId = loaded.selectedServerId
        }

        coreVersion = container.coreVersion.read()
        refreshLog()
        refreshStatus()
        checkPause()
    }

    /// Пауза для входа в Wi-Fi отеля или кафе (решение 557, `CaptivePortalFlow` десктопа): туннель опускается на 5 минут, чтобы
    /// открылась страница входа в сеть, подключение по требованию на это время выключено. Защита выключена — пауза не нужна.
    func pause() async {
        guard [.connected, .connecting, .reconnecting].contains(home.status.state) || blocked else {
            post(texts.pauseOff)
            return
        }

        setPause(Date().addingTimeInterval(CaptivePause.duration))
        await tunnel.disconnect()
        Notifications.schedulePauseEnd(after: CaptivePause.duration, texts)
        post(texts.pauseStarted, event: true)
        armPause()
        await reload()
    }

    /// Конец паузы — по таймеру, кнопкой «Вернуть защиту» или когда приложение открыли после срока: защита включается снова.
    func endPause() async {
        guard pausedUntil != nil else {
            return
        }

        setPause(nil)
        Notifications.cancelPauseEnd()
        if home.off {
            await retry()
        }
    }

    /// Пауза идет и туннель опущен: на главной «Защита на паузе» с отсчетом, а не «Выключено».
    var paused: Date? {
        home.off ? pausedUntil : nil
    }

    private func setPause(_ until: Date?) {
        pauseTask?.cancel()
        pauseTask = nil
        pausedUntil = until
        if let until {
            UserDefaults.standard.set(until, forKey: AppModel.pauseKey)
        } else {
            UserDefaults.standard.removeObject(forKey: AppModel.pauseKey)
        }
    }

    /// Таймер конца паузы, пока приложение живо.
    private func armPause() {
        pauseTask?.cancel()
        guard let until = pausedUntil else {
            return
        }

        pauseTask = Task { [weak self] in
            let wait = max(0, until.timeIntervalSinceNow)
            try? await Task.sleep(nanoseconds: UInt64(wait * 1_000_000_000))
            guard !Task.isCancelled else {
                return
            }

            await self?.endPause()
        }
    }

    /// После чтения состояния: защиту включили сами — паузы больше нет; срок вышел, пока приложение спало, — вернуть защиту.
    private func checkPause() {
        guard let until = pausedUntil else {
            return
        }

        if !home.off {
            setPause(nil)
            Notifications.cancelPauseEnd()
        } else if until <= Date() {
            Task { await endPause() }
        } else if pauseTask == nil {
            armPause()
        }
    }

    /// Журнал туннеля заново — пока «Проверка» на экране, раз в несколько секунд; тот же — экран не перерисовывается.
    func refreshLog() {
        let entries = container.log.entries()
        if entries != log {
            log = entries
        }
    }

    /// «До сервера» для «Задержки» на «Проверке» — от расширения туннеля; не измерено — nil.
    func serverDelay() async -> Int? {
        await tunnel.serverDelay()
    }

    func refreshStatus() {
        if tunnel.system == .connected || tunnel.system == .connecting {
            appError = nil
        }

        let before = home.status.state
        systemVpn = tunnel.system
        home.status = TunnelView.status(system: tunnel.system, report: container.status.read(), appError: appError)
        // «Ошибка» при поднятом туннеле — расширение оставило его без ядра (TunnelView): трафик закрыт, а не идет напрямую.
        blocked = home.status.state == .error && (tunnel.system == .connected || tunnel.system == .reasserting)
        // Защита подключилась — подпискам «только через VPN» теперь можно обновиться.
        if before != .connected, home.status.state == .connected {
            Task { await checkSubscriptions() }
        }
    }

    private var connected: Bool { home.status.state == .connected }

    private var protectionOff: Bool { home.status.state == .off }

    /// «Обновить серверы» на главной и в «Серверах»: список заново и задержка всех серверов через ядро — только без защиты.
    /// Замер уже идет — нажатие не считается (кнопка не гаснет, крутится только стрелка); замерили — «Серверы обновлены · пинг
    /// проверен», как у десктопа; защита включена или включилась во время замера — сообщение, почему не мерили. Список
    /// перечитывается после каждой порции.
    func refreshLatency() async {
        home.measuring = true
        let result = await meter.measure { [weak self] in
            await self?.reload()
        }
        if result != .running {
            home.measuring = false
        }

        await reload()
        switch result {
        case .done: post(texts.serverRefreshed)
        case .busy: post(texts.androidServersPingBusy)
        case .running: break
        }
    }

    /// Не пора ли обновить подписки — окно открыто, защита подключилась, раз в 10 минут, пока окно на экране.
    func checkSubscriptions() async {
        let notices = await subscriptionActions.updater.updateDue(connected: connected)
        await reload()
        if let notice = notices.first {
            post(TextKeys.window(notice) { texts.text(key: $0, $1) }, event: true)
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
            post(TextKeys.window(notice) { texts.text(key: $0, $1) }, event: true)
        } else if let text = TextKeys.updated(report.outcome, name: name) {
            // «Сначала включите защиту» — просьба, а не итог: держится дольше, как LongDuration десктопа.
            say(text, event: report.outcome == .blocked(.protectionOff))
        }
    }

    private func say(_ text: (key: String, arguments: [String]), event: Bool = false) {
        post(texts.text(key: text.key, text.arguments), event: event)
    }

    /// Сообщение окну: итог действия или событие (`event`) — его подсказка держится дольше.
    /// Событие о защите при открытом окне (уведомление, которое iOS не покажет поверх приложения) — подсказкой сверху на 6 с.
    func showEvent(_ text: String) {
        post(text, event: true)
    }

    private func post(_ text: String?, event: Bool = false) {
        messageIsEvent = event
        message = text
    }

    /// Спросить ли об уведомлениях сейчас — после удачного подключения (DECISIONS.md, п. 7): уведомления не выключены в
    /// настройках, iOS еще не спрашивала, и на наш вопрос ответа не было.
    func notificationsUnasked() async -> Bool {
        guard settings.notifications != .off, !UserDefaults.standard.bool(forKey: AppModel.notificationsAskedKey) else { return false }
        return await Notifications.permissionUndecided()
    }

    /// Ответ на вопрос об уведомлениях: больше не спрашивать; «Разрешить» — системный запрос iOS.
    func answerNotifications(allow: Bool) async {
        UserDefaults.standard.set(true, forKey: AppModel.notificationsAskedKey)
        if allow {
            await Notifications.requestPermission()
        }
    }

    /// Кнопка включения: включить или выключить защиту; в «Блокировке» — снять ее (окно сначала спрашивает). Подключаться не
    /// к чему — `home.power` скажет окну, куда вести.
    func togglePower() async {
        if home.off, !blocked {
            await run { try await self.tunnel.connect() }
        } else {
            await tunnel.disconnect()
        }
    }

    /// «Подключаться при запуске» (доска 04, ConnectOnLaunch десктопа): приложение открыли — защита включается к выбранному
    /// серверу, один раз за запуск. Настройка выключена, защита уже идет (по требованию iOS, из «Пункта управления»), сервер
    /// не выбран или «Блокировка» — ничего.
    func connectOnLaunch() async {
        guard !launchHandled else {
            return
        }

        launchHandled = true
        if Self.connectsOnLaunch(settings, home: home, blocked: blocked) {
            await togglePower()
        }
    }

    /// Подключиться ли при запуске: настройка включена, защита выключена (не «Блокировка» — там кнопка снимает защиту) и
    /// есть выбранный сервер — подключаться есть к чему.
    nonisolated static func connectsOnLaunch(_ settings: AppSettings, home: HomeState, blocked: Bool) -> Bool {
        settings.connectOnLaunch && home.off && !blocked && home.power == .toggle
    }

    /// «Повторить» в «Ошибке» и «Блокировке», «Повторить» и «Подключить» в уведомлении: подключиться снова — туннель
    /// поднимается, а оставленный без ядра перезапускает ядро, не опускаясь (`TunnelController.connect`).
    func retry() async {
        await run { try await self.tunnel.connect() }
    }

    func select(_ id: String) async {
        let state = home.status.state
        await run { try await self.actions.select(id, state: state) }
    }

    func connectTo(_ id: String) async {
        let state = home.status.state
        await run { try await self.actions.connectTo(id, state: state) }
    }

    /// Удалить сервер (после «да» в карточке); удалился — «Сервер удален».
    func delete(_ id: String) async {
        let status = home.status
        if await run({ try await self.actions.delete(id, status: status) }) {
            post(texts.cardDeleted)
        }
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

    /// «Постоянная защита» — под защитой сразу в профиль VPN, туннель не опускается.
    func setAlwaysOn(_ on: Bool) async {
        let state = home.status.state
        await run { try await self.settingsActions.setAlwaysOn(on, state: state) }
    }

    /// «Подключаться при запуске» — только для окна, расширению туннеля она не нужна.
    func setConnectOnLaunch(_ on: Bool) async {
        await run { try await self.container.settings.setConnectOnLaunch(on) }
    }

    /// Какие уведомления показывать — расширение читает уровень при каждом событии.
    func setNotifications(_ level: NotificationLevel) async {
        await run { try await self.container.settings.setNotifications(level) }
    }

    /// Что делать при обрыве — расширение читает правило на каждой неудаче, переподключаться не нужно.
    func setReconnect(_ policy: ReconnectPolicy) async {
        await run { try await self.container.settings.setReconnect(policy) }
    }

    /// «Подключение и ядро»: под защитой то, что получают ядро и туннель (журнал, MTU, определение домена, отпечаток TLS, Mux,
    /// фрагментация), сразу в работу — ядро перезапускается в том же туннеле; таймаут расширение читает само на каждой пробе.
    func setConnection(_ next: ConnectionSettings) async {
        let state = home.status.state
        await run { try await self.settingsActions.setConnection(next, state: state) }
    }

    /// «Маршрутизация»: режим и правила для сайтов — под защитой сразу в работу, как настройки туннеля.
    func setRouting(_ next: RoutingSettings) async {
        let state = home.status.state
        await run { try await self.settingsActions.setRouting(next, state: state) }
    }

    /// Адрес сервера для карточки («адрес:порт») — из ссылки в Связке ключей, только по просьбе открытой карточки.
    func address(_ id: String) async -> String? {
        try? await container.servers.address(id)
    }

    /// Ссылка сервера для «Поделиться» — только по нажатию; ключа нет — nil.
    func shareLink(_ id: String) async -> String? {
        try? await container.servers.shareLink(id)
    }

    /// Действие и перечитанное состояние; true — действие прошло без ошибки. Отказ туннеля (iOS не дала поднять VPN) — ошибкой
    /// с кодом; файл не записался — перечитанное состояние покажет, что ничего не изменилось.
    @discardableResult
    private func run(_ body: () async throws -> Void) async -> Bool {
        appError = nil
        var done = false
        do {
            try await body()
            done = true
        } catch let failure as TunnelFailure {
            appError = failure.code
        } catch is VpnPermissionDenied {
            // Защита остается «Выключено»; следующее нажатие щита спросит снова (DECISIONS.md, п. 5).
            post(texts.iosPermissionDenied, event: true)
        } catch {}

        await reload()
        return done
    }
}

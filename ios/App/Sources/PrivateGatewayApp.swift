import PrivateGatewayCore
import PrivateGatewayTheme
import SwiftUI
import UIKit
import UserNotifications

/// Приложение iOS (этап 10): окно — главная по доске 25; туннель — расширение PacketTunnel (NEPacketTunnelProvider), им
/// управляет профиль VPN в настройках iOS (`TunnelController`).
@main
struct PrivateGatewayApp: App {
    @StateObject private var model = AppModel()

    init() {
        // До конца запуска: нажатие на уведомление, которое запустило приложение, приходит сразу.
        UNUserNotificationCenter.current().delegate = AppNotifications.shared
    }

    var body: some Scene {
        WindowGroup {
            RootView(model: model)
                .dynamicTypeSize(...PgTextStyle.largestSize)
        }
        .backgroundTask(.appRefresh(BackgroundRefresh.task)) {
            await BackgroundRefresh.run()
        }
    }
}

/// Окно — как `AppScreen` Android: разделы островка (главная, серверы, проверка, настройки), карточка сервера нижним листом поверх
/// списка, «Добавить» поверх всего. Вернулись в приложение — серверы, настройки и состояние перечитываются: их мог поменять «Пункт управления» или
/// расширение туннеля.
struct RootView: View {
    @ObservedObject var model: AppModel
    @StateObject private var importing: ImportModel
    @StateObject private var inspector = InspectorModel()
    /// Скорость туннеля для главной — по счетчикам интерфейса туннеля, пока главная на экране под защитой.
    @StateObject private var speed = SpeedMeter()
    /// Слои окна: диалог подтверждения, меню у кнопки и подсказка сверху — их зовут блоки любого экрана.
    @StateObject private var layers = PgLayers()
    @State private var tab = AppTab.home
    @State private var adding = false
    /// Карточка сервера (доска 11) — нижний лист поверх списка серверов: какой сервер и открыт ли лист. Сервер остается, пока
    /// лист уезжает вниз, — лист не пустеет на ходу.
    @State private var card: String?
    @State private var cardOpen = false
    @State private var query = ServerListQuery()
    /// Открытая страница «Настроек».
    @State private var settingsPage = SettingsPage.main
    /// «Проверка»: прокси системы, задержка, идет ли проверка; открытая страница и прокрутка к «Задержке» с плитки «Пинг».
    @StateObject private var check = CheckModel()
    @State private var checkPage = CheckPage.main
    @State private var focusDelay = false
    @Environment(\.scenePhase) private var phase

    init(model: AppModel) {
        self.model = model
        _importing = StateObject(wrappedValue: ImportModel(container: model.container))
    }

    var body: some View {
        ZStack(alignment: .bottom) {
            PgBackground()
            if adding {
                ImportView(model: importing, texts: model.texts, onClose: closeImport)
            } else if model.welcome {
                WelcomeView(texts: model.texts, onAdd: { adding = true }, onDone: { model.finishOnboarding() })
            } else {
                page
                Island(
                    texts: model.texts, tab: tab, state: model.home.status.state, hasServer: model.home.inUse != nil, blocked: model.blocked,
                    leak: homeFacts.leak(model.home.status.state), paused: model.paused != nil,
                    onTab: { next in
                        tab = next
                        cardOpen = false
                        settingsPage = .main
                        checkPage = .main
                        layers.closeMenu()
                    },
                    onPower: power
                )
            }
        }
        .pgLayers(layers)
        .task {
            importing.onAdded = { result, existing in
                Task { await model.reload() }
                imported(result, existing: existing)
            }
            // Сеть телефона — для строки сети главной и «Проверки»; следим, пока окно живо.
            inspector.start()
            await model.start()
            // «Подключаться при запуске» — когда приложение открыли, а не iOS разбудила его в фоне обновить подписки.
            if UIApplication.shared.applicationState != .background {
                await model.connectOnLaunch()
            }

            measureWebIp()
        }
        .onChange(of: model.home.status.state) { state in
            // Внешний IP главной: подключились — адрес выхода, выключились — реальный; прежний адрес выхода больше не тот.
            if state != .connected {
                inspector.forgetExit()
            }

            // «Проверка» на экране — проверить заново: на экране не остается устаревший «OK» (внешний IP меряет она же).
            if tab == .check {
                runChecks()
            } else {
                measureWebIp()
            }
        }
        .onChange(of: model.home.status.serverId) { _ in
            // Под защитой сменили сервер, туннель не опускался: у нового сервера — свой адрес выхода.
            if model.home.status.state == .connected {
                measureWebIp()
            }
        }
        .onChange(of: inspector.networkKey) { _ in
            // Другая сеть — другой реальный IP (и под защитой — повод проверить выход заново).
            measureWebIp()
        }
        .task(id: journalWanted) {
            // Журнал туннеля обновляется сам, пока «Проверка» на экране: расширение пишет его в общую папку.
            guard journalWanted else {
                return
            }

            while !Task.isCancelled {
                model.refreshLog()
                try? await Task.sleep(nanoseconds: 2_000_000_000)
            }
        }
        .task(id: speedWanted) {
            guard speedWanted else {
                speed.stop()
                return
            }

            await speed.run()
        }
        .onChange(of: model.home.servers) { _ in
            // Список серверов изменился, пока открыт предпросмотр «Добавить» (обновление подписки, удаление): дубли и «уже
            // добавлена» — заново, иначе кнопка обещала бы не то, что сделает добавление.
            if adding {
                Task { await importing.recount() }
            }
        }
        .onChange(of: model.message) { message in
            // Сообщение модели — подсказкой сверху окна (доска 19): итог действия — 1,8 с, событие (выбранный сервер пропал из
            // подписки или изменился) — 6 с. Модель сразу освобождается для следующего, в том числе такого же.
            guard let message else {
                return
            }

            layers.show(message, for: model.messageIsEvent ? PgToastDuration.event : PgToastDuration.action)
            model.message = nil
        }
        .task(id: phase) {
            // Пока окно на экране — не пора ли обновить подписки: сразу и раз в 10 минут.
            guard phase == .active else {
                return
            }

            while !Task.isCancelled {
                await model.checkSubscriptions()
                try? await Task.sleep(nanoseconds: UInt64(SubscriptionUpdater.checkEvery * 1_000_000_000))
            }
        }
        .onChange(of: model.route) { route in
            // Нажали уведомление: «Серверы» или «Профили и подписки».
            guard let route else {
                return
            }

            adding = false
            cardOpen = false
            switch route {
            case .servers:
                tab = .servers
            case .subscriptions:
                tab = .settings
                settingsPage = .subscriptions
            }

            model.route = nil
        }
        .onChange(of: phase) { phase in
            if phase == .active {
                Task { await model.reload() }
            } else if phase == .background {
                BackgroundRefresh.schedule()
            }
        }
    }

    @ViewBuilder
    private var page: some View {
        switch tab {
        case .home:
            HomeView(
                texts: model.texts, home: model.home, killSwitch: model.settings.killSwitch, facts: homeFacts, onAdd: { adding = true },
                onServers: { tab = .servers }, onSelect: { id in Task { await model.select(id) } },
                onRefresh: {
                    // «Обновить серверы»: задержка серверов (без защиты) и внешний IP заново.
                    Task { await model.refreshLatency() }
                    measureWebIp()
                },
                onCheck: {
                    // «Пинг» — подробности на «Проверке»: экран прокручивается к «Задержке», замер идет вместе с проверкой.
                    focusDelay = true
                    checkPage = .main
                    tab = .check
                },
                onCheckNow: measureWebIp, onRetry: { Task { await model.retry() } }, onCaptive: askPause,
                onResume: { Task { await model.endPause() } }
            )
        case .servers:
            ServersView(
                texts: model.texts, home: model.home, query: $query, onSelect: { id in Task { await model.select(id) } },
                onOpen: { id in
                    card = id
                    cardOpen = true
                },
                onAdd: { adding = true }, onRefresh: { Task { await model.refreshLatency() } }
            )
            .pgSheet(isPresented: $cardOpen) {
                if let card {
                    ServerCardView(texts: model.texts, home: model.home, id: card, actions: cardActions)
                }
            }
        case .check:
            CheckView(
                texts: model.texts, input: checkInput, findings: check.findings, server: model.home.inUse, networks: inspector.networks, log: model.log,
                about: model.about, page: $checkPage, probe: $inspector.probe, focusDelay: focusDelay, onRun: runChecks,
                onDelay: {
                    Task {
                        await check.measureDelay(connected: model.home.status.state == .connected, toServer: { await model.serverDelay() })
                    }
                },
                onFocused: { focusDelay = false },
                onMeasure: { Task { await inspector.measureIp(protected: model.home.status.state == .connected) } },
                onDescribe: { Task { await inspector.describe() } }, onProbe: { Task { await inspector.runProbe() } }
            )
            .onAppear {
                inspector.start()
                // Экран открыли — проверить сразу: проверки только читают состояние.
                runChecks()
            }
        case .settings:
            SettingsView(
                texts: model.texts, settings: model.settings, about: model.about, page: $settingsPage,
                onTunnel: { next in Task { await model.setTunnel(next) } }, onKillSwitch: { on in Task { await model.setKillSwitch(on) } },
                onAlwaysOn: { on in Task { await model.setAlwaysOn(on) } }, onConnectOnLaunch: { on in Task { await model.setConnectOnLaunch(on) } },
                onNotifications: { level in Task { await model.setNotifications(level) } }, onReconnect: { policy in Task { await model.setReconnect(policy) } },
                onConnection: { next in Task { await model.setConnection(next) } }, onRouting: { next in Task { await model.setRouting(next) } },
                onCheck: {
                    checkPage = .main
                    tab = .check
                },
                onCaptive: askPause,
                backup: { onBack in AnyView(BackupView(texts: model.texts, model: model, onBack: onBack)) }
            ) { onBack in
                SubscriptionsView(
                    texts: model.texts, settings: model.settings.subscriptions, groups: model.groups, manualCount: model.manualCount,
                    updating: model.updating, home: model.home, actions: subscriptionActions, onBack: onBack
                )
            }
        }
    }

    private var subscriptionActions: SubscriptionsActions {
        SubscriptionsActions(
            settings: { next in Task { await model.setSubscriptionSettings(next) } },
            add: { adding = true },
            update: { group in Task { await model.updateSubscription(group) } },
            edit: { group, name, url in await model.editSubscription(group, name: name, url: url) },
            remove: { id, name, inUse in Task { await model.removeGroup(id, name: name, inUse: inUse) } }
        )
    }

    private var cardActions: CardActions {
        CardActions(
            back: { cardOpen = false },
            connect: { id in
                // «Подключиться» закрывает лист (data-close макета, CloseSheet десктопа) — видно, как поднимается защита.
                cardOpen = false
                Task { await model.connectTo(id) }
            },
            favorite: { id, favorite in Task { await model.setFavorite(id, favorite) } },
            rename: { id, name in Task { await model.rename(id, name) } },
            delete: { id in
                cardOpen = false
                Task { await model.delete(id) }
            },
            address: { id in await model.address(id) },
            share: { id in await model.shareLink(id) }
        )
    }

    /// Что главная знает сверх состояния туннеля: «Блокировка», сеть, внешний IP (сведения RIPEstat — если о нем спросили на
    /// «Проверке») и скорость туннеля.
    private var homeFacts: HomeFacts {
        var ip = inspector.web
        ip.info = inspector.ip.info
        return HomeFacts(blocked: model.blocked, uplink: inspector.uplink, ip: ip, speed: speed.speed, paused: model.paused)
    }

    /// Что «Проверка» знает о защите и сети: состояние туннеля и профиля VPN, «Блокировка», «Утечка IP», внешний IP и реальный
    /// IP этой сети, прокси и VPN других приложений, идет ли проверка.
    private var checkInput: CheckInput {
        let state = model.home.status.state
        return CheckInput(
            state: state, system: model.systemVpn, error: model.home.status.error, blocked: model.blocked, leak: homeFacts.leak(state),
            killSwitch: model.settings.killSwitch, core: model.coreVersion, ip: inspector.ip, direct: inspector.web.direct, proxy: check.findings.proxy,
            running: check.findings.running
        )
    }

    /// Журнал туннеля обновляется, пока «Проверка» на экране.
    private var journalWanted: Bool {
        tab == .check && !adding && phase == .active
    }

    /// «Проверить», открытие «Проверки» и смена состояния защиты на ней: прокси и VPN других приложений, внешний IP, журнал,
    /// затем задержка (`CheckModel`).
    private func runChecks() {
        model.refreshLog()
        Task {
            await check.run(
                connected: { model.home.status.state == .connected }, measureIp: { await measureIpNow() }, toServer: { await model.serverDelay() }
            )
        }
    }

    /// Скорость нужна, пока главная на экране под защитой.
    private var speedWanted: Bool {
        tab == .home && !adding && phase == .active && model.home.status.state == .connected
    }

    /// Внешний IP для главной: под защитой — адрес выхода (он же проверка утечки), без защиты и в «Ошибке» без туннеля —
    /// реальный. В «Подключении», «Переподключении» и «Блокировке» мерить нечего: прямой путь закрыт, а выхода еще нет.
    private func measureWebIp() {
        let state = model.home.status.state
        guard state == .connected || state == .off || (state == .error && !model.blocked) else {
            return
        }

        Task { await inspector.measureIp(protected: state == .connected) }
    }

    /// То же, но с ожиданием итога — для «Проверки»: строка «IP для сайтов» ждет замера.
    private func measureIpNow() async {
        let state = model.home.status.state
        guard state == .connected || state == .off || (state == .error && !model.blocked) else {
            return
        }

        await inspector.measureIp(protected: state == .connected)
    }

    /// Кнопка-щит: подключаться не к чему — серверов нет (добавить) или ни один не выбран (к списку: сервер выбирает только
    /// пользователь). В «Блокировке» снять защиту — открыть сайтам реальный IP: сначала вопрос (доска 18), как у десктопа.
    private func power() {
        switch model.home.power {
        case .toggle:
            if model.blocked {
                layers.confirm(HomeDialogs.unprotect(model.texts)) {
                    Task { await model.togglePower() }
                }
            } else {
                Task { await model.togglePower() }
            }
        case .add:
            adding = true
        case .choose:
            tab = .servers
            cardOpen = false
        }
    }

    /// «Вход в Wi-Fi» (главная, «Защита и утечки»): защита выключена — пауза не нужна; иначе вопрос, как у десктопа (доска 18):
    /// «Открыть на 5 минут» — опасная вторая кнопка (решение 557).
    private func askPause() {
        guard !model.home.off || model.blocked else {
            layers.show(model.texts.pauseOff)
            return
        }

        layers.confirm(HomeDialogs.pause(model.texts)) {
            Task { await model.pause() }
        }
    }

    /// «Назад» из «Добавить профиль» — туда, откуда открыли (вкладка и страница настроек остались прежними), как Back десктопа.
    private func closeImport() {
        adding = false
        importing.reset()
    }

    /// «Добавить» сохранило серверы (доска 03, ImportPage десктопа): итог подсказкой сверху, экран «Добавить профиль» сброшен,
    /// вкладка — «Серверы».
    private func imported(_ result: AddResult, existing: String?) {
        layers.show(ImportLook.added(result, existing: existing, model.texts))
        closeImport()
        tab = .servers
        cardOpen = false
        settingsPage = .main
        layers.closeMenu()
    }
}

extension PgTexts {
    /// Тексты на языке приложения: системный выбор языка для приложения (Настройки → Private Gateway → Язык) или первый из
    /// предпочитаемых языков iPhone; русский или английский, иначе английский — как values и values-ru у Android.
    static let current = PgTexts(PgTexts.language(for: Bundle.main.preferredLocalizations + Locale.preferredLanguages))
}

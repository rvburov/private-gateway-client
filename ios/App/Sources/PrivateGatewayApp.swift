import PrivateGatewayCore
import PrivateGatewayTheme
import SwiftUI
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
        }
        .backgroundTask(.appRefresh(BackgroundRefresh.task)) {
            await BackgroundRefresh.run()
        }
    }
}

/// Окно — как `AppScreen` Android: разделы островка (главная, серверы, настройки), карточка сервера поверх списка, «Добавить» поверх
/// всего. Вернулись в приложение — серверы, настройки и состояние перечитываются: их мог поменять «Пункт управления» или
/// расширение туннеля.
struct RootView: View {
    @ObservedObject var model: AppModel
    @StateObject private var importing: ImportModel
    @State private var tab = AppTab.home
    @State private var adding = false
    /// Открытая карточка сервера (доска 11) — страница поверх списка серверов.
    @State private var card: String?
    @State private var query = ServerListQuery()
    /// Открытая страница «Настроек».
    @State private var settingsPage = SettingsPage.main
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
            } else {
                page
                Island(
                    texts: model.texts, tab: tab, state: model.home.status.state, hasServer: model.home.inUse != nil,
                    onTab: { next in
                        tab = next
                        card = nil
                        settingsPage = .main
                    },
                    onPower: power
                )
            }
        }
        .task {
            importing.onAdded = { [model] in
                Task { await model.reload() }
            }
            await model.start()
        }
        .overlay(alignment: .top) {
            if let message = model.message {
                MessageBanner(text: message) { model.message = nil }
            }
        }
        .task(id: model.message) {
            // Сообщение висит 4 секунды, как короткое сообщение Android.
            guard model.message != nil else {
                return
            }

            try? await Task.sleep(nanoseconds: 4_000_000_000)
            if !Task.isCancelled {
                model.message = nil
            }
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
            card = nil
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
                texts: model.texts, home: model.home, killSwitch: model.settings.killSwitch, onAdd: { adding = true }, onServers: { tab = .servers },
                onSelect: { id in Task { await model.select(id) } }
            )
        case .servers:
            if let card {
                ServerCardView(texts: model.texts, home: model.home, id: card, actions: cardActions)
            } else {
                ServersView(
                    texts: model.texts, home: model.home, query: $query, onSelect: { id in Task { await model.select(id) } }, onOpen: { card = $0 },
                    onAdd: { adding = true }
                )
            }
        case .settings:
            SettingsView(
                texts: model.texts, settings: model.settings, about: model.about, page: $settingsPage,
                onTunnel: { next in Task { await model.setTunnel(next) } }, onKillSwitch: { on in Task { await model.setKillSwitch(on) } },
                onNotifications: { level in Task { await model.setNotifications(level) } }, onReconnect: { policy in Task { await model.setReconnect(policy) } }
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
            back: { card = nil },
            connect: { id in Task { await model.connectTo(id) } },
            favorite: { id, favorite in Task { await model.setFavorite(id, favorite) } },
            rename: { id, name in Task { await model.rename(id, name) } },
            delete: { id in
                card = nil
                Task { await model.delete(id) }
            },
            address: { id in await model.address(id) },
            share: { id in await model.shareLink(id) }
        )
    }

    /// Кнопка-щит: подключаться не к чему — серверов нет (добавить) или ни один не выбран (к списку: сервер выбирает только
    /// пользователь).
    private func power() {
        switch model.home.power {
        case .toggle:
            Task { await model.togglePower() }
        case .add:
            adding = true
        case .choose:
            tab = .servers
            card = nil
        }
    }

    private func closeImport() {
        adding = false
        importing.reset()
    }
}

extension PgTexts {
    /// Тексты на языке приложения: системный выбор языка для приложения (Настройки → Private Gateway → Язык) или первый из
    /// предпочитаемых языков iPhone; русский или английский, иначе английский — как values и values-ru у Android.
    static let current = PgTexts(PgTexts.language(for: Bundle.main.preferredLocalizations + Locale.preferredLanguages))
}

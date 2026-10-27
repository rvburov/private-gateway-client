import PrivateGatewayCore
import PrivateGatewayTheme
import SwiftUI

/// Приложение iOS (этап 10): окно — главная по доске 25; туннель — расширение PacketTunnel (NEPacketTunnelProvider), им
/// управляет профиль VPN в настройках iOS (`TunnelController`).
@main
struct PrivateGatewayApp: App {
    @StateObject private var model = AppModel()

    var body: some Scene {
        WindowGroup {
            RootView(model: model)
        }
    }
}

/// Окно — как `AppScreen` Android: разделы островка (главная, серверы), карточка сервера поверх списка, «Добавить» поверх
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
        .onChange(of: phase) { phase in
            if phase == .active {
                Task { await model.reload() }
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
        }
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

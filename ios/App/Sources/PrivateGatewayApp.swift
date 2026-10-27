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

/// Окно: главная и действия модели. Вернулись в приложение — серверы, настройки и состояние перечитываются: их мог поменять
/// «Пункт управления» или расширение туннеля.
struct RootView: View {
    @ObservedObject var model: AppModel
    @Environment(\.scenePhase) private var phase

    var body: some View {
        HomeView(
            texts: model.texts,
            home: model.home,
            killSwitch: model.settings.killSwitch,
            onPower: power,
            onSelect: { id in Task { await model.select(id) } }
        )
        .task { await model.start() }
        .onChange(of: phase) { phase in
            if phase == .active {
                Task { await model.reload() }
            }
        }
    }

    /// Кнопка включения: подключаться не к чему — добавление или список (экраны — следующим шагом этапа 10).
    private func power() {
        if model.home.power == .toggle {
            Task { await model.togglePower() }
        }
    }
}

extension PgTexts {
    /// Тексты на языке приложения: системный выбор языка для приложения (Настройки → Private Gateway → Язык) или первый из
    /// предпочитаемых языков iPhone; русский или английский, иначе английский — как values и values-ru у Android.
    static let current = PgTexts(PgTexts.language(for: Bundle.main.preferredLocalizations + Locale.preferredLanguages))
}

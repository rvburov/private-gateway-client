import PrivateGatewayCore
import PrivateGatewayTheme
import SwiftUI

/// Приложение iOS (этап 10): окно — главная по доске 25; туннель — расширение PacketTunnel (NEPacketTunnelProvider).
@main
struct PrivateGatewayApp: App {
    var body: some Scene {
        WindowGroup {
            HomeView(texts: .current)
        }
    }
}

extension PgTexts {
    /// Тексты на языке приложения: системный выбор языка для приложения (Настройки → Private Gateway → Язык) или первый из
    /// предпочитаемых языков iPhone; русский или английский, иначе английский — как values и values-ru у Android.
    static let current = PgTexts(PgTexts.language(for: Bundle.main.preferredLocalizations + Locale.preferredLanguages))
}

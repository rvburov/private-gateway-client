import Foundation
import PrivateGatewayTheme
import SwiftUI

/// Сколько держится подсказка сверху окна (доска 19, ToastViewModel десктопа): итог действия («IP скопирован») — 1,8 с,
/// событие («Выбранный сервер пропал из подписки…») — 6 с, его надо успеть прочесть.
enum PgToastDuration {
    static let action: TimeInterval = 1.8
    static let event: TimeInterval = 6
}

/// Подсказка сверху окна (.toast): темная плашка text.primary, белый текст small Medium по центру, скругление radius.input,
/// тень shadow.popup; по ширине текста, длинная переносится в полях 20 от краев окна. Нажатия проходят сквозь нее. Показ и
/// срок — у `PgLayers`.
struct PgToastView: View {
    /// Отступ от верха окна под полосой состояния (app.css .toast, top 56).
    static let top: CGFloat = 56

    let text: String

    var body: some View {
        Text(text)
            .pgText(PgType.small.with(weight: 500))
            .foregroundStyle(Color(PgColor.Surface.solid))
            .multilineTextAlignment(.center)
            .fixedSize(horizontal: false, vertical: true)
            .padding(.horizontal, 14)
            .padding(.vertical, PgSpace.s)
            .background(RoundedRectangle(cornerRadius: PgRadius.input, style: .continuous).fill(Color(PgColor.Text.primary)))
            .pgShadow(PgShadow.popup)
            .padding(.horizontal, PgSpace.windowSide)
            .accessibilityIdentifier("message")
    }
}

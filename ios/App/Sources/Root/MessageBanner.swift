import PrivateGatewayTheme
import SwiftUI

/// Короткое сообщение окна сверху — итог действия или что случилось с выбранным сервером, как короткое сообщение Android.
/// Нажатие убирает его раньше срока.
struct MessageBanner: View {
    let text: String
    let onClose: () -> Void

    var body: some View {
        Button(action: onClose) {
            Text(text)
                .pgText(PgType.small)
                .foregroundStyle(Color(PgColor.Text.primary))
                .multilineTextAlignment(.leading)
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(PgSpace.m)
                .background(RoundedRectangle(cornerRadius: PgRadius.card, style: .continuous).fill(Color(PgColor.Surface.solid)))
                .overlay(RoundedRectangle(cornerRadius: PgRadius.card, style: .continuous).stroke(Color(PgColor.Line.border), lineWidth: 1))
                .pgShadow(PgShadow.card)
        }
        .buttonStyle(.plain)
        .padding(.horizontal, PgSpace.windowSideMobile)
        .padding(.top, PgSpace.s)
        .accessibilityIdentifier("message")
    }
}

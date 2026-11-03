import PrivateGatewayCore
import PrivateGatewayTheme
import SwiftUI

/// «Работает другой VPN» на iPhone (доска 16, DECISIONS.md, п. 4) — вместо главной, пока защита стоит на PGC-NET-010: VPN
/// телефона занят другим приложением — не дал подключиться или вытеснил наш во время работы (iOS остановила туннель с
/// причиной `superceded`). Знак-треугольник в оранжевой плитке 72, заголовок display, пояснение для iOS, факты с оранжевыми
/// кружками, «Я закрыл его — проверить снова» и ссылка на журнал. Открыть настройки VPN приложению iOS не дает — путь в
/// тексте. Островок под страницей работает.
struct OtherVpnView: View {
    let texts: PgTexts
    /// «Я закрыл его — проверить снова»: VPN свободен — подключить, занят — подсказка, страница остается.
    let onRetry: () -> Void
    /// «Подробнее в журнале · код PGC-NET-010» — вкладка «Проверка» с журналом туннеля.
    let onDetails: () -> Void

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 0) {
                HStack(spacing: PgSpace.m) {
                    ZStack {
                        PgIcon(data: PgIcons.shield, color: Color(PgColor.Accent.base), size: 28)
                        PgIcon(data: PgIcons.markOk, color: Color(PgColor.Accent.base), size: 28)
                    }
                    .accessibilityHidden(true)
                    Text(texts.appTitle).pgText(PgType.brand).foregroundStyle(Color(PgColor.Text.primary)).lineLimit(1)
                }
                .frame(height: 60)

                VStack(spacing: PgSpace.m) {
                    PgIcon(data: PgIcons.warning, color: Color(PgColor.Status.Caution.text), size: 34, lineWidth: 1.9)
                        .frame(width: 72, height: 72)
                        .background(RoundedRectangle(cornerRadius: 20, style: .continuous).fill(Color(PgColor.Status.Caution.tint)))
                        .accessibilityHidden(true)

                    Text(texts.problemConflictTitleMobile)
                        .pgText(PgType.display)
                        .foregroundStyle(Color(PgColor.Text.primary))
                        .multilineTextAlignment(.center)
                        .frame(maxWidth: .infinity)
                        .padding(.top, PgSpace.xs)
                        .accessibilityAddTraits(.isHeader)
                        .accessibilityIdentifier("other_vpn.title")
                    Text(texts.problemConflictTextIos)
                        .pgText(PgType.body)
                        .foregroundStyle(Color(PgColor.Text.secondary))
                        .multilineTextAlignment(.center)
                        .frame(maxWidth: 360)
                        .accessibilityIdentifier("other_vpn.text")

                    VStack(spacing: 0) {
                        fact(texts.problemFactVpnMobile, texts.problemFactVpnMobileOther)
                        fact(texts.problemFactProtectionMobile, texts.problemFactProtectionMobileOff)
                    }
                    .padding(.horizontal, PgSpace.l)
                    .padding(.vertical, 4)
                    .pgCard(padding: 0)
                    .padding(.top, PgSpace.xs)

                    PgActionButton(label: texts.problemConflictRetry, identifier: "other_vpn.retry", height: 50, action: onRetry)
                        .padding(.top, PgSpace.l)

                    Button(action: onDetails) {
                        Text(texts.problemDetails(TunnelErrors.otherVpn))
                            .pgText(PgTextStyle(size: PgType.small.size, weight: 600))
                            .foregroundStyle(Color(PgColor.Accent.text))
                            .multilineTextAlignment(.center)
                            .frame(minHeight: PgLayout.touchTarget)
                    }
                    .buttonStyle(PgPressStyle())
                    .accessibilityIdentifier("other_vpn.details")
                }
                .padding(.top, 20)
                .padding(.horizontal, 4)
            }
            .padding(.horizontal, PgSpace.windowSideMobile)
            .padding(.bottom, PgLayout.scrollBottomPadding)
        }
        .accessibilityIdentifier("other_vpn")
    }

    /// Факт (.pr-fact): оранжевый кружок 8, что проверено, итог моноширинным справа.
    private func fact(_ label: String, _ value: String) -> some View {
        HStack(spacing: PgSpace.m) {
            Circle().fill(Color(PgColor.Status.Caution.base)).frame(width: 8, height: 8)
            Text(label).pgText(PgType.body).foregroundStyle(Color(PgColor.Text.primary)).frame(maxWidth: .infinity, alignment: .leading)
            Text(value)
                .font(.system(size: PgType.caption.size, design: .monospaced))
                .foregroundStyle(Color(PgColor.Text.secondary))
                .multilineTextAlignment(.trailing)
        }
        .padding(.vertical, 11)
    }
}

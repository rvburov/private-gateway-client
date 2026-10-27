import PrivateGatewayCore
import PrivateGatewayTheme
import SwiftUI
import UIKit

/// Страницы «Настроек»: список разделов и вложенные страницы — по одной на экран, как у Android (доска 25).
enum SettingsPage: Hashable {
    case main
    case protection
    case licenses
}

/// «Настройки» — как `SettingsScreen` Android: раздел «Защита и утечки», «О программе» с версиями и лицензиями открытых
/// компонентов, язык — системный или выбранный для приложения в Настройках iOS. Изменения под защитой применяются сразу —
/// перезапуском ядра или туннеля (`SettingsActions`).
struct SettingsView: View {
    let texts: PgTexts
    let settings: AppSettings
    let about: AboutInfo
    let onTunnel: (TunnelSettings) -> Void
    let onKillSwitch: (Bool) -> Void

    @State private var page: SettingsPage
    @Environment(\.openURL) private var openURL

    init(
        texts: PgTexts, settings: AppSettings, about: AboutInfo, page: SettingsPage = .main, onTunnel: @escaping (TunnelSettings) -> Void = { _ in },
        onKillSwitch: @escaping (Bool) -> Void = { _ in }
    ) {
        self.texts = texts
        self.settings = settings
        self.about = about
        self.onTunnel = onTunnel
        self.onKillSwitch = onKillSwitch
        _page = State(initialValue: page)
    }

    var body: some View {
        switch page {
        case .protection:
            ProtectionView(
                texts: texts, tunnel: settings.tunnel, killSwitch: settings.killSwitch, onTunnel: onTunnel, onKillSwitch: onKillSwitch,
                onBack: { page = .main }
            )
        case .licenses:
            LicensesView(texts: texts, about: about) { page = .main }
        case .main:
            main
        }
    }

    private var main: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: PgSpace.l) {
                Text(texts.navSettings).pgText(PgType.h1).foregroundStyle(Color(PgColor.Text.primary)).padding(.top, PgSpace.l)
                VStack(spacing: 0) {
                    SectionRow(
                        icon: PgIcons.advProtection, title: texts.settingsAdvProtectionTitle, text: texts.settingsAdvProtectionText,
                        identifier: "settings.protection"
                    ) { page = .protection }
                }
                .pgCard()

                VStack(alignment: .leading, spacing: PgSpace.xs) {
                    Overline(text: texts.aboutName)
                    Text(texts.aboutVersion(about.app, about.system, about.arch))
                        .pgText(PgType.body)
                        .foregroundStyle(Color(PgColor.Text.primary))
                        .accessibilityIdentifier("settings.version")
                    Text(texts.cardCore + ": Xray-core " + (about.core ?? "—"))
                        .pgText(PgType.small)
                        .foregroundStyle(Color(PgColor.Text.secondary))
                        .accessibilityIdentifier("settings.core")
                    SectionRow(icon: PgIcons.file, title: texts.aboutLicenses, text: texts.aboutLicensesText, identifier: "settings.licenses") {
                        page = .licenses
                    }
                    .padding(.top, PgSpace.s)
                }
                .pgCard()

                // Язык приложения iOS выбирает сама (Настройки → Private Gateway → Язык): нажатие открывает эту страницу.
                Button {
                    if let url = URL(string: UIApplication.openSettingsURLString) {
                        openURL(url)
                    }
                } label: {
                    Text(texts.settingsLanguageTitle + ": " + texts.iosSettingsLanguage)
                        .pgText(PgType.caption)
                        .foregroundStyle(Color(PgColor.Text.muted))
                        .multilineTextAlignment(.center)
                        .frame(maxWidth: .infinity, minHeight: PgLayout.touchTarget)
                }
                .buttonStyle(.plain)
                .accessibilityIdentifier("settings.language")
            }
            .padding(.horizontal, PgSpace.windowSideMobile)
            .padding(.bottom, PgLayout.scrollBottomPadding)
        }
        .accessibilityIdentifier("settings")
    }
}

/// Раздел настроек: иконка, название, что внутри и стрелка — как список разделов десктопа (доска 4).
struct SectionRow: View {
    let icon: String
    let title: String
    let text: String
    var identifier = ""
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            HStack(spacing: PgSpace.m) {
                PgIcon(data: icon, color: Color(PgColor.Accent.base), size: 22)
                VStack(alignment: .leading, spacing: 2) {
                    Text(title).pgText(PgType.bodyStrong).foregroundStyle(Color(PgColor.Text.primary))
                    Text(text).pgText(PgType.caption).foregroundStyle(Color(PgColor.Text.secondary))
                }

                Spacer(minLength: 0)
                PgIcon(data: PgIcons.chevronRight, color: Color(PgColor.Text.muted), size: 20)
            }
            .frame(minHeight: PgLayout.touchTarget)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityIdentifier(identifier)
    }
}

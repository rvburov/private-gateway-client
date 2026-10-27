import PrivateGatewayCore
import PrivateGatewayTheme
import SwiftUI
import UIKit

/// Страницы «Настроек»: список разделов и вложенные страницы — по одной на экран, как у Android (доска 25).
enum SettingsPage: Hashable {
    case main
    case protection
    case subscriptions
    case licenses
}

/// «Настройки» — как `SettingsScreen` Android: разделы «Защита и утечки» и «Профили и подписки» (`subscriptions` — страница
/// по нажатию «Назад»), уведомления и переподключение, «О программе» с версиями и лицензиями открытых компонентов, язык — системный или выбранный для
/// приложения в Настройках iOS. Изменения под защитой применяются сразу — перезапуском ядра или туннеля (`SettingsActions`).
/// Открытая страница — у окна: «Добавить» поверх и возврат не сбрасывают ее.
struct SettingsView<Subscriptions: View>: View {
    let texts: PgTexts
    let settings: AppSettings
    let about: AboutInfo
    @Binding var page: SettingsPage
    var onTunnel: (TunnelSettings) -> Void = { _ in }
    var onKillSwitch: (Bool) -> Void = { _ in }
    var onNotifications: (NotificationLevel) -> Void = { _ in }
    var onReconnect: (ReconnectPolicy) -> Void = { _ in }
    let subscriptions: (_ onBack: @escaping () -> Void) -> Subscriptions

    @Environment(\.openURL) private var openURL

    var body: some View {
        switch page {
        case .protection:
            ProtectionView(
                texts: texts, tunnel: settings.tunnel, killSwitch: settings.killSwitch, onTunnel: onTunnel, onKillSwitch: onKillSwitch,
                onBack: { page = .main }
            )
        case .subscriptions:
            subscriptions { page = .main }
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
                    SectionRow(
                        icon: PgIcons.advProfiles, title: texts.settingsAdvProfilesTitle, text: texts.settingsAdvProfilesText,
                        identifier: "settings.profiles"
                    ) { page = .subscriptions }
                }
                .pgCard()

                VStack(alignment: .leading, spacing: PgSpace.xs) {
                    Text(texts.settingsNotificationsTitle).pgText(PgType.title).foregroundStyle(Color(PgColor.Text.primary))
                    ForEach([(NotificationLevel.all, texts.settingsNotificationsAll), (.importantOnly, texts.settingsNotificationsImportant),
                             (.off, texts.settingsNotificationsOff)], id: \.0) { item in
                        PgChoiceRow(title: item.1, selected: settings.notifications == item.0, identifier: "notify.\(item.0.rawValue)") { onNotifications(item.0) }
                    }
                }
                .pgCard()

                VStack(alignment: .leading, spacing: PgSpace.xs) {
                    Text(texts.coreReconnectTitle).pgText(PgType.title).foregroundStyle(Color(PgColor.Text.primary))
                    Text(texts.coreReconnectText).pgText(PgType.small).foregroundStyle(Color(PgColor.Text.secondary))
                    ForEach([(ReconnectPolicy.backoff60Seconds, texts.coreReconnectBackoff), (.upTo5Minutes, texts.coreReconnect5min),
                             (.never, texts.coreReconnectNever)], id: \.0) { item in
                        PgChoiceRow(title: item.1, selected: settings.reconnect == item.0, identifier: "reconnect.\(item.0.rawValue)") { onReconnect(item.0) }
                    }
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

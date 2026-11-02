import PrivateGatewayCore
import PrivateGatewayTheme
import SwiftUI
import UIKit

/// Страницы «Настроек»: список разделов и вложенные страницы — по одной на экран, как у Android (доска 25).
enum SettingsPage: Hashable {
    case main
    case protection
    case core
    case routing
    case subscriptions
    case about
    case licenses
}

/// «Настройки» (доска 04, SettingsPage десктопа): заголовок, «Основные» одной карточкой — блок при обрыве (выключение — через
/// вопрос), подключение при запуске, уведомления сегментами с подписью по выбору, язык — системный выбор языка приложения в
/// Настройках iOS; «Для опытных» — строки-ссылки на страницы, которые есть на телефоне (переподключение — на странице
/// «Подключение и ядро»), и вкладку «Проверка»; внизу — версия приложения и ядра. Режима «Только прокси» на телефоне нет
/// (решение 527).
/// Изменения под защитой применяются сразу — перезапуском ядра или туннеля (`SettingsActions`). Открытая страница — у окна:
/// «Добавить» поверх и возврат не сбрасывают ее.
struct SettingsView<Subscriptions: View>: View {
    let texts: PgTexts
    let settings: AppSettings
    let about: AboutInfo
    @Binding var page: SettingsPage
    var onTunnel: (TunnelSettings) -> Void = { _ in }
    var onKillSwitch: (Bool) -> Void = { _ in }
    var onAlwaysOn: (Bool) -> Void = { _ in }
    var onConnectOnLaunch: (Bool) -> Void = { _ in }
    var onNotifications: (NotificationLevel) -> Void = { _ in }
    var onReconnect: (ReconnectPolicy) -> Void = { _ in }
    /// «Подключение и ядро»: журнал ядра, MTU, определение домена, отпечаток TLS, Mux, фрагментация, таймаут.
    var onConnection: (ConnectionSettings) -> Void = { _ in }
    /// «Маршрутизация»: режим и правила для сайтов.
    var onRouting: (RoutingSettings) -> Void = { _ in }
    /// «Проверка и логи» — вкладка «Проверка» островка.
    var onCheck: () -> Void = {}
    /// «Вход в Wi-Fi отеля или кафе» в «Защите и утечках» — вопрос о паузе (решение 557).
    var onCaptive: () -> Void = {}
    let subscriptions: (_ onBack: @escaping () -> Void) -> Subscriptions

    @Environment(\.openURL) private var openURL

    var body: some View {
        switch page {
        case .protection:
            ProtectionView(
                texts: texts, tunnel: settings.tunnel, killSwitch: settings.killSwitch, alwaysOn: settings.alwaysOn, onTunnel: onTunnel,
                onKillSwitch: onKillSwitch, onAlwaysOn: onAlwaysOn, onBack: { page = .main }, onCaptive: onCaptive
            )
        case .core:
            CoreView(
                texts: texts, connection: settings.connection, reconnect: settings.reconnect, coreVersion: about.core, onConnection: onConnection,
                onReconnect: onReconnect, onBack: { page = .main }
            )
        case .routing:
            RoutingView(
                texts: texts, routing: settings.routing, allowLan: settings.tunnel.allowLan, onRouting: onRouting, onLan: { page = .protection },
                onBack: { page = .main }
            )
        case .subscriptions:
            subscriptions { page = .main }
        case .about:
            AboutView(texts: texts, about: about, onLicenses: { page = .licenses }, onReport: onCheck) { page = .main }
        case .licenses:
            // Лицензии открываются из «О программе» — туда и «Назад».
            LicensesView(texts: texts, about: about) { page = .about }
        case .main:
            main
        }
    }

    private var main: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 0) {
                Text(texts.navSettings)
                    .pgText(PgType.h1)
                    .foregroundStyle(Color(PgColor.Text.primary))
                    .accessibilityAddTraits(.isHeader)
                    .padding(.top, 18)
                    .padding(.bottom, PgSpace.l)
                SettingsGroup(title: texts.settingsSectionBasic, inset: PgSpace.xxs) {
                    basic
                }

                SettingsGroup(title: texts.settingsSectionAdvanced, inset: PgSpace.xxs) {
                    advanced
                }
                .padding(.top, 18)

                Text(SettingsLook.footer(about, texts))
                    .pgText(PgType.caption)
                    .foregroundStyle(Color(PgColor.Text.muted))
                    .multilineTextAlignment(.center)
                    .frame(maxWidth: .infinity)
                    .padding(.top, 14)
                    .accessibilityIdentifier("settings.footer")
            }
            .padding(.horizontal, PgSpace.windowSideMobile)
            .padding(.bottom, PgLayout.scrollBottomPadding)
        }
        .accessibilityIdentifier("settings")
    }

    /// «Основные» (доска 04): строки .setting через полосы.
    @ViewBuilder
    private var basic: some View {
        PgToggleRow(
            title: texts.settingsKsTitle, text: texts.settingsKsText, isOn: settings.killSwitch, padded: true, confirmOff: SettingsDialogs.ksOff(texts),
            identifier: "settings.killSwitch", onChange: onKillSwitch
        )
        PgDivider().padding(.horizontal, PgSpace.card)
        PgToggleRow(
            title: texts.settingsConnectOnLaunchTitle, text: texts.settingsConnectOnLaunchText, isOn: settings.connectOnLaunch, padded: true,
            identifier: "settings.connectOnLaunch", onChange: onConnectOnLaunch
        )
        PgDivider().padding(.horizontal, PgSpace.card)
        notifications
        PgDivider().padding(.horizontal, PgSpace.card)
        language
    }

    /// «Уведомления» (.setting.stacked): заголовок, подпись по выбранному уровню и три сегмента под ними.
    private var notifications: some View {
        VStack(alignment: .leading, spacing: PgSpace.m) {
            VStack(alignment: .leading, spacing: 3) {
                Text(texts.settingsNotificationsTitle).pgText(PgType.bodyStrong).foregroundStyle(Color(PgColor.Text.primary))
                if let note = SettingsLook.notificationsNote(settings.notifications, texts) {
                    Text(note)
                        .pgText(PgType.caption)
                        .foregroundStyle(Color(PgColor.Text.muted))
                        .fixedSize(horizontal: false, vertical: true)
                        .accessibilityIdentifier("settings.notifications.note")
                }
            }

            PgSegments(options: SettingsLook.notificationOptions(texts), selection: settings.notifications, identifier: "notify", onSelect: onNotifications)
        }
        .padding(.vertical, 13)
        .padding(.horizontal, PgSpace.card)
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    /// «Язык»: язык приложения iOS выбирает сама (Настройки → Private Gateway → Язык) — справа текущий, нажатие открывает эту
    /// страницу Настроек.
    private var language: some View {
        Button {
            if let url = URL(string: UIApplication.openSettingsURLString) {
                openURL(url)
            }
        } label: {
            PgSettingRow(title: texts.settingsLanguageTitle, caption: SettingsLook.languageNote(texts)) {
                HStack(spacing: PgSpace.xxs) {
                    Text(SettingsLook.languageName(texts))
                        .pgText(PgType.small.with(weight: 500))
                        .foregroundStyle(Color(PgColor.Text.secondary))
                        .lineLimit(1)
                    PgIcon(data: PgIcons.chevronRight, color: Color(PgColor.Text.muted), size: 16)
                }
            }
            .frame(minHeight: PgLayout.touchTarget)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(.isButton)
        .accessibilityIdentifier("settings.language")
    }

    /// «Для опытных» (доска 04): строки-ссылки с плиткой значка через полосы по порядку доски — только на то, что на телефоне
    /// есть («Резервная копия» появится со своей страницей).
    @ViewBuilder
    private var advanced: some View {
        PgLinkRow(
            icon: PgIcons.advProtection, title: texts.settingsAdvProtectionTitle, caption: texts.settingsAdvProtectionText, large: true,
            identifier: "settings.protection"
        ) { page = .protection }
        PgDivider().padding(.horizontal, PgSpace.card)
        PgLinkRow(icon: PgIcons.advCore, title: texts.settingsAdvCoreTitle, caption: texts.settingsAdvCoreText, large: true, identifier: "settings.core") {
            page = .core
        }
        PgDivider().padding(.horizontal, PgSpace.card)
        PgLinkRow(
            icon: PgIcons.advRouting, title: texts.settingsAdvRoutingTitle, caption: texts.settingsAdvRoutingText, large: true, identifier: "settings.routing"
        ) { page = .routing }
        PgDivider().padding(.horizontal, PgSpace.card)
        PgLinkRow(
            icon: PgIcons.advProfiles, title: texts.settingsAdvProfilesTitle, caption: texts.settingsAdvProfilesText, large: true,
            identifier: "settings.profiles"
        ) { page = .subscriptions }
        PgDivider().padding(.horizontal, PgSpace.card)
        PgLinkRow(
            icon: PgIcons.advCheck, title: texts.settingsAdvCheckTitle, caption: texts.settingsAdvCheckText, large: true, identifier: "settings.check",
            action: onCheck
        )
        PgDivider().padding(.horizontal, PgSpace.card)
        PgLinkRow(
            icon: PgIcons.advAbout, title: texts.settingsAdvAboutTitle, caption: texts.settingsAdvAboutText, large: true, identifier: "settings.about"
        ) { page = .about }
    }
}

/// Группа настроек (доски 04 и 05): подпись .t-overline над карточкой, в карточке — строки через полосы; `inset` — поля карточки
/// сверху и снизу (4 у «Настроек», 2 у «Защиты и утечек»).
struct SettingsGroup<Content: View>: View {
    let title: String
    var inset: Double = 2
    @ViewBuilder let content: () -> Content

    var body: some View {
        VStack(alignment: .leading, spacing: PgSpace.s) {
            Overline(text: title)
                .padding(.horizontal, PgSpace.xs)
                .accessibilityAddTraits(.isHeader)
            VStack(spacing: 0) {
                content()
            }
            .padding(.vertical, inset)
            .pgCard(padding: 0)
        }
    }
}

/// Правила «Настроек» — как SettingsPageViewModel десктопа.
enum SettingsLook {
    /// Уровни уведомлений сегментами по порядку макета.
    static func notificationOptions(_ texts: PgTexts) -> [PgOption<NotificationLevel>] {
        [
            PgOption(value: NotificationLevel.all, title: texts.settingsNotificationsAll),
            PgOption(value: NotificationLevel.importantOnly, title: texts.settingsNotificationsImportant),
            PgOption(value: NotificationLevel.off, title: texts.settingsNotificationsOff),
        ]
    }

    /// Подпись под «Уведомлениями» по выбранному уровню (NotificationsNote десктопа). У «Выключены» подпись десктопа — про
    /// значок в трее, а трея у телефона нет: подписи нет, пока у телефона нет своего текста.
    static func notificationsNote(_ level: NotificationLevel, _ texts: PgTexts) -> String? {
        switch level {
        case .all: return texts.settingsNotificationsAllNote
        case .importantOnly: return texts.settingsNotificationsImportantNote
        case .off: return nil
        }
    }

    /// Язык приложения сейчас — «Русский» или «English», как сегменты макета.
    static func languageName(_ texts: PgTexts) -> String {
        texts.language == .russian ? texts.settingsLanguageRu : texts.settingsLanguageEn
    }

    /// Где меняется язык — с заглавной буквы: «Как в системе — меняется в Настройках iOS: Private Gateway → Язык».
    static func languageNote(_ texts: PgTexts) -> String {
        let text = texts.iosSettingsLanguage
        return text.prefix(1).uppercased() + text.dropFirst()
    }

    /// Подпись внизу (settings.footer десктопа) без службы — ее у телефона нет: «Private Gateway Client 0.1.0 · Ядро: Xray-core
    /// 26.3.27»; версию ядра сообщает расширение туннеля после первого подключения. Все части — из словаря.
    static func footer(_ about: AboutInfo, _ texts: PgTexts) -> String {
        var text = texts.aboutName
        if !about.app.isEmpty {
            text += " " + about.app
        }

        text += " · " + texts.cardCore + ": " + texts.coreXrayTitle
        if let core = about.core, !core.isEmpty {
            text += " " + core
        }

        return text
    }
}

/// Вопросы «Настроек» и «Защиты и утечек», как SafetyDialogs десктопа (доска 18).
enum SettingsDialogs {
    /// Выключить блок при обрыве: основная кнопка — «Оставить включенным» (тумблер остается), «да» — опасная вторая «Выключить».
    static func ksOff(_ texts: PgTexts) -> PgDialog {
        PgDialog(
            kind: .warning, icon: PgIcons.shield, detail: PgIcons.markAlert, title: texts.dialogKsOffTitle, text: texts.dialogKsOffText,
            primary: texts.dialogKsOffKeep, secondary: texts.dialogKsOffDrop, secondaryDanger: true
        )
    }
}

import PrivateGatewayCore
import PrivateGatewayTheme
import SwiftUI

/// «О программе» (доска 15, решение 555) — как `AboutScreen` Android: карточка программы (знак, название, «Версия 0.1.0 · iOS 18 ·
/// arm64»), «Компоненты» (клиент, Xray-core, TUN-драйвер iOS), «Приватность» тремя строками и ссылки — лицензии, политика
/// конфиденциальности листом, «Сообщить о проблеме» (вкладка «Проверка» с отчетом). Карточки «Обновления» нет: приложение iOS
/// обновляет App Store (решение 555); строк «Служба» и «Hysteria2, TUIC» нет — службы и второго ядра на телефоне нет.
struct AboutView: View {
    let texts: PgTexts
    let about: AboutInfo
    let onLicenses: () -> Void
    let onReport: () -> Void
    let onBack: () -> Void

    @State private var privacy = false

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 0) {
                PgPageHeader(title: texts.settingsAdvAboutTitle, texts: texts, onBack: onBack)
                appCard.padding(.top, PgSpace.l)

                SettingsGroup(title: texts.aboutComponents, inset: PgSpace.xxs) {
                    value(texts.aboutClient, about.app.isEmpty ? "—" : about.app, "about.client")
                    PgDivider().padding(.horizontal, PgSpace.card)
                    value(texts.coreXrayTitle, about.core ?? "—", "about.core")
                    PgDivider().padding(.horizontal, PgSpace.card)
                    value(texts.aboutTun, texts.aboutTunSystem, "about.tun")
                }
                .padding(.top, 18)

                SettingsGroup(title: texts.aboutPrivacyGroup, inset: PgSpace.xxs) {
                    privacyRow(texts.aboutPrivacyTelemetry)
                    PgDivider().padding(.horizontal, PgSpace.card)
                    privacyRow(texts.aboutPrivacyLogsMobile)
                    PgDivider().padding(.horizontal, PgSpace.card)
                    privacyRow(texts.aboutPrivacyReport)
                }
                .padding(.top, 18)

                VStack(spacing: 0) {
                    link(texts.aboutLicenses, "about.licenses", action: onLicenses)
                    PgDivider().padding(.horizontal, PgSpace.card)
                    link(texts.aboutPrivacy, "about.privacy") { privacy = true }
                    PgDivider().padding(.horizontal, PgSpace.card)
                    link(texts.aboutReport, "about.report", action: onReport)
                }
                .padding(.vertical, PgSpace.xxs)
                .pgCard(padding: 0)
                .padding(.top, 18)
            }
            .padding(.horizontal, PgSpace.windowSideMobile)
            .padding(.bottom, PgLayout.scrollBottomPadding)
        }
        .accessibilityIdentifier("about")
        .pgSheet(isPresented: $privacy) {
            PgSheetHeader(title: texts.aboutPrivacy, closeLabel: texts.cardClose, identifier: "about.privacy.close") { privacy = false }
            ScrollView {
                Text(texts.aboutPrivacyTextMobile)
                    .pgText(PgType.body)
                    .foregroundStyle(Color(PgColor.Text.secondary))
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(.top, PgSpace.m)
            }
            .accessibilityIdentifier("about.privacy.sheet")
        }
    }

    /// Карточка программы: плитка 56 со знаком акцентным цветом, название полужирным и строка версии.
    private var appCard: some View {
        HStack(spacing: 14) {
            ZStack {
                RoundedRectangle(cornerRadius: 16, style: .continuous).fill(Color(PgColor.Accent.tint))
                PgIcon(data: PgIcons.shield, color: Color(PgColor.Accent.base), size: 32)
                PgIcon(data: PgIcons.markOk, color: Color(PgColor.Accent.base), size: 32)
            }
            .frame(width: 56, height: 56)
            VStack(alignment: .leading, spacing: 3) {
                Text(texts.aboutName).pgText(PgType.status).foregroundStyle(Color(PgColor.Text.primary))
                Text(AboutLook.version(about, texts))
                    .pgText(PgType.small)
                    .foregroundStyle(Color(PgColor.Text.secondary))
                    .accessibilityIdentifier("about.version")
            }
            Spacer(minLength: 0)
        }
        .pgCard()
    }

    private func value(_ title: String, _ value: String, _ identifier: String) -> some View {
        HStack(spacing: PgSpace.m) {
            Text(title).pgText(PgType.bodyStrong).foregroundStyle(Color(PgColor.Text.primary))
            Spacer(minLength: PgSpace.s)
            Text(value).pgText(PgType.mono).foregroundStyle(Color(PgColor.Text.secondary))
        }
        .padding(.vertical, 13)
        .padding(.horizontal, PgSpace.card)
        .accessibilityElement(children: .combine)
        .accessibilityIdentifier(identifier)
    }

    private func privacyRow(_ text: String) -> some View {
        HStack(spacing: PgSpace.m) {
            ZStack {
                Circle().fill(Color(PgColor.Status.Success.tint))
                PgIcon(data: PgIcons.chipOk, color: Color(PgColor.Status.Success.base), size: 14, lineWidth: 2.6)
            }
            .frame(width: 22, height: 22)
            Text(text).pgText(PgType.body).foregroundStyle(Color(PgColor.Text.primary))
            Spacer(minLength: 0)
        }
        .padding(.vertical, 12)
        .padding(.horizontal, PgSpace.card)
    }

    /// Строка-ссылка (.link-row): текст и стрелка справа, без плитки значка.
    private func link(_ title: String, _ identifier: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            HStack(spacing: PgSpace.m) {
                Text(title).pgText(PgType.bodyStrong).foregroundStyle(Color(PgColor.Text.primary))
                Spacer(minLength: PgSpace.s)
                PgIcon(data: PgIcons.chevronRight, color: Color(PgColor.Text.muted), size: 20)
            }
            .frame(minHeight: PgLayout.touchTarget)
            .padding(.horizontal, PgSpace.card)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityIdentifier(identifier)
    }
}

/// Правила «О программе».
enum AboutLook {
    /// «Версия 0.1.0 · iOS 18.1 · arm64» (about.version); чего не известно — «—».
    static func version(_ about: AboutInfo, _ texts: PgTexts) -> String {
        texts.aboutVersion(about.app.isEmpty ? "—" : about.app, about.system.isEmpty ? "—" : about.system, about.arch.isEmpty ? "—" : about.arch)
    }
}

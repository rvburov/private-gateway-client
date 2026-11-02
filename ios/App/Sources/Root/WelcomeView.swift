import PrivateGatewayCore
import PrivateGatewayTheme
import SwiftUI

/// Первый запуск (доска 10, решение 555) — как `WelcomeScreen` Android, без островка: приветствие — знак, слоган, три обещания и
/// «Начать»; затем «Добавьте первый сервер» — «Добавить сервер» открывает «Добавить профиль», «Позже» ведет на главную. Шага
/// «Установка службы» нет: службы у телефона нет, профиль VPN iOS спросит при первом подключении. Сегментов языка нет: язык
/// приложения iOS выбирает в своих Настройках, и приложение открывается уже на нем.
struct WelcomeView: View {
    let texts: PgTexts
    let onAdd: () -> Void
    let onDone: () -> Void

    @State private var step = Step.greet

    enum Step {
        case greet
        case add
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 0) {
                switch step {
                case .greet: greet
                case .add: addFirst
                }
            }
            .padding(.horizontal, PgSpace.windowSideMobile)
            .padding(.top, PgSpace.l)
            .padding(.bottom, 24)
        }
        .accessibilityIdentifier("welcome")
    }

    private var greet: some View {
        VStack(spacing: 0) {
            logo(size: 40).padding(.top, 40).accessibilityIdentifier("welcome.logo")
            Text(texts.appTitle)
                .pgText(PgType.h1)
                .foregroundStyle(Color(PgColor.Text.primary))
                .multilineTextAlignment(.center)
                .padding(.top, PgSpace.l)
                .accessibilityAddTraits(.isHeader)
            Text(texts.welcomeTagline)
                .pgText(PgType.body)
                .foregroundStyle(Color(PgColor.Text.secondary))
                .multilineTextAlignment(.center)
                .padding(.top, PgSpace.s)
            VStack(alignment: .leading, spacing: PgSpace.l) {
                promise(PgIcons.navHome, texts.welcomeAllTitle("iOS"), texts.welcomeAllTextMobile)
                promise(PgIcons.shield, texts.welcomeKsTitle, texts.welcomeKsText)
                promise(PgIcons.navServers, texts.welcomeChoiceTitle, texts.welcomeChoiceText)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(.top, 28)
            PgActionButton(label: texts.welcomeStart, identifier: "welcome.start", height: 50) { step = .add }
                .padding(.top, 32)
        }
    }

    private var addFirst: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: PgSpace.m) {
                logo(size: 28)
                Text(texts.appTitle).pgText(PgType.status).foregroundStyle(Color(PgColor.Text.primary))
            }

            VStack(alignment: .leading, spacing: 0) {
                ZStack {
                    RoundedRectangle(cornerRadius: 16, style: .continuous).fill(Color(PgColor.Accent.tint))
                    PgIcon(data: PgIcons.serverAdd, color: Color(PgColor.Accent.base), size: 32)
                }
                .frame(width: 56, height: 56)
                Text(texts.welcomeAddTitle)
                    .pgText(PgType.status)
                    .foregroundStyle(Color(PgColor.Text.primary))
                    .padding(.top, PgSpace.l)
                Text(texts.welcomeAddText)
                    .pgText(PgType.small)
                    .foregroundStyle(Color(PgColor.Text.secondary))
                    .fixedSize(horizontal: false, vertical: true)
                    .padding(.top, PgSpace.s)
                PgActionButton(label: texts.welcomeAddButton, identifier: "welcome.add.button", height: 50, action: onAdd)
                    .padding(.top, 20)
                PgActionButton(label: texts.welcomeAddLater, kind: .secondary, identifier: "welcome.add.later", height: 46, action: onDone)
                    .padding(.top, PgSpace.s)
            }
            .pgCard()
            .padding(.top, 28)
            .accessibilityIdentifier("welcome.add")
        }
    }

    private func logo(size: CGFloat) -> some View {
        ZStack {
            PgIcon(data: PgIcons.shield, color: Color(PgColor.Accent.base), size: size)
            PgIcon(data: PgIcons.markOk, color: Color(PgColor.Accent.base), size: size)
        }
    }

    private func promise(_ icon: String, _ title: String, _ text: String) -> some View {
        HStack(spacing: PgSpace.m) {
            ZStack {
                RoundedRectangle(cornerRadius: 10, style: .continuous).fill(Color(PgColor.Accent.tint))
                PgIcon(data: icon, color: Color(PgColor.Accent.base), size: 20)
            }
            .frame(width: 36, height: 36)
            VStack(alignment: .leading, spacing: 2) {
                Text(title).pgText(PgType.bodyStrong).foregroundStyle(Color(PgColor.Text.primary))
                Text(text).pgText(PgType.caption).foregroundStyle(Color(PgColor.Text.muted))
            }
        }
    }
}

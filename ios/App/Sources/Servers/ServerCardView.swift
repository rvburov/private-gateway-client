import PrivateGatewayCore
import PrivateGatewayTheme
import SwiftUI
import UIKit
import UniformTypeIdentifiers

/// Что умеет карточка сервера. `address` и `share` открывают ссылку из Связки ключей — только когда карточка открыта.
struct CardActions {
    var back: () -> Void = {}
    var connect: (String) -> Void = { _ in }
    var favorite: (String, Bool) -> Void = { _, _ in }
    var rename: (String, String) -> Void = { _, _ in }
    var delete: (String) -> Void = { _ in }
    var address: (String) async -> String? = { _ in nil }
    var share: (String) async -> String? = { _ in nil }
}

/// Карточка сервера (доска 11; на телефоне — своя страница) — как `ServerCardScreen` Android: избранное, состояние, задержка,
/// сведения (адрес, протокол, транспорт, защита, ядро), «Подключиться» или «Используется сейчас», «Поделиться», свое название,
/// удаление с подтверждением.
struct ServerCardView: View {
    let texts: PgTexts
    let home: HomeState
    let id: String
    let actions: CardActions

    @State private var deleting = false

    var body: some View {
        if let server = home.servers.first(where: { $0.id == id }) {
            card(server)
        } else {
            // Сервер удален (здесь или обновлением подписки) — карточке нечего показывать.
            Color.clear.onAppear(perform: actions.back)
        }
    }

    private func card(_ server: ServerItem) -> some View {
        let active = home.active(server.id)
        let selected = server.id == home.selectedId
        return ScrollView {
            VStack(alignment: .leading, spacing: PgSpace.m) {
                PgPageHeader(title: server.displayName, texts: texts, onBack: actions.back)
                HStack(spacing: PgSpace.m) {
                    CountryBadge(code: server.countryCode)
                    VStack(alignment: .leading, spacing: 2) {
                        Text(from(server)).pgText(PgType.small).foregroundStyle(Color(PgColor.Text.secondary)).lineLimit(2)
                        if server.missingSince != nil {
                            Text(texts.cardMissing).pgText(PgType.caption).foregroundStyle(Color(PgColor.Status.Caution.text))
                        }
                    }

                    Spacer(minLength: 0)
                    LatencyText(texts: texts, latency: server.latency)
                }

                HStack {
                    favoriteToggle(server)
                    Spacer()
                    if let status = Self.status(active: active, selected: selected, texts) {
                        Text(status)
                            .pgText(PgType.caption)
                            .foregroundStyle(Color(active ? PgColor.Status.Success.text : PgColor.Accent.text))
                            .padding(.horizontal, PgSpace.s)
                            .padding(.vertical, 2)
                            .background(RoundedRectangle(cornerRadius: PgRadius.chip).fill(Color(active ? PgColor.Status.Success.tint : PgColor.Accent.tint)))
                            .accessibilityIdentifier("card.status")
                    }
                }

                CardDetails(texts: texts, server: server, address: actions.address)
                if active {
                    Text(texts.cardInUse)
                        .pgText(PgType.bodyStrong)
                        .foregroundStyle(Color(PgColor.Status.Success.text))
                        .padding(.vertical, PgSpace.m)
                        .accessibilityIdentifier("card.inUse")
                } else {
                    PgButton(label: texts.cardConnect, identifier: "card.connect") { actions.connect(server.id) }
                }

                ShareBlock(texts: texts, server: server, share: actions.share)
                RenameBlock(texts: texts, server: server, onSave: { actions.rename(server.id, $0) })
                PgTextAction(label: texts.cardDelete, color: PgColor.Status.Danger.text, identifier: "card.delete") { deleting = true }
            }
            .padding(.horizontal, PgSpace.windowSideMobile)
            .padding(.bottom, PgLayout.scrollBottomPadding)
        }
        .alert(texts.dialogDeleteServerTitle(server.displayName), isPresented: $deleting) {
            Button(texts.dialogDeleteServerDelete, role: .destructive) { actions.delete(server.id) }
            Button(texts.dialogCancel, role: .cancel) {}
        } message: {
            Text(active ? texts.dialogDeleteServerTextInUse : texts.dialogDeleteServerText)
        }
        .accessibilityIdentifier("card")
    }

    /// «Активен» — защита идет через сервер, «Выбран» — выбран, защита выключена.
    private static func status(active: Bool, selected: Bool, _ texts: PgTexts) -> String? {
        if active {
            return texts.cardStatusActive
        }

        return selected ? texts.cardStatusSelected : nil
    }

    private func from(_ server: ServerItem) -> String {
        guard let group = server.subscriptionId.flatMap({ id in home.subscriptions.first { $0.id == id } }) else {
            return texts.cardFromLink
        }

        return texts.cardFromSubscription(group.name)
    }

    /// «В избранное» / «Убрать из избранного»: звезда с подписью.
    private func favoriteToggle(_ server: ServerItem) -> some View {
        Button {
            actions.favorite(server.id, !server.favorite)
        } label: {
            HStack(spacing: PgSpace.s) {
                if server.favorite {
                    FavoriteMark(size: 20)
                } else {
                    PgIcon(data: PgIcons.star, color: Color(PgColor.Text.muted), size: 20)
                }

                Text(server.favorite ? texts.cardFavoriteRemove : texts.cardFavoriteAdd).pgText(PgType.body).foregroundStyle(Color(PgColor.Text.primary))
            }
            .frame(minHeight: PgLayout.touchTarget)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(server.favorite ? .isSelected : [])
        .accessibilityIdentifier("card.favorite")
    }
}

/// Сведения: адрес (из ссылки в Связке ключей — по открытии карточки), протокол, транспорт, защита, ядро.
struct CardDetails: View {
    let texts: PgTexts
    let server: ServerItem
    let address: (String) async -> String?

    @State private var host = "…"

    var body: some View {
        VStack(alignment: .leading, spacing: PgSpace.xs) {
            detail(texts.cardAddress, host, "card.address")
            detail(texts.cardProtocol, ServerFacts.protocolName(server.protocol), "card.protocol")
            detail(texts.cardTransport, ServerFacts.transportName(server.transport) ?? String(describing: server.transport).uppercased(), "card.transport")
            detail(texts.cardSecurity, ServerFacts.securityName(server.security) ?? (server.encrypted ? texts.cardSecurityBuiltin : texts.cardSecurityNone), "card.security")
            detail(texts.cardCore, ServerFacts.coreName(server.protocol) ?? texts.androidCardCoreNone, "card.core")
        }
        .pgCard()
        .task(id: server.id) {
            host = await address(server.id) ?? "—"
        }
        .accessibilityIdentifier("card.details")
    }

    private func detail(_ label: String, _ value: String, _ identifier: String) -> some View {
        HStack(alignment: .top, spacing: PgSpace.s) {
            Text(label).pgText(PgType.small).foregroundStyle(Color(PgColor.Text.secondary)).frame(width: 110, alignment: .leading)
            Text(value).pgText(PgType.mono).foregroundStyle(Color(PgColor.Text.primary)).accessibilityIdentifier(identifier)
            Spacer(minLength: 0)
        }
    }
}

/// «Поделиться» (решение 436): ссылка открывается из Связки ключей только по нажатию и живет, пока QR на экране; уход окна в
/// фон его прячет (снимок для переключателя программ ключа не покажет). Ссылка — с названием из ссылки, как у десктопа и
/// Android; копия в буфер — только на этом iPhone и на 2 минуты, «Отправить» — системный выбор программы.
struct ShareBlock: View {
    let texts: PgTexts
    let server: ServerItem
    let share: (String) async -> String?

    @State private var link: String?
    @State private var noKey = false
    @Environment(\.scenePhase) private var phase

    var body: some View {
        Group {
            if let link {
                shown(link)
            } else {
                VStack(alignment: .leading, spacing: 0) {
                    PgTextAction(label: texts.cardShare, identifier: "card.share") {
                        Task {
                            let opened = await share(server.id)
                            noKey = opened == nil
                            link = opened
                        }
                    }

                    if noKey {
                        Text(texts.cardShareNoKey).pgText(PgType.small).foregroundStyle(Color(PgColor.Status.Warning.text))
                            .accessibilityIdentifier("card.share.noKey")
                    }
                }
            }
        }
        .onChange(of: phase) { phase in
            if phase != .active {
                link = nil
            }
        }
        .onChange(of: server.id) { _ in
            link = nil
        }
    }

    private func shown(_ link: String) -> some View {
        VStack(alignment: .leading, spacing: PgSpace.s) {
            if let image = QrCode.image(link, scale: 6) {
                Image(uiImage: image)
                    .interpolation(.none)
                    .resizable()
                    .aspectRatio(1, contentMode: .fit)
                    .accessibilityLabel(texts.cardShareQr)
                    .accessibilityIdentifier("card.share.qr")
            }

            Text(texts.cardShareWarning).pgText(PgType.small).foregroundStyle(Color(PgColor.Status.Caution.text))
            HStack(spacing: PgSpace.m) {
                PgTextAction(label: texts.cardShareCopy, identifier: "card.share.copy") {
                    // Ключ — только на этом iPhone (не в «Универсальный буфер обмена») и ненадолго.
                    UIPasteboard.general.setItems(
                        [[UTType.utf8PlainText.identifier: link]],
                        options: [.localOnly: true, .expirationDate: Date().addingTimeInterval(120)]
                    )
                }
                ShareLink(item: link) {
                    Text(texts.androidShareSend).pgText(PgType.bodyStrong).foregroundStyle(Color(PgColor.Accent.text)).frame(minHeight: PgLayout.touchTarget)
                }
                .accessibilityIdentifier("card.share.send")
            }

            PgTextAction(label: texts.cardShareHide, identifier: "card.share.hide") { self.link = nil }
        }
        .pgCard()
        .accessibilityIdentifier("card.share.block")
    }
}

/// Свое название: 1–64 знака без пробелов по краям; «Сохранить» гаснет, пока сохранять нечего.
struct RenameBlock: View {
    let texts: PgTexts
    let server: ServerItem
    let onSave: (String) -> Void

    @State private var name = ""

    var body: some View {
        let valid = ServerNames.validate(name)
        VStack(alignment: .leading, spacing: PgSpace.xs) {
            Overline(text: texts.cardRename)
            PgField(placeholder: texts.cardRenameLabel, text: $name, identifier: "card.rename.text")
            PgButton(label: texts.cardRenameSave, enabled: valid != nil && valid != server.displayName, identifier: "card.rename.save") {
                if let valid {
                    onSave(valid)
                }
            }
        }
        .onAppear { name = server.displayName }
        .onChange(of: server.id) { _ in name = server.displayName }
    }
}

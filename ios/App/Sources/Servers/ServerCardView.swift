import PrivateGatewayCore
import PrivateGatewayTheme
import SwiftUI
import UIKit
import UniformTypeIdentifiers

/// Что умеет карточка сервера. `address` и `share` открывают ссылку из Связки ключей — только когда карточка открыта; `exit` —
/// последний IP, который сайты видели через сервер (решение 583), nil — не мерили.
struct CardActions {
    /// Закрыть лист: крестик или сервер пропал из списка.
    var back: () -> Void = {}
    /// «Подключиться»: окно закрывает лист и включает защиту через сервер.
    var connect: (String) -> Void = { _ in }
    var favorite: (String, Bool) -> Void = { _, _ in }
    var rename: (String, String) -> Void = { _, _ in }
    /// Удалить — после «да» в диалоге: окно закрывает лист, удаляет сервер и говорит «Сервер удален».
    var delete: (String) -> Void = { _ in }
    var address: (String) async -> String? = { _ in nil }
    var share: (String) async -> String? = { _ in nil }
    var exit: (String) async -> String? = { _ in nil }
}

/// Что стоит в середине карточки: сведения, поле нового названия или QR-код со ссылкой.
enum CardPage: Equatable {
    case details
    case rename
    case share
}

/// Карточка сервера (доска 11, ServerCardView десктопа) — нижний лист поверх «Серверов» (`pgSheet`): значок страны, название и
/// «из подписки «…»», звезда избранного и крестик; чипы состояния и задержки; сведения (адрес, протокол, транспорт, защита,
/// ядро), на их месте — поле названия или QR-код; внизу «Подключиться» или «Используется сейчас» и плитки «Переименовать»,
/// «Поделиться», «Удалить». Диалог удаления и подсказки встают в слоях листа.
struct ServerCardView: View {
    let texts: PgTexts
    let home: HomeState
    let id: String
    let actions: CardActions

    @State private var page: CardPage
    /// Поле «Название».
    @State private var name = ""
    /// Ссылка с ключом — только пока QR-код на экране.
    @State private var link: String?
    /// Адрес из ссылки в Связке ключей — открывается, когда карточка открыта; nil — еще не открыт (`hostLoaded`) или не
    /// открылся.
    @State private var host: String?
    @State private var hostLoaded = false
    @State private var seen: String?
    /// Ссылки сервера в Связке ключей нет — делиться нечем: плитка «Поделиться» недоступна.
    @State private var noKey = false
    @Environment(\.pgLayers) private var layers
    @Environment(\.scenePhase) private var phase

    init(texts: PgTexts, home: HomeState, id: String, actions: CardActions, page: CardPage = .details, link: String? = nil) {
        self.texts = texts
        self.home = home
        self.id = id
        self.actions = actions
        _page = State(initialValue: page)
        _link = State(initialValue: link)
    }

    var body: some View {
        if let server = home.servers.first(where: { $0.id == id }) {
            card(server)
        } else {
            // Сервер удален (здесь или обновлением подписки) — карточке нечего показывать.
            Color.clear.onAppear(perform: actions.back)
        }
    }

    private func card(_ server: ServerItem) -> some View {
        let status = CardLook.status(server, home: home, texts)
        let inUse = status?.active == true
        return VStack(spacing: 0) {
            head(server)
            ScrollView {
                VStack(alignment: .leading, spacing: PgSpace.l) {
                    chips(server, status: status)
                    switch page {
                    case .details:
                        details(server)
                    case .rename:
                        renameBlock(server)
                    case .share:
                        if let link {
                            shareBlock(link)
                        } else {
                            details(server)
                        }
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            .padding(.vertical, PgSpace.l)

            bottom(server, inUse: inUse)
        }
        .task(id: server.id) {
            let opened = await actions.address(server.id)
            seen = await actions.exit(server.id)
            host = opened
            hostLoaded = true
            if opened == nil {
                noKey = true
            }
        }
        .onChange(of: phase) { phase in
            // Уход окна в фон прячет QR: снимок для переключателя программ ключа не покажет.
            if phase != .active {
                hideShare()
            }
        }
        .accessibilityIdentifier("card")
    }

    /// Заголовок листа (.sc-head): значок страны 52 × 38, название (type.status) и под ним откуда сервер, звезда избранного и
    /// крестик «Закрыть».
    private func head(_ server: ServerItem) -> some View {
        HStack(spacing: 0) {
            CountryBadge(code: server.countryCode, size: .card)
            VStack(alignment: .leading, spacing: 2) {
                Text(server.displayName)
                    .pgText(PgType.status)
                    .foregroundStyle(Color(PgColor.Text.primary))
                    .lineLimit(1)
                    .accessibilityAddTraits(.isHeader)
                    .accessibilityIdentifier("card.name")
                Text(CardLook.subtitle(server, subscriptions: home.subscriptions, texts))
                    .pgText(PgType.caption)
                    .foregroundStyle(Color(PgColor.Text.muted))
                    .fixedSize(horizontal: false, vertical: true)
                    .accessibilityIdentifier("card.from")
            }
            .padding(.leading, PgSpace.l)
            .padding(.trailing, PgSpace.s)
            .frame(maxWidth: .infinity, alignment: .leading)

            Button {
                actions.favorite(server.id, !server.favorite)
            } label: {
                Group {
                    if server.favorite {
                        FavoriteMark(size: 22)
                    } else {
                        PgIcon(data: PgIcons.star, color: Color(PgColor.Text.muted), size: 22)
                    }
                }
                .frame(width: PgLayout.touchTarget, height: PgLayout.touchTarget)
                .contentShape(Rectangle())
            }
            .buttonStyle(PgPressStyle())
            .accessibilityLabel(server.favorite ? texts.cardFavoriteRemove : texts.cardFavoriteAdd)
            .accessibilityAddTraits(server.favorite ? .isSelected : [])
            .accessibilityIdentifier("card.favorite")

            Button(action: actions.back) {
                PgIcon(data: PgIcons.close, color: Color(PgColor.Text.secondary), size: 20, lineWidth: 2.2)
                    .frame(width: PgLayout.touchTarget, height: PgLayout.touchTarget)
                    .contentShape(Rectangle())
            }
            .buttonStyle(PgPressStyle())
            .accessibilityLabel(texts.cardClose)
            .accessibilityIdentifier("card.close")
        }
        .padding(.top, 2)
    }

    /// Чипы (.sc-chip, высота 28): состояние с точкой — «Активен» зеленым или «Выбран» синим — и задержка моноширинным.
    private func chips(_ server: ServerItem, status: CardStatus?) -> some View {
        let latency = CardLook.latency(server.latency, texts)
        let shape = RoundedRectangle(cornerRadius: 8, style: .continuous)
        return HStack(spacing: PgSpace.s) {
            if let status {
                HStack(spacing: PgSpace.xs) {
                    Circle()
                        .fill(Color(status.active ? PgColor.Status.Success.base : PgColor.Accent.base))
                        .frame(width: 7, height: 7)
                    Text(status.text)
                        .pgText(PgType.caption.with(weight: 600))
                        .foregroundStyle(Color(status.active ? PgColor.Status.Success.badge : PgColor.Accent.text))
                        .lineLimit(1)
                }
                .padding(.horizontal, PgSpace.m)
                .frame(minHeight: 28)
                .background(shape.fill(Color(status.active ? PgColor.Status.Success.tint : PgColor.Accent.tint)))
                .accessibilityElement(children: .combine)
                .accessibilityIdentifier("card.status")
            }

            Text(latency.text)
                .pgText(PgTextStyle(size: PgType.caption.size, weight: 600, mono: true))
                .foregroundStyle(Color(latency.color))
                .lineLimit(1)
                .padding(.horizontal, PgSpace.m)
                .frame(minHeight: 28)
                .background(shape.fill(Color(PgColor.Control.segmentTrack)))
                .accessibilityLabel(latency.hint)
                .accessibilityIdentifier("card.latency")
        }
    }

    /// Сведения (вставка surface.inset): подпись слева серым, значение моноширинным по правому краю, строки через черту.
    private func details(_ server: ServerItem) -> some View {
        let rows = CardLook.details(server, host: hostLoaded ? host ?? "—" : "…", exit: hostLoaded ? seen : "…", texts)
        return VStack(spacing: 0) {
            ForEach(rows.indices, id: \.self) { index in
                if index > 0 {
                    PgDivider()
                }

                HStack(spacing: PgSpace.l) {
                    Text(rows[index].label).pgText(PgType.small).foregroundStyle(Color(PgColor.Text.muted)).lineLimit(1)
                    Text(rows[index].value)
                        .pgText(PgType.mono)
                        .foregroundStyle(Color(PgColor.Text.primary))
                        .lineLimit(1)
                        .frame(maxWidth: .infinity, alignment: .trailing)
                        .accessibilityIdentifier(rows[index].identifier)
                }
                .padding(.vertical, PgSpace.s)
            }
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 2)
        .modifier(CardInset())
        .accessibilityIdentifier("card.details")
    }

    /// Новое название (вместо сведений): «Название», поле до 64 знаков, «Отмена» и «Сохранить» — обе возвращают сведения.
    /// «Сохранить» недоступна, пока сохранять нечего.
    private func renameBlock(_ server: ServerItem) -> some View {
        let field = RoundedRectangle(cornerRadius: PgRadius.input, style: .continuous)
        return VStack(alignment: .leading, spacing: PgSpace.m) {
            Overline(text: texts.cardRenameLabel)
            TextField("", text: $name)
                .pgText(PgType.body)
                .foregroundStyle(Color(PgColor.Text.primary))
                .autocorrectionDisabled()
                .padding(.horizontal, PgSpace.l)
                .frame(minHeight: PgLayout.touchTarget)
                .background(field.fill(Color(PgColor.Surface.solid)))
                .overlay(field.strokeBorder(Color(PgColor.Line.strong), lineWidth: 1))
                .accessibilityLabel(texts.cardRenameLabel)
                .accessibilityIdentifier("card.rename.text")
            HStack(spacing: PgSpace.s) {
                PgActionButton(label: texts.dialogCancel, kind: .secondary, identifier: "card.rename.cancel") {
                    page = .details
                }
                .fixedSize(horizontal: true, vertical: false)
                Spacer(minLength: 0)
                PgActionButton(label: texts.cardRenameSave, enabled: CardLook.canSave(name, server: server), identifier: "card.rename.save") {
                    if let valid = ServerNames.validate(name) {
                        actions.rename(server.id, valid)
                    }

                    page = .details
                }
                .fixedSize(horizontal: true, vertical: false)
            }
        }
        .padding(14)
        .modifier(CardInset())
        .accessibilityIdentifier("card.rename.block")
    }

    /// QR-код со ссылкой (вместо сведений): код 168 в белой рамке, предупреждение о ключе в желтой плашке и «Копировать
    /// ссылку» — в буфер только этого iPhone (не в «Универсальный буфер обмена») и на 2 минуты.
    private func shareBlock(_ link: String) -> some View {
        let frame = RoundedRectangle(cornerRadius: 12, style: .continuous)
        return VStack(spacing: PgSpace.l) {
            if let image = QrCode.image(link, scale: 6) {
                Image(uiImage: image)
                    .interpolation(.none)
                    .resizable()
                    .frame(width: 168, height: 168)
                    .padding(PgSpace.m)
                    .background(frame.fill(Color(PgColor.Surface.solid)))
                    .overlay(frame.strokeBorder(Color(PgColor.Line.divider), lineWidth: 1))
                    .accessibilityLabel(texts.cardShareQr)
                    .accessibilityIdentifier("card.share.qr")
            }

            Text(texts.cardShareWarning)
                .pgText(PgType.small)
                .foregroundStyle(Color(PgColor.Status.Caution.text))
                .fixedSize(horizontal: false, vertical: true)
                .padding(.horizontal, PgSpace.l)
                .padding(.vertical, PgSpace.m)
                .frame(maxWidth: .infinity, alignment: .leading)
                .background(frame.fill(Color(PgColor.Status.Caution.tint)))
                .overlay(frame.strokeBorder(Color(PgColor.Status.Caution.line), lineWidth: 1))
            PgActionButton(label: texts.cardShareCopy, kind: .secondary, identifier: "card.share.copy") {
                CardShare.copy(link, layers: layers, texts: texts) { text in
                    UIPasteboard.general.setItems(
                        [[UTType.utf8PlainText.identifier: text]],
                        options: [.localOnly: true, .expirationDate: Date().addingTimeInterval(120)]
                    )
                }
            }
        }
        .padding(PgSpace.card)
        .modifier(CardInset())
        .accessibilityIdentifier("card.share.block")
    }

    /// Низ листа: «Используется сейчас» (зеленая полоса со значком) или «Подключиться» и под ними три плитки. Hysteria2 и TUIC
    /// телефон не подключает — «Подключиться» недоступна.
    private func bottom(_ server: ServerItem, inUse: Bool) -> some View {
        let supported = ServersLook.supported(server)
        let bar = RoundedRectangle(cornerRadius: 14, style: .continuous)
        return VStack(spacing: PgSpace.s) {
            if inUse {
                HStack(spacing: PgSpace.s) {
                    PgIcon(data: PgIcons.chipOk, color: Color(PgColor.Status.Success.badge), size: 18, lineWidth: 2.4)
                    Text(texts.cardInUse).pgText(PgType.title).foregroundStyle(Color(PgColor.Status.Success.badge)).lineLimit(1)
                }
                .frame(maxWidth: .infinity, minHeight: 50)
                .background(bar.fill(Color(PgColor.Status.Success.tint)))
                .overlay(bar.strokeBorder(Color(PgColor.Status.Success.line), lineWidth: 1.5))
                .accessibilityElement(children: .combine)
                .accessibilityIdentifier("card.inUse")
            } else {
                PgActionButton(label: texts.cardConnect, enabled: supported, identifier: "card.connect", height: 50, style: PgType.title) {
                    actions.connect(server.id)
                }
                .accessibilityHint(supported ? "" : texts.androidCardCoreNone)
            }

            HStack(spacing: PgSpace.s) {
                CardTile(icon: PgIcons.edit, label: texts.cardRename, identifier: "card.rename") {
                    startRename(server)
                }
                CardTile(
                    icon: PgIcons.qr, label: page == .share ? texts.cardShareHide : texts.cardShare, on: page == .share, enabled: !noKey,
                    hint: noKey ? texts.cardShareNoKey : nil, identifier: "card.share"
                ) {
                    toggleShare(server)
                }
                CardTile(icon: PgIcons.trash, label: texts.cardDelete, danger: true, identifier: "card.delete") {
                    layers?.confirm(CardLook.deleteDialog(server.displayName, inUse: inUse, texts)) {
                        actions.delete(server.id)
                    }
                }
            }
        }
    }

    /// «Переименовать»: поле с нынешним названием на месте сведений; QR-код, если был, закрывается.
    private func startRename(_ server: ServerItem) {
        name = server.displayName
        hideShare()
        page = .rename
    }

    /// «Поделиться» / «Скрыть QR»: ссылка открывается из Связки ключей только по нажатию; ключа нет — подсказка сверху листа,
    /// плитка становится недоступной.
    private func toggleShare(_ server: ServerItem) {
        if page == .share {
            hideShare()
            return
        }

        Task {
            guard let opened = await actions.share(server.id) else {
                noKey = true
                layers?.show(texts.cardShareNoKey, for: PgToastDuration.action)
                return
            }

            link = opened
            page = .share
        }
    }

    private func hideShare() {
        link = nil
        if page == .share {
            page = .details
        }
    }
}

/// Вставка карточки (.sc-inset): surface.inset, рамка line.divider, скругление 16.
private struct CardInset: ViewModifier {
    func body(content: Content) -> some View {
        let shape = RoundedRectangle(cornerRadius: 16, style: .continuous)
        return content
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(shape.fill(Color(PgColor.Surface.inset)))
            .overlay(shape.strokeBorder(Color(PgColor.Line.divider), lineWidth: 1))
    }
}

/// Плитка действия (.btn-tile, .sc-tile): значок 18 над подписью caption, высота 58, скругление 14, белая с рамкой
/// line.strong; нажатая (.on, «Скрыть QR») — accent.tint и рамка accent.base, опасная — красные подпись и рамка,
/// недоступная — прозрачность 0,45. Почему недоступна — подсказка VoiceOver.
struct CardTile: View {
    let icon: String
    let label: String
    var on = false
    var danger = false
    var enabled = true
    var hint: String?
    var identifier = ""
    let action: () -> Void

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: 14, style: .continuous)
        Button(action: action) {
            VStack(spacing: PgSpace.xxs) {
                PgIcon(data: icon, color: Color(ink), size: 18, lineWidth: 1.9)
                Text(label)
                    .pgText(PgType.caption)
                    .foregroundStyle(Color(ink))
                    .lineLimit(1)
                    .minimumScaleFactor(0.8)
            }
            .padding(.horizontal, PgSpace.xxs)
            .frame(maxWidth: .infinity, minHeight: 58)
            .background(shape.fill(Color(on ? PgColor.Accent.tint : PgColor.Surface.solid)))
            .overlay(shape.strokeBorder(Color(border), lineWidth: 1))
            .contentShape(Rectangle())
        }
        .buttonStyle(PgPressStyle())
        .disabled(!enabled)
        .opacity(enabled ? 1 : 0.45)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(label)
        .accessibilityHint(hint ?? "")
        .accessibilityAddTraits(on ? [.isButton, .isSelected] : .isButton)
        .accessibilityIdentifier(identifier)
    }

    private var ink: PgRGBA {
        if danger {
            return PgColor.Status.Danger.text
        }

        return on ? PgColor.Accent.text : PgColor.Text.primary
    }

    private var border: PgRGBA {
        if danger {
            return PgColor.Status.Danger.line
        }

        return on ? PgColor.Accent.base : PgColor.Line.strong
    }
}

/// Состояние сервера в карточке: «Активен» — защита идет или поднимается через него, «Выбран» — к нему подключит щит.
struct CardStatus: Equatable {
    let text: String
    let active: Bool
}

/// Строка сведений: подпись, значение и имя для тестов.
struct CardDetail: Equatable {
    let label: String
    let value: String
    let identifier: String
}

/// Что показывает карточка — правила `ServerCardViewModel` десктопа.
enum CardLook {
    /// «из подписки «Основная»» или «добавлен ссылкой»; пропал из подписки — «Пропал из подписки — остается, пока выбран». Города
    /// сервера телефон не знает (его нет ни в ссылке, ни в подписке), поэтому «город · » впереди не пишется — как у десктопа
    /// для сервера без города.
    static func subtitle(_ server: ServerItem, subscriptions: [SubscriptionRef], _ texts: PgTexts) -> String {
        if server.missingSince != nil {
            return texts.cardMissing
        }

        guard let group = server.subscriptionId.flatMap({ id in subscriptions.first { $0.id == id } }) else {
            return texts.cardFromLink
        }

        return texts.cardFromSubscription(group.name)
    }

    /// Чип у сервера в работе (защита идет через него, иначе — выбранного): «Активен», пока защита включена или включается,
    /// «Выбран» — выключена или не включилась; у других серверов чипа нет.
    static func status(_ server: ServerItem, home: HomeState, _ texts: PgTexts) -> CardStatus? {
        guard home.inUse?.id == server.id else {
            return nil
        }

        return home.off ? CardStatus(text: texts.cardStatusSelected, active: false) : CardStatus(text: texts.cardStatusActive, active: true)
    }

    /// Задержка в чипе — как в строке, только без ответа — «нет ответа» (card.latency.none): в чипе есть место для слов.
    static func latency(_ latency: Latency?, _ texts: PgTexts) -> (text: String, color: PgRGBA, hint: String) {
        let look = LatencyLook.of(latency, texts)
        guard latency?.failure != .unsupported, LatencyKind.of(latency) == .noAnswer else {
            return look
        }

        return (texts.cardLatencyNone, look.color, look.hint)
    }

    /// Адрес (из ссылки в Связке ключей), выходной IP — последний, который сайты видели через сервер (у сервера «VPS + прокси»
    /// он не совпадает с адресом; решения 583 и пользователя 2026-11-08, как у десктопа; nil — «Не измерен»), протокол,
    /// транспорт, защита и ядро; Hysteria2 и TUIC ядру телефона не по силам — «нет: …».
    static func details(_ server: ServerItem, host: String, exit: String? = nil, _ texts: PgTexts) -> [CardDetail] {
        [
            CardDetail(label: texts.cardAddress, value: host, identifier: "card.address"),
            CardDetail(label: texts.cardExitIp, value: exit ?? texts.cardExitIpUnknown, identifier: "card.exitIp"),
            CardDetail(label: texts.cardProtocol, value: ServerFacts.protocolName(server.protocol), identifier: "card.protocol"),
            CardDetail(
                label: texts.cardTransport, value: ServerFacts.transportName(server.transport) ?? String(describing: server.transport).uppercased(),
                identifier: "card.transport"
            ),
            CardDetail(
                label: texts.cardSecurity,
                value: ServerFacts.securityName(server.security) ?? (server.encrypted ? texts.cardSecurityBuiltin : texts.cardSecurityNone),
                identifier: "card.security"
            ),
            CardDetail(label: texts.cardCore, value: ServerFacts.coreName(server.protocol) ?? texts.androidCardCoreNone, identifier: "card.core"),
        ]
    }

    /// «Сохранить»: 1–64 знака без пробелов по краям и не то же название, что сейчас.
    static func canSave(_ name: String, server: ServerItem) -> Bool {
        guard let valid = ServerNames.validate(name) else {
            return false
        }

        return valid != server.displayName
    }

    /// «Удалить сервер «…»?» (доска 11, DialogView): красный значок корзины, текст по состоянию — защита идет через сервер
    /// или нет, главная красная «Удалить» и вторая «Отмена».
    static func deleteDialog(_ name: String, inUse: Bool, _ texts: PgTexts) -> PgDialog {
        PgDialog(
            kind: .danger, icon: PgIcons.trash, title: texts.dialogDeleteServerTitle(name),
            text: inUse ? texts.dialogDeleteServerTextInUse : texts.dialogDeleteServerText, primary: texts.dialogDeleteServerDelete,
            secondary: texts.dialogCancel
        )
    }
}

/// «Копировать ссылку»: ссылка — в буфер (как именно — решает окно), подсказка «Ссылка скопирована» сверху листа на 1,8 с.
enum CardShare {
    @MainActor
    static func copy(_ link: String, layers: PgLayers?, texts: PgTexts, pasteboard: (String) -> Void) {
        pasteboard(link)
        layers?.show(texts.cardShareCopied, for: PgToastDuration.action)
    }
}

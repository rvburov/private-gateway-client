import PrivateGatewayCore
import PrivateGatewayTheme
import SwiftUI

/// Что умеет «Профили и подписки».
struct SubscriptionsActions {
    var settings: (SubscriptionSettings) -> Void = { _ in }
    var add: () -> Void = {}
    var update: (GroupItem) -> Void = { _ in }
    /// Название и новый адрес; false — адрес не http(s), ничего не сохранено.
    var edit: (GroupItem, String, String) async -> Bool = { _, _, _ in true }
    /// Группа (nil — «Мои серверы»), ее название и идет ли защита через ее сервер.
    var remove: (String?, String, Bool) -> Void = { _, _, _ in }
}

/// «Профили и подписки» (доска 12, SubscriptionsPage десктопа): шапка с «Назад» и «+» стоит на месте, прокручивается тело —
/// карточка «Как обновлять» (автообновление выпадающим списком, «только через VPN» с причиной, почему подписки сейчас не
/// обновляются, сообщения о выбранном сервере), рубрика «Подписки · N», карточки групп — состояние, маска адреса, когда
/// обновлялась, причина неудачи, «Обновить» (крутится стрелка), «Изменить» (нижний лист), корзина (вопрос), — «Мои серверы»
/// последней и пунктирная «Добавить подписку».
struct SubscriptionsView: View {
    let texts: PgTexts
    let settings: SubscriptionSettings
    let groups: [GroupItem]
    let manualCount: Int
    let updating: Set<String>
    let home: HomeState
    let actions: SubscriptionsActions
    let onBack: () -> Void

    /// Группа в листе «Изменить» и открыт ли лист: группа остается, пока лист уезжает вниз, — лист не пустеет на ходу.
    @State private var editing: String?
    @State private var editOpen: Bool
    @Environment(\.pgLayers) private var layers

    init(
        texts: PgTexts, settings: SubscriptionSettings, groups: [GroupItem], manualCount: Int, updating: Set<String> = [], home: HomeState,
        editing: String? = nil, actions: SubscriptionsActions, onBack: @escaping () -> Void
    ) {
        self.texts = texts
        self.settings = settings
        self.groups = groups
        self.manualCount = manualCount
        self.updating = updating
        self.home = home
        self.actions = actions
        self.onBack = onBack
        _editing = State(initialValue: editing)
        _editOpen = State(initialValue: editing != nil)
    }

    var body: some View {
        VStack(spacing: 0) {
            PgPageHeader(
                title: texts.settingsAdvProfilesTitle, texts: texts,
                action: PgHeaderAction(icon: PgIcons.plus, label: texts.subsAdd, identifier: "subs.add.header", action: actions.add), fixed: true,
                onBack: onBack
            )
            ScrollView {
                VStack(alignment: .leading, spacing: 14) {
                    settingsCard
                    Overline(text: texts.subsSection(SubscriptionsLook.cards(groups: groups.count, manualCount: manualCount)))
                        .padding(.horizontal, PgSpace.xs)
                        .padding(.top, PgSpace.xxs)
                        .padding(.bottom, -PgSpace.xxs)
                    if groups.isEmpty && manualCount == 0 {
                        Text(texts.subsEmpty)
                            .pgText(PgType.body)
                            .foregroundStyle(Color(PgColor.Text.secondary))
                            .fixedSize(horizontal: false, vertical: true)
                            .padding(.horizontal, PgSpace.xs)
                    }

                    ForEach(groups, id: \.id) { group in
                        // «Обновить» — только у подписки с адресом; профиль из ссылок не обновляется.
                        let update: (() -> Void)? = group.canUpdate ? { actions.update(group) } : nil
                        GroupCard(
                            texts: texts, name: group.name, address: group.maskedUrl ?? texts.subsManualAddress, chip: SubscriptionsLook.chip(group),
                            meta: SubscriptionsLook.meta(group, now: Date(), texts), failure: SubscriptionsLook.failure(group, texts),
                            busy: updating.contains(group.id), onUpdate: update, onEdit: { edit(group.id) }, onRemove: { ask(group.id) }
                        )
                        .accessibilityIdentifier("group.\(group.id)")
                    }

                    if manualCount > 0 {
                        // «Мои серверы»: серверы без группы — добавлены ссылками; не обновляются и не меняются, удалить можно разом.
                        GroupCard(
                            texts: texts, name: texts.subsMineName, address: texts.subsMineAddress, chip: .manual,
                            meta: SubscriptionsLook.servers(manualCount, texts), onRemove: { ask(SubscriptionsLook.mine) }
                        )
                        .accessibilityIdentifier("group.mine")
                    }

                    addButton
                }
                .padding(.horizontal, PgSpace.windowSideMobile)
                .padding(.bottom, PgLayout.scrollBottomPadding)
            }
        }
        .pgSheet(isPresented: $editOpen) {
            if let group = groups.first(where: { $0.id == editing }) {
                SubscriptionEditView(texts: texts, group: group, onSave: { name, url in await actions.edit(group, name, url) }, onClose: { editOpen = false })
            }
        }
        .accessibilityIdentifier("subscriptions")
    }

    private func edit(_ id: String) {
        editing = id
        editOpen = true
    }

    /// Корзина: сначала вопрос (доска 12, DialogView десктопа) — «Удалить» красная основная, «Отмена»; удаление — только
    /// после «Удалить».
    private func ask(_ id: String) {
        let group = groups.first { $0.id == id }
        let name = group?.name ?? texts.subsMineName
        let inUse = SubscriptionsLook.inUse(home) == id
        let dialog = SubscriptionsLook.removeDialog(name: name, servers: group?.servers ?? manualCount, inUse: inUse, texts)
        PgLayers.confirming(dialog, in: layers) {
            actions.remove(group?.id, name, inUse)
        }
    }

    /// «Как обновлять» (.sb-settings): три строки настройки через полосы — автообновление выпадающим списком, «только через
    /// VPN» (защита выключена — третьей строкой, почему подписки не обновляются), сообщения о выбранном сервере.
    private var settingsCard: some View {
        VStack(spacing: 0) {
            PgSettingRow(title: texts.subsRefreshTitle, caption: texts.subsRefreshText) {
                PgSelect(
                    options: SubscriptionsLook.refreshOptions(texts), selection: settings.refresh, label: texts.subsRefreshTitle, identifier: "subs.refresh",
                    onSelect: { interval in
                        var next = settings
                        next.refresh = interval
                        actions.settings(next)
                    }
                )
            }

            PgDivider().padding(.horizontal, PgSpace.card)
            PgToggleRow(
                title: texts.subsVpnTitle, text: texts.subsVpnText, warning: SubscriptionsLook.blockNote(settings, state: home.status.state, texts),
                isOn: settings.onlyViaVpn, padded: true, identifier: "subs.vpn"
            ) { on in
                var next = settings
                next.onlyViaVpn = on
                actions.settings(next)
            }

            PgDivider().padding(.horizontal, PgSpace.card)
            PgToggleRow(title: texts.subsNotifyTitle, text: texts.subsNotifyText, isOn: settings.notifySelected, padded: true, identifier: "subs.notify") { on in
                var next = settings
                next.notifySelected = on
                actions.settings(next)
            }
        }
        .padding(.vertical, 2)
        .pgCard(padding: 0)
    }

    /// «Добавить подписку» (.sb-add): пунктирная рамка accent.line на accent.tint, «+» и подпись accent.text полужирным —
    /// последней, после всех карточек; ведет на «Добавить профиль».
    private var addButton: some View {
        let shape = RoundedRectangle(cornerRadius: 14, style: .continuous)
        return Button(action: actions.add) {
            HStack(spacing: PgSpace.s) {
                PgIcon(data: PgIcons.plus, color: Color(PgColor.Accent.text), size: 18, lineWidth: 2.2)
                Text(texts.subsAdd).pgText(PgType.bodyStrong).foregroundStyle(Color(PgColor.Accent.text)).lineLimit(1)
            }
            .frame(maxWidth: .infinity, minHeight: 46)
            .background(shape.fill(Color(PgColor.Accent.tint)))
            .overlay(shape.strokeBorder(Color(PgColor.Accent.line), style: StrokeStyle(lineWidth: 1.5, dash: [5, 4])))
            .contentShape(Rectangle())
        }
        .buttonStyle(PgPressStyle())
        .accessibilityIdentifier("subs.add")
    }
}

/// Правила «Профилей и подписок» — как SubscriptionsPageViewModel и SubscriptionCardViewModel десктопа.
enum SubscriptionsLook {
    /// «Мои серверы» там, где ждут id группы, — метка группы без подписки.
    static let mine = ""

    /// Число в рубрике «Подписки · N»: все карточки — группы и «Мои серверы», если в них есть серверы.
    static func cards(groups: Int, manualCount: Int) -> Int {
        groups + (manualCount > 0 ? 1 : 0)
    }

    /// Пункты автообновления по порядку — выключено, 6, 12 и 24 часа.
    static func refreshOptions(_ texts: PgTexts) -> [PgOption<RefreshInterval>] {
        [
            PgOption(value: RefreshInterval.off, title: texts.subsRefreshOff), PgOption(value: RefreshInterval.every6Hours, title: texts.subsRefresh6),
            PgOption(value: RefreshInterval.every12Hours, title: texts.subsRefresh12), PgOption(value: RefreshInterval.every24Hours, title: texts.subsRefresh24),
        ]
    }

    /// Почему подписки сейчас не обновляются: включено «только через VPN», а защита не «Подключено»; иначе — nil.
    static func blockNote(_ settings: SubscriptionSettings, state: TunnelState, _ texts: PgTexts) -> String? {
        settings.onlyViaVpn && state != .connected ? texts.subsVpnBlockedOff : nil
    }

    /// Группа сервера, через который идет или поднимается защита (в любом состоянии, кроме «Выключено», — как у десктопа):
    /// удалять ее — значит отключить VPN. «Мои серверы» — `mine`; защита выключена — nil.
    static func inUse(_ home: HomeState) -> String? {
        guard home.status.state != .off, let server = home.inUse else {
            return nil
        }

        return server.subscriptionId ?? mine
    }

    /// Вопрос перед удалением группы: значок корзины на красном, «Удалить «…»?», сколько серверов уйдет с ключами (и что VPN
    /// отключится, если защита идет через один из них), «Удалить» — красная основная, «Отмена».
    static func removeDialog(name: String, servers: Int, inUse: Bool, _ texts: PgTexts) -> PgDialog {
        let count = Self.servers(servers, texts)
        return PgDialog(
            kind: .danger, icon: PgIcons.trash, title: texts.subsRemoveTitle(name), text: inUse ? texts.subsRemoveTextInUse(count) : texts.subsRemoveText(count),
            primary: texts.dialogDeleteServerDelete, secondary: texts.dialogCancel
        )
    }

    /// Состояние группы: без адреса — «Вручную», последняя попытка не удалась — «Ошибка», иначе «Работает».
    static func chip(_ group: GroupItem) -> GroupChip {
        !group.canUpdate ? .manual : group.failure != nil ? .error : .ok
    }

    /// «12 серверов · обновлено в 18:10» или после неудачи «12 серверов · последняя попытка 5 мин назад».
    static func meta(_ group: GroupItem, now: Date, _ texts: PgTexts) -> String {
        let servers = Self.servers(group.servers, texts)
        if group.failure != nil, let attempt = group.lastAttemptAt {
            return texts.subsMetaAttempt(servers, ago(TimeText.ago(attempt, now: now), texts))
        }

        if let updated = group.updatedAt {
            return texts.subsMetaUpdated(servers, day(TimeText.day(updated, now: now, zone: .current), texts))
        }

        return servers
    }

    /// Причина последней неудачи и что осталось прошлое: «Сервер ответил 403 — доступ запрещен… Оставлена прошлая версия
    /// списка — серверы работают.»; удачно — nil.
    static func failure(_ group: GroupItem, _ texts: PgTexts) -> String? {
        group.failure.map { texts.subsError(reason(FailureReason.of($0, status: group.status), texts)) }
    }

    /// Подсказка в поле нового адреса — маска текущего; у профиля без адреса — начало адреса.
    static func urlPlaceholder(_ group: GroupItem) -> String {
        group.maskedUrl ?? "https://…"
    }

    /// «1 сервер», «3 сервера», «12 серверов».
    static func servers(_ count: Int, _ texts: PgTexts) -> String {
        texts.text(key: "subs.servers." + Plural.form(count, texts.language.rawValue), [String(count)]) ?? String(count)
    }

    static func day(_ day: Day, _ texts: PgTexts) -> String {
        switch day {
        case let .today(hour, minute):
            return texts.whenAt(String(format: "%02d:%02d", hour, minute))
        case .yesterday:
            return texts.whenYesterday
        case let .date(year, month, day):
            var calendar = Calendar(identifier: .gregorian)
            calendar.timeZone = .current
            let formatter = DateFormatter()
            formatter.locale = Locale(identifier: texts.language.rawValue)
            formatter.setLocalizedDateFormatFromTemplate("dMMM")
            let date = calendar.date(from: DateComponents(year: year, month: month, day: day)) ?? Date()
            return formatter.string(from: date)
        }
    }

    static func ago(_ ago: Ago, _ texts: PgTexts) -> String {
        switch ago {
        case .now: return texts.agoNow
        case let .minutes(count): return texts.agoMinutes(count)
        case let .hours(count): return texts.agoHours(count)
        case let .days(count): return texts.agoDays(count)
        }
    }

    /// Причина неудачи с заглавной буквы: «Сервер подписки не ответил вовремя».
    static func reason(_ reason: FailureReason, _ texts: PgTexts) -> String {
        let key = TextKeys.failure(reason)
        let text = texts.text(key: key.key, key.value.map { [$0] } ?? []) ?? ""
        return text.prefix(1).uppercased() + text.dropFirst()
    }
}

/// Состояние группы: «Работает», «Ошибка», «Вручную».
enum GroupChip: Equatable {
    case ok
    case error
    case manual
}

/// Метка состояния (.sb-health): круглая, caption полужирным; «Работает» — success.tint и success.badge, «Ошибка» — тона
/// warning (последняя попытка не удалась, а прошлый список работает), «Вручную» — серая.
private struct StateChip: View {
    let texts: PgTexts
    let chip: GroupChip

    var body: some View {
        let (text, color, tint): (String, PgRGBA, PgRGBA) = switch chip {
        case .ok: (texts.subsChipOk, PgColor.Status.Success.badge, PgColor.Status.Success.tint)
        case .error: (texts.subsChipError, PgColor.Status.Warning.badge, PgColor.Status.Warning.tint)
        case .manual: (texts.subsChipManual, PgColor.Text.secondary, PgColor.Control.segmentTrack)
        }
        Text(text)
            .pgText(PgType.caption.with(weight: 600))
            .foregroundStyle(Color(color))
            .lineLimit(1)
            .padding(.horizontal, 9)
            .padding(.vertical, 3)
            .background(Capsule().fill(Color(tint)))
            .fixedSize()
    }
}

/// Карточка группы (.sb-card): название (title, 700) и маска адреса моноширинным мелко, справа метка состояния; число
/// серверов и время; причина неудачи — оранжевая плашка; внизу «Обновить» (мягкая, стрелка крутится, пока идет), «Изменить»
/// и корзина справа цветом danger.text. У «Моих серверов» — только корзина.
private struct GroupCard: View {
    let texts: PgTexts
    let name: String
    let address: String
    let chip: GroupChip
    let meta: String
    var failure: String?
    var busy = false
    var onUpdate: (() -> Void)?
    var onEdit: (() -> Void)?
    let onRemove: () -> Void

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: 18, style: .continuous)
        VStack(alignment: .leading, spacing: PgSpace.s) {
            HStack(alignment: .top, spacing: 0) {
                VStack(alignment: .leading, spacing: 3) {
                    Text(name).pgText(PgType.title.with(weight: 700)).foregroundStyle(Color(PgColor.Text.primary)).lineLimit(1)
                    Text(address)
                        .pgText(PgTextStyle(size: PgType.caption.size, weight: PgType.mono.weight, mono: true))
                        .foregroundStyle(Color(PgColor.Text.muted))
                        .lineLimit(1)
                }
                .padding(.trailing, PgSpace.m)
                .frame(maxWidth: .infinity, alignment: .leading)

                StateChip(texts: texts, chip: chip)
            }

            Text(meta).pgText(PgType.small).foregroundStyle(Color(PgColor.Text.secondary)).accessibilityIdentifier("group.meta")
            if let failure {
                Text(failure)
                    .pgText(PgType.caption)
                    .foregroundStyle(Color(PgColor.Status.Warning.badge))
                    .fixedSize(horizontal: false, vertical: true)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(.vertical, PgSpace.s)
                    .padding(.horizontal, PgSpace.m)
                    .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(Color(PgColor.Status.Warning.tint)))
                    .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous).strokeBorder(Color(PgColor.Status.Warning.line), lineWidth: 1))
                    .accessibilityIdentifier("group.error")
            }

            HStack(spacing: 0) {
                if let onUpdate {
                    PgSmallButton(label: texts.subsUpdate, icon: PgIcons.refresh, soft: true, spinning: busy, identifier: "subs.update", action: onUpdate)
                        .padding(.trailing, PgSpace.xs)
                }

                if let onEdit {
                    PgSmallButton(label: texts.subsEdit, identifier: "subs.edit", action: onEdit)
                }

                Spacer(minLength: PgSpace.s)
                Button(action: onRemove) {
                    PgIcon(data: PgIcons.trash, color: Color(PgColor.Status.Danger.text), size: 18, lineWidth: 1.9)
                        .frame(width: PgLayout.touchTarget, height: PgLayout.touchTarget)
                        .contentShape(Rectangle())
                }
                .buttonStyle(PgPressStyle())
                .accessibilityLabel(texts.subsRemove(name))
                .accessibilityIdentifier("subs.remove")
            }
            .padding(.top, 2)
        }
        .padding(.top, 14)
        .padding(.trailing, PgSpace.l)
        .padding(.bottom, PgSpace.l)
        .padding(.leading, PgSpace.card)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(shape.fill(Color(PgColor.Surface.card)))
        .overlay(shape.strokeBorder(Color(PgColor.Line.border), lineWidth: 1))
    }
}

/// Лист «Изменить» (.sb-sheet, SubscriptionEditView десктопа): заголовок «Изменить «…»» и крестик «Закрыть»; «Название» до
/// 64 знаков и «Новый адрес подписки» моноширинным с подсказкой-маской текущего (пустой — прежний); адрес хранится только в
/// Связке ключей; внизу рядом «Отмена» и «Сохранить». Профилю без адреса тоже можно дать адрес — он станет подпиской.
struct SubscriptionEditView: View {
    let texts: PgTexts
    let group: GroupItem
    let onSave: (String, String) async -> Bool
    let onClose: () -> Void

    @State private var name: String
    // Новый адрес подписки — секрет: в сохраненное состояние окна он не пишется (@State живет только в памяти).
    @State private var url = ""
    @State private var bad = false
    @State private var saving = false

    init(texts: PgTexts, group: GroupItem, onSave: @escaping (String, String) async -> Bool, onClose: @escaping () -> Void) {
        self.texts = texts
        self.group = group
        self.onSave = onSave
        self.onClose = onClose
        _name = State(initialValue: group.name)
    }

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: PgSpace.s) {
                Text(texts.subsEditTitle(group.name))
                    .pgText(PgType.status)
                    .foregroundStyle(Color(PgColor.Text.primary))
                    .lineLimit(1)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .accessibilityAddTraits(.isHeader)
                Button(action: onClose) {
                    PgIcon(data: PgIcons.close, color: Color(PgColor.Text.secondary), size: 20, lineWidth: 2.2)
                        .frame(width: PgLayout.touchTarget, height: PgLayout.touchTarget)
                        .contentShape(Rectangle())
                }
                .buttonStyle(PgPressStyle())
                .accessibilityLabel(texts.cardClose)
                .accessibilityIdentifier("subs.edit.close")
            }

            ScrollView {
                VStack(alignment: .leading, spacing: 14) {
                    VStack(alignment: .leading, spacing: PgSpace.xs) {
                        label(texts.subsEditName)
                        PgField(placeholder: "", text: $name, identifier: "subs.edit.name")
                            .accessibilityLabel(texts.subsEditName)
                    }

                    VStack(alignment: .leading, spacing: PgSpace.xs) {
                        label(texts.subsEditUrl)
                        PgField(
                            placeholder: SubscriptionsLook.urlPlaceholder(group), text: $url,
                            style: PgTextStyle(size: PgType.caption.size, weight: PgType.mono.weight, mono: true), identifier: "subs.edit.url"
                        )
                        .keyboardType(.URL)
                        .accessibilityLabel(texts.subsEditUrl)
                        Text(texts.subsEditUrlNote(texts.iosKeychain))
                            .pgText(PgType.caption)
                            .foregroundStyle(Color(PgColor.Text.muted))
                            .fixedSize(horizontal: false, vertical: true)
                        if bad {
                            Text(texts.subsEditUrlBad)
                                .pgText(PgType.caption)
                                .foregroundStyle(Color(PgColor.Status.Warning.badge))
                                .accessibilityIdentifier("subs.edit.bad")
                        }
                    }
                }
                .padding(.vertical, PgSpace.card)
            }

            HStack(spacing: PgSpace.s) {
                PgActionButton(label: texts.dialogCancel, kind: .secondary, identifier: "subs.edit.cancel", height: 50, action: onClose)
                PgActionButton(label: texts.subsEditSave, enabled: canSave && !saving, identifier: "subs.edit.save", height: 50) {
                    save()
                }
            }
        }
        .onChange(of: url) { _ in bad = false }
        .onChange(of: name) { next in
            if next.count > ServerNames.maxLength {
                name = String(next.prefix(ServerNames.maxLength))
            }
        }
        .accessibilityIdentifier("subs.editor")
    }

    private var canSave: Bool {
        !name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }

    private func label(_ text: String) -> some View {
        Text(text).pgText(PgType.small.with(weight: PgType.bodyStrong.weight)).foregroundStyle(Color(PgColor.Text.secondary))
    }

    /// «Сохранить»: новый адрес сразу проверяется загрузкой; не http(s) — подсказка под полем, лист остается.
    private func save() {
        saving = true
        Task {
            let saved = await onSave(name, url)
            saving = false
            if saved {
                onClose()
            } else {
                bad = true
            }
        }
    }
}

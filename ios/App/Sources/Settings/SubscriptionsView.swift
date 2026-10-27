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

/// «Профили и подписки» (доска 12 в одну колонку) — как `SubscriptionsScreen` Android: автообновление, «только через VPN»
/// (защита выключена — подписки не обновляются), сообщения о выбранном сервере; карточки подписок и профилей — состояние,
/// маска адреса, когда обновлялась, причина неудачи, «Обновить», «Изменить», «Удалить»; «Мои серверы» — последней.
struct SubscriptionsView: View {
    let texts: PgTexts
    let settings: SubscriptionSettings
    let groups: [GroupItem]
    let manualCount: Int
    let updating: Set<String>
    let home: HomeState
    let actions: SubscriptionsActions
    let onBack: () -> Void

    @State private var editing: String?
    /// Удаляемая группа: id подписки или `Self.mine`; `confirming` — открыт вопрос «Удалить?».
    @State private var removing: String?
    @State private var confirming = false

    /// «Мои серверы» в `removing` — метка группы без подписки.
    private static let mine = ""

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
    }

    /// Группа сервера, через который идет защита: удалять ее — значит отключить VPN. «Мои серверы» — `mine`.
    private var inUse: String? {
        home.servers.first { home.active($0.id) }.map { $0.subscriptionId ?? Self.mine }
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: PgSpace.l) {
                PgPageHeader(title: texts.settingsAdvProfilesTitle, texts: texts, onBack: onBack)
                settingsCard
                Overline(text: texts.subsSection(groups.count))
                PgButton(label: texts.subsAdd, identifier: "subs.add", action: actions.add)
                if groups.isEmpty && manualCount == 0 {
                    Text(texts.subsEmpty).pgText(PgType.body).foregroundStyle(Color(PgColor.Text.secondary))
                }

                ForEach(groups, id: \.id) { group in
                    if editing == group.id {
                        EditCard(texts: texts, group: group, onSave: { name, url in await actions.edit(group, name, url) }, onClose: { editing = nil })
                    } else {
                        GroupCard(
                            texts: texts, group: group, busy: updating.contains(group.id), onUpdate: { actions.update(group) }, onEdit: { editing = group.id },
                            onRemove: { ask(group.id) }
                        )
                    }
                }

                if manualCount > 0 {
                    mineCard
                }
            }
            .padding(.horizontal, PgSpace.windowSideMobile)
            .padding(.bottom, PgLayout.scrollBottomPadding)
        }
        .alert(removeTitle, isPresented: $confirming) {
            Button(texts.dialogDeleteServerDelete, role: .destructive) {
                if let target = removing {
                    let group = groups.first { $0.id == target }
                    actions.remove(group?.id, group?.name ?? texts.subsMineName, inUse == target)
                }
            }
            Button(texts.dialogCancel, role: .cancel) {}
        } message: {
            Text(removeText)
        }
        .accessibilityIdentifier("subscriptions")
    }

    private func ask(_ id: String) {
        removing = id
        confirming = true
    }

    private var removeTitle: String {
        let group = groups.first { $0.id == removing }
        return texts.subsRemoveTitle(group?.name ?? texts.subsMineName)
    }

    private var removeText: String {
        let group = groups.first { $0.id == removing }
        let servers = Self.servers(group?.servers ?? manualCount, texts)
        return inUse == removing ? texts.subsRemoveTextInUse(servers) : texts.subsRemoveText(servers)
    }

    private var settingsCard: some View {
        VStack(alignment: .leading, spacing: PgSpace.xs) {
            Text(texts.subsRefreshTitle).pgText(PgType.bodyStrong).foregroundStyle(Color(PgColor.Text.primary))
            Text(texts.subsRefreshText).pgText(PgType.caption).foregroundStyle(Color(PgColor.Text.secondary))
            ForEach([(RefreshInterval.off, texts.subsRefreshOff), (.every6Hours, texts.subsRefresh6), (.every12Hours, texts.subsRefresh12),
                     (.every24Hours, texts.subsRefresh24)], id: \.0) { item in
                PgChoiceRow(title: item.1, selected: settings.refresh == item.0, identifier: "refresh.\(item.0.rawValue)") {
                    var next = settings
                    next.refresh = item.0
                    actions.settings(next)
                }
            }

            PgToggleRow(title: texts.subsVpnTitle, text: texts.subsVpnText, isOn: settings.onlyViaVpn, identifier: "subs.vpn") { on in
                var next = settings
                next.onlyViaVpn = on
                actions.settings(next)
            }
            .padding(.top, PgSpace.s)
            if settings.onlyViaVpn && home.status.state != .connected {
                Text(texts.subsVpnBlockedOff)
                    .pgText(PgType.small)
                    .foregroundStyle(Color(PgColor.Status.Caution.text))
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(PgSpace.s)
                    .background(RoundedRectangle(cornerRadius: PgRadius.button, style: .continuous).fill(Color(PgColor.Status.Caution.tint)))
                    .accessibilityIdentifier("subs.vpn.blocked")
            }

            PgToggleRow(title: texts.subsNotifyTitle, text: texts.subsNotifyText, isOn: settings.notifySelected, identifier: "subs.notify") { on in
                var next = settings
                next.notifySelected = on
                actions.settings(next)
            }
        }
        .pgCard()
    }

    /// «Мои серверы»: серверы без группы — добавлены ссылками; не обновляются, удалить можно разом.
    private var mineCard: some View {
        VStack(alignment: .leading, spacing: PgSpace.xs) {
            HStack {
                Text(texts.subsMineName).pgText(PgType.bodyStrong).foregroundStyle(Color(PgColor.Text.primary))
                Spacer(minLength: PgSpace.s)
                StateChip(texts: texts, chip: .manual)
            }

            Text(texts.subsMineAddress).pgText(PgType.small).foregroundStyle(Color(PgColor.Text.secondary))
            Text(Self.servers(manualCount, texts)).pgText(PgType.caption).foregroundStyle(Color(PgColor.Text.muted))
            PgTextAction(label: texts.subsRemove(texts.subsMineName), color: PgColor.Status.Danger.text, identifier: "subs.remove") {
                ask(Self.mine)
            }
        }
        .pgCard()
        .accessibilityIdentifier("group.mine")
    }

    /// «1 сервер», «3 сервера», «12 серверов».
    static func servers(_ count: Int, _ texts: PgTexts) -> String {
        texts.text(key: "subs.servers." + Plural.form(count, texts.language.rawValue), [String(count)]) ?? String(count)
    }
}

/// Состояние группы: «Работает», «Ошибка», «Вручную» — словом и цветом.
private struct StateChip: View {
    enum Chip {
        case ok
        case error
        case manual
    }

    let texts: PgTexts
    let chip: Chip

    var body: some View {
        let (text, color, tint): (String, PgRGBA, PgRGBA) = switch chip {
        case .ok: (texts.subsChipOk, PgColor.Status.Success.text, PgColor.Status.Success.tint)
        case .error: (texts.subsChipError, PgColor.Status.Danger.text, PgColor.Status.Danger.tint)
        case .manual: (texts.subsChipManual, PgColor.Text.secondary, PgColor.Control.segmentTrack)
        }
        Text(text)
            .pgText(PgType.caption)
            .foregroundStyle(Color(color))
            .padding(.horizontal, PgSpace.s)
            .padding(.vertical, 2)
            .background(RoundedRectangle(cornerRadius: PgRadius.chip, style: .continuous).fill(Color(tint)))
    }
}

/// Карточка подписки или профиля: название и состояние, адрес (маска), серверы и время, причина неудачи, действия.
private struct GroupCard: View {
    let texts: PgTexts
    let group: GroupItem
    let busy: Bool
    let onUpdate: () -> Void
    let onEdit: () -> Void
    let onRemove: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: PgSpace.xs) {
            HStack {
                Text(group.name).pgText(PgType.bodyStrong).foregroundStyle(Color(PgColor.Text.primary)).lineLimit(1)
                Spacer(minLength: PgSpace.s)
                StateChip(texts: texts, chip: !group.canUpdate ? .manual : group.failure != nil ? .error : .ok)
            }

            Text(group.maskedUrl ?? texts.subsManualAddress)
                .pgText(group.maskedUrl != nil ? PgType.mono : PgType.small)
                .foregroundStyle(Color(PgColor.Text.secondary))
                .lineLimit(1)
            Text(meta).pgText(PgType.caption).foregroundStyle(Color(PgColor.Text.muted)).accessibilityIdentifier("group.meta")
            if let failure = group.failure {
                Text(texts.subsError(Self.reason(FailureReason.of(failure, status: group.status), texts)))
                    .pgText(PgType.small)
                    .foregroundStyle(Color(PgColor.Status.Danger.text))
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(PgSpace.s)
                    .background(RoundedRectangle(cornerRadius: PgRadius.button, style: .continuous).fill(Color(PgColor.Status.Danger.tint)))
                    .padding(.top, PgSpace.s)
                    .accessibilityIdentifier("group.error")
            }

            HStack(spacing: PgSpace.m) {
                if group.canUpdate {
                    PgTextAction(label: busy ? texts.importDownloading : texts.subsUpdate, enabled: !busy, identifier: "subs.update", action: onUpdate)
                }

                PgTextAction(label: texts.subsEdit, identifier: "subs.edit", action: onEdit)
                PgTextAction(label: texts.subsRemove(group.name), color: PgColor.Status.Danger.text, identifier: "subs.remove", action: onRemove)
                    .lineLimit(1)
            }
        }
        .pgCard()
        .accessibilityIdentifier("group.\(group.id)")
    }

    /// «12 серверов · обновлено в 18:10» или после неудачи «12 серверов · последняя попытка 5 мин назад».
    private var meta: String {
        let servers = SubscriptionsView.servers(group.servers, texts)
        let now = Date()
        if group.failure != nil, let attempt = group.lastAttemptAt {
            return texts.subsMetaAttempt(servers, Self.ago(TimeText.ago(attempt, now: now), texts))
        }

        if let updated = group.updatedAt {
            return texts.subsMetaUpdated(servers, Self.day(TimeText.day(updated, now: now, zone: .current), texts))
        }

        return servers
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

/// «Изменить»: название и новый адрес (пустой — прежний); адрес хранится только в Связке ключей.
private struct EditCard: View {
    let texts: PgTexts
    let group: GroupItem
    let onSave: (String, String) async -> Bool
    let onClose: () -> Void

    @State private var name: String
    // Новый адрес подписки — секрет: в сохраненное состояние окна он не пишется (@State живет только в памяти).
    @State private var url = ""
    @State private var bad = false

    init(texts: PgTexts, group: GroupItem, onSave: @escaping (String, String) async -> Bool, onClose: @escaping () -> Void) {
        self.texts = texts
        self.group = group
        self.onSave = onSave
        self.onClose = onClose
        _name = State(initialValue: group.name)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: PgSpace.s) {
            Text(texts.subsEditTitle(group.name)).pgText(PgType.title).foregroundStyle(Color(PgColor.Text.primary))
            Text(texts.subsEditName).pgText(PgType.caption).foregroundStyle(Color(PgColor.Text.secondary))
            PgField(placeholder: "", text: $name, identifier: "subs.edit.name")
            // Профилю без адреса тоже можно дать адрес — он станет подпиской.
            Text(texts.subsEditUrl).pgText(PgType.caption).foregroundStyle(Color(PgColor.Text.secondary))
            PgField(placeholder: "https://", text: $url, identifier: "subs.edit.url")
                .keyboardType(.URL)
                .onChange(of: url) { _ in bad = false }
            Text(texts.subsEditUrlNote(texts.iosKeychain)).pgText(PgType.caption).foregroundStyle(Color(PgColor.Text.muted))
            if bad {
                Text(texts.subsEditUrlBad).pgText(PgType.small).foregroundStyle(Color(PgColor.Status.Danger.text)).accessibilityIdentifier("subs.edit.bad")
            }

            PgButton(label: texts.subsEditSave, enabled: !name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, identifier: "subs.edit.save") {
                Task {
                    if await onSave(name, url) {
                        onClose()
                    } else {
                        bad = true
                    }
                }
            }
            .padding(.top, PgSpace.s)
            PgTextAction(label: texts.dialogCancel, identifier: "subs.edit.cancel", action: onClose)
        }
        .pgCard()
        .accessibilityIdentifier("subs.editor")
    }
}

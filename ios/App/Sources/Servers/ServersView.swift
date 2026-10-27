import PrivateGatewayCore
import PrivateGatewayTheme
import SwiftUI

/// «Серверы» (доска 2 в одну колонку) — как `ServersScreen` Android: поиск, фильтр протоколов и порядок, вкладки «Все» ·
/// «Избранные» · «Недавние», группы по подпискам и «Мои серверы». Нажатие на строку выбирает сервер (под защитой — сразу
/// переподключение), «⋯» — карточка. Сервер меняется только нажатием пользователя.
struct ServersView: View {
    let texts: PgTexts
    let home: HomeState
    @Binding var query: ServerListQuery
    var onSelect: (String) -> Void = { _ in }
    var onOpen: (String) -> Void = { _ in }
    var onAdd: () -> Void = {}

    @State private var filterOpen = false

    var body: some View {
        let groups = ServerList.build(home.servers, home.subscriptions, query, locale: Locale(identifier: texts.language.rawValue))
        let empty = ServerList.empty(home.servers, groups, query)
        ScrollView {
            LazyVStack(alignment: .leading, spacing: PgSpace.s) {
                HStack {
                    Text(texts.navServers).pgText(PgType.h1).foregroundStyle(Color(PgColor.Text.primary))
                    Spacer()
                    PgRoundButton(icon: PgIcons.plus, label: texts.headerAdd, identifier: "servers.add", action: onAdd)
                }
                .padding(.top, PgSpace.l)
                .padding(.bottom, PgSpace.s)

                if !home.servers.isEmpty {
                    searchRow
                    if filterOpen {
                        filterPanel
                    }

                    tabs
                }

                if let empty {
                    Text(emptyText(empty))
                        .pgText(PgType.body)
                        .foregroundStyle(Color(PgColor.Text.secondary))
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .pgCard()
                        .accessibilityIdentifier("servers.empty")
                }

                ForEach(groups, id: \.subscription?.id) { group in
                    GroupHeader(texts: texts, group: group, tab: query.tab)
                    ForEach(group.servers, id: \.id) { server in
                        ServerRow(
                            texts: texts, server: server, selected: server.id == home.selectedId, active: home.active(server.id),
                            onSelect: { onSelect(server.id) }, onMore: { onOpen(server.id) }
                        )
                    }
                }

                Text(texts.serversNote).pgText(PgType.caption).foregroundStyle(Color(PgColor.Text.muted)).padding(.top, PgSpace.s)
            }
            .padding(.horizontal, PgSpace.windowSideMobile)
            .padding(.bottom, PgLayout.scrollBottomPadding)
        }
        .accessibilityIdentifier("servers")
    }

    private func emptyText(_ empty: ServerListEmpty) -> String {
        switch empty {
        case .noServers: return texts.serversNone
        case .noFavorites: return texts.serversEmptyFavorites
        case .noRecent: return texts.serversEmptyRecent
        case .nothingFound: return texts.serversEmpty
        }
    }

    /// Поиск (название, страна, протокол — адрес запечатан, решение 420) и кнопка фильтра с числом отличий от обычного.
    private var searchRow: some View {
        let count = ServerList.filterCount(home.servers, query)
        let highlighted = filterOpen || count > 0
        return HStack(spacing: PgSpace.s) {
            HStack(spacing: PgSpace.s) {
                PgIcon(data: PgIcons.search, color: Color(PgColor.Text.muted), size: 18)
                TextField("", text: $query.search, prompt: Text(texts.androidServersSearchPlaceholder).foregroundColor(Color(PgColor.Text.muted)))
                    .pgText(PgType.body)
                    .foregroundStyle(Color(PgColor.Text.primary))
                    .textInputAutocapitalization(.never)
                    .autocorrectionDisabled()
                    .accessibilityLabel(texts.serversSearchLabel)
                    .accessibilityIdentifier("servers.search")
            }
            .padding(.horizontal, PgSpace.m)
            .frame(minHeight: PgLayout.touchTarget)
            .background(RoundedRectangle(cornerRadius: PgRadius.input, style: .continuous).fill(Color(PgColor.Surface.solid)))
            .overlay(RoundedRectangle(cornerRadius: PgRadius.input, style: .continuous).stroke(Color(PgColor.Line.border), lineWidth: 1))

            Button {
                filterOpen.toggle()
            } label: {
                ZStack(alignment: .topTrailing) {
                    PgIcon(data: PgIcons.filter, color: Color(highlighted ? PgColor.Accent.text : PgColor.Text.primary))
                        .frame(width: PgLayout.touchTarget, height: PgLayout.touchTarget)
                    if count > 0 {
                        Text(String(count))
                            .pgText(PgType.caption)
                            .foregroundStyle(Color(PgColor.Surface.solid))
                            .padding(.horizontal, 5)
                            .background(Capsule().fill(Color(PgColor.Accent.base)))
                            .padding(2)
                            .accessibilityIdentifier("servers.filter.count")
                    }
                }
                .background(RoundedRectangle(cornerRadius: PgRadius.input, style: .continuous).fill(Color(highlighted ? PgColor.Accent.tint : PgColor.Surface.solid)))
                .overlay(RoundedRectangle(cornerRadius: PgRadius.input, style: .continuous).stroke(Color(PgColor.Line.border), lineWidth: 1))
            }
            .buttonStyle(.plain)
            .accessibilityLabel(texts.serversFilter)
            .accessibilityAddTraits(filterOpen ? .isSelected : [])
            .accessibilityIdentifier("servers.filter")
        }
    }

    /// Фильтр: протоколы, что есть в списке, порядок, «Сбросить» и «Готово».
    private var filterPanel: some View {
        VStack(alignment: .leading, spacing: 0) {
            Overline(text: texts.serversFilterProtocols)
            ForEach(ServerList.protocolsIn(home.servers), id: \.self) { proto in
                PgToggleRow(
                    title: ServerFacts.protocolName(proto), isOn: !query.hiddenProtocols.contains(proto), identifier: "filter.\(proto.rawValue)"
                ) { _ in
                    if query.hiddenProtocols.contains(proto) {
                        query.hiddenProtocols.remove(proto)
                    } else {
                        query.hiddenProtocols.insert(proto)
                    }
                }
            }

            Overline(text: texts.serversFilterSort).padding(.top, PgSpace.m)
            PgChoiceRow(title: texts.serversSortLatency, selected: query.sort == .latency, identifier: "sort.latency") { query.sort = .latency }
            PgChoiceRow(title: texts.serversSortName, selected: query.sort == .name, identifier: "sort.name") { query.sort = .name }
            HStack(spacing: PgSpace.m) {
                Spacer()
                PgTextAction(label: texts.serversFilterReset, identifier: "filter.reset") {
                    query.hiddenProtocols = []
                    query.sort = .latency
                }
                PgTextAction(label: texts.serversFilterDone, identifier: "filter.done") { filterOpen = false }
            }
        }
        .pgCard()
        .accessibilityIdentifier("servers.filter.panel")
    }

    /// Вкладки-сегменты: «Все · N», «Избранные», «Недавние».
    private var tabs: some View {
        HStack(spacing: 0) {
            ForEach([ServerListTab.all, .favorites, .recent], id: \.self) { tab in
                let selected = tab == query.tab
                Button {
                    query.tab = tab
                } label: {
                    Text(label(tab))
                        .pgText(selected ? PgType.bodyStrong : PgType.body)
                        .foregroundStyle(Color(selected ? PgColor.Text.primary : PgColor.Text.secondary))
                        .lineLimit(1)
                        .frame(maxWidth: .infinity, minHeight: PgLayout.touchTarget - 6)
                        .background(RoundedRectangle(cornerRadius: PgRadius.button, style: .continuous).fill(selected ? Color(PgColor.Surface.solid) : .clear))
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .accessibilityAddTraits(selected ? [.isButton, .isSelected] : .isButton)
                .accessibilityIdentifier("tab.\(tab)")
            }
        }
        .padding(3)
        .background(RoundedRectangle(cornerRadius: PgRadius.button, style: .continuous).fill(Color(PgColor.Control.segmentTrack)))
        .accessibilityElement(children: .contain)
        .accessibilityLabel(texts.serversTabs)
    }

    private func label(_ tab: ServerListTab) -> String {
        switch tab {
        case .all: return texts.serversTabAll(home.servers.count)
        case .favorites: return texts.serversTabFavorites
        case .recent: return texts.serversTabRecent
        }
    }
}

/// Заголовок группы: «Подписка · 7» и «обновлено 14:20», «Мои серверы · 2», «Избранные · 3», «Недавние · 4».
struct GroupHeader: View {
    let texts: PgTexts
    let group: ServerGroup
    let tab: ServerListTab

    var body: some View {
        HStack {
            Overline(text: title)
            Spacer()
            if let subscription = group.subscription {
                Text(subscription.updatedAt.map { texts.serversUpdated(Self.clock($0)) } ?? texts.serversUpdatedNever)
                    .pgText(PgType.caption)
                    .foregroundStyle(Color(PgColor.Text.muted))
            }
        }
        .padding(.top, PgSpace.m)
    }

    private var title: String {
        let count = group.servers.count
        if let subscription = group.subscription {
            return texts.serversGroupSubscription(subscription.name, count)
        }

        switch tab {
        case .favorites: return texts.serversGroupFavorites(count)
        case .recent: return texts.serversGroupRecent(count)
        case .all: return texts.serversGroupManual(count)
        }
    }

    /// «14:20» — время по часам телефона.
    static func clock(_ date: Date) -> String {
        let formatter = DateFormatter()
        formatter.dateFormat = "HH:mm"
        return formatter.string(from: date)
    }
}

/// Строка сервера: значок страны, звезда избранного, название с пометкой «активен» или «выбран», протокол и порт, задержка,
/// «⋯» — карточка.
struct ServerRow: View {
    let texts: PgTexts
    let server: ServerItem
    let selected: Bool
    let active: Bool
    let onSelect: () -> Void
    let onMore: () -> Void

    /// Пометка у названия: «активен» — защита идет через него, «выбран» — выбран, защита выключена.
    private var tag: String? {
        if active {
            return texts.serversTagActive
        }

        return selected ? texts.serversTagSelected : nil
    }

    var body: some View {
        HStack(spacing: 0) {
            Button(action: onSelect) {
                HStack(spacing: PgSpace.l) {
                    CountryBadge(code: server.countryCode)
                    VStack(alignment: .leading, spacing: 2) {
                        HStack(spacing: PgSpace.xs) {
                            if server.favorite {
                                FavoriteMark(size: 14)
                            }

                            Text(server.displayName).pgText(PgType.bodyStrong).foregroundStyle(Color(PgColor.Text.primary)).lineLimit(1)
                            if let tag {
                                Text(tag)
                                    .pgText(PgType.caption)
                                    .foregroundStyle(Color(active ? PgColor.Status.Success.text : PgColor.Accent.text))
                                    .padding(.horizontal, PgSpace.xs)
                                    .background(RoundedRectangle(cornerRadius: PgRadius.chip).fill(Color(active ? PgColor.Status.Success.tint : PgColor.Accent.tint)))
                            }
                        }

                        Text(server.summary + " · " + texts.serverPort(server.port)).pgText(PgType.caption).foregroundStyle(Color(PgColor.Text.secondary))
                        if server.missingSince != nil {
                            Text(texts.cardMissing).pgText(PgType.caption).foregroundStyle(Color(PgColor.Status.Caution.text))
                        }
                    }

                    Spacer(minLength: PgSpace.s)
                    LatencyText(texts: texts, latency: server.latency)
                }
                .padding(.leading, PgSpace.m)
                .padding(.vertical, PgSpace.s)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .accessibilityAddTraits(selected ? [.isButton, .isSelected] : .isButton)
            .accessibilityIdentifier("server.\(server.id)")

            Button(action: onMore) {
                PgIcon(data: PgIcons.more, color: Color(PgColor.Text.muted))
                    .frame(width: PgLayout.touchTarget, height: PgLayout.touchTarget)
                    .contentShape(Circle())
            }
            .buttonStyle(.plain)
            .accessibilityLabel(texts.serversMoreName(server.displayName))
            .accessibilityIdentifier("more.\(server.id)")
        }
        .background(RoundedRectangle(cornerRadius: PgRadius.card, style: .continuous).fill(Color(selected ? PgColor.Accent.tint : PgColor.Surface.card)))
    }
}

/// Звезда избранного: заливка и контур цветами favorite.* макета.
struct FavoriteMark: View {
    let size: CGFloat

    var body: some View {
        ZStack {
            PgIconShape(data: PgIcons.star).fill(Color(PgColor.Favorite.fill))
            PgIcon(data: PgIcons.star, color: Color(PgColor.Favorite.stroke), size: size)
        }
        .frame(width: size, height: size)
        .accessibilityHidden(true)
    }
}

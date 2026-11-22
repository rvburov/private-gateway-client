import PrivateGatewayCore
import PrivateGatewayTheme
import SwiftUI

/// «Серверы» (доска 02, ServersPage десктопа): шапка «Серверы» с «Обновить серверы» и «+», поле поиска с кнопкой фильтра и
/// вкладки «Все» · «Избранные» · «Недавние» стоят на месте, прокручивается только список — группы по подпискам и «Мои
/// серверы». Фильтр протоколов и порядок — панель под полем поиска поверх вкладок и списка: «Готово», нажатие мимо и Esc ее
/// закрывают. Нажатие на строку выбирает сервер (под защитой — сразу переподключение), «⋯» — карточка сервера нижним листом.
/// Сервер меняется только нажатием пользователя.
struct ServersView: View {
    let texts: PgTexts
    let home: HomeState
    @Binding var query: ServerListQuery
    var latencyUpdated: [String: Date]
    var latencyViaVpn: Bool
    var onSelect: (String) -> Void
    var onOpen: (String) -> Void
    var onAdd: () -> Void
    /// «Обновить серверы»: список заново и задержка серверов через отдельное ядро приложения. Пока идет замер,
    /// крутится только стрелка, кнопка не гаснет — повтор отсекает модель.
    var onRefresh: () -> Void

    @State private var filterOpen: Bool

    init(
        texts: PgTexts, home: HomeState, query: Binding<ServerListQuery>, filterOpen: Bool = false, latencyUpdated: [String: Date] = [:],
        latencyViaVpn: Bool = false, onSelect: @escaping (String) -> Void = { _ in },
        onOpen: @escaping (String) -> Void = { _ in }, onAdd: @escaping () -> Void = {}, onRefresh: @escaping () -> Void = {}
    ) {
        self.texts = texts
        self.home = home
        self.latencyUpdated = latencyUpdated
        self.latencyViaVpn = latencyViaVpn
        _query = query
        self.onSelect = onSelect
        self.onOpen = onOpen
        self.onAdd = onAdd
        self.onRefresh = onRefresh
        _filterOpen = State(initialValue: filterOpen)
    }

    var body: some View {
        let groups = ServerList.build(home.servers, home.subscriptions, query, locale: Locale(identifier: texts.language.rawValue))
        let empty = ServerList.empty(home.servers, groups, query)
        VStack(spacing: 0) {
            header
            VStack(spacing: PgSpace.m) {
                searchRow
                tabs
                Text(measurementNote)
                    .pgText(PgType.caption)
                    .foregroundStyle(Color(PgColor.Text.muted))
                    .fixedSize(horizontal: false, vertical: true)
                    .accessibilityIdentifier("servers.measure.path")
            }
            .padding(.horizontal, PgSpace.windowSideMobile)

            ScrollView {
                LazyVStack(alignment: .leading, spacing: PgSpace.l) {
                    if let empty {
                        Text(emptyText(empty))
                            .pgText(PgType.body)
                            .foregroundStyle(Color(PgColor.Text.muted))
                            .multilineTextAlignment(.center)
                            .frame(maxWidth: .infinity)
                            .padding(.vertical, PgSpace.windowSide)
                            .padding(.horizontal, PgSpace.xxs)
                            .accessibilityIdentifier("servers.empty")
                    }

                    ForEach(groups, id: \.subscription?.id) { group in
                        VStack(alignment: .leading, spacing: PgSpace.xs) {
                            GroupHeader(texts: texts, group: group, tab: query.tab)
                            ForEach(group.servers, id: \.id) { server in
                                ServerRow(
                                    texts: texts, server: server, tag: ServersLook.tag(server, home: home, texts),
                                    measuredAt: latencyUpdated[server.id], trackFreshness: true,
                                    onSelect: { onSelect(server.id) }, onMore: { onOpen(server.id) }
                                )
                            }
                        }
                    }

                    Text(texts.serversNote)
                        .pgText(PgType.caption.with(lineHeight: PgType.body.lineHeight))
                        .foregroundStyle(Color(PgColor.Text.muted))
                        .fixedSize(horizontal: false, vertical: true)
                        .padding(.horizontal, PgSpace.xxs)
                }
                .padding(.horizontal, PgSpace.windowSideMobile)
                .padding(.top, PgSpace.l)
                .padding(.bottom, PgLayout.scrollBottomPadding)
            }
        }
        .overlayPreferenceValue(SearchFieldBounds.self) { anchor in
            GeometryReader { proxy in
                if filterOpen, let anchor {
                    filterLayer(below: proxy[anchor])
                }
            }
        }
        .accessibilityIdentifier("servers")
    }

    private var measurementNote: String {
        let path = texts.text(key: latencyViaVpn ? "ios.latency.viaVpn" : "ios.latency.direct") ?? ""
        guard home.measuring else { return path }
        let progress = texts.text(key: "latency.progress", [String(latencyUpdated.count), String(home.servers.count)]) ?? ""
        return progress + " · " + path
    }

    private func emptyText(_ empty: ServerListEmpty) -> String {
        switch empty {
        case .noServers: return texts.serversNone
        case .noFavorites: return texts.serversEmptyFavorites
        case .noRecent: return texts.serversEmptyRecent
        case .nothingFound: return texts.serversEmpty
        }
    }

    /// Шапка (.pg-header, 60): «Серверы»; «Обновить серверы» — при замере крутится только стрелка, кнопка не сжимается и не
    /// гаснет (.btn-icon.steady); «+» — добавить ссылку или подписку.
    private var header: some View {
        HStack(spacing: 0) {
            Text(texts.navServers)
                .pgText(PgType.h1)
                .foregroundStyle(Color(PgColor.Text.primary))
                .lineLimit(1)
                .minimumScaleFactor(0.8)
                .frame(maxWidth: .infinity, alignment: .leading)
                .accessibilityAddTraits(.isHeader)
            Button(action: onRefresh) {
                PgSpinningIcon(data: PgIcons.refresh, spinning: home.measuring, size: 20, lineWidth: 1.9)
                    .frame(width: PgLayout.touchTarget, height: PgLayout.touchTarget)
                    .contentShape(Rectangle())
            }
            .buttonStyle(SteadyButtonStyle())
            .accessibilityLabel(texts.serverRefresh)
            .accessibilityIdentifier("servers.refresh")
            Button(action: onAdd) {
                PgIcon(data: PgIcons.plus, size: 22, lineWidth: 2)
                    .frame(width: PgLayout.touchTarget, height: PgLayout.touchTarget)
                    .contentShape(Rectangle())
            }
            .buttonStyle(PgPressStyle())
            .padding(.leading, 2)
            .accessibilityLabel(texts.headerAdd)
            .accessibilityIdentifier("servers.add")
        }
        .padding(.leading, PgSpace.windowSideMobile)
        .padding(.trailing, PgSpace.l)
        .frame(height: 60)
    }

    /// Поиск (.search.sv-search): высота 46, фон surface.card, рамка line.border, пока панель фильтра открыта — accent.base;
    /// справа внутри поля — кнопка фильтра 38 × 38 с числом отличий от обычного (скрытые протоколы и порядок не по пингу).
    /// Ищется название, код страны и протокол: адрес запечатан в Связке ключей вместе с ключом, города у сервера нет
    /// (решение 420), поэтому и подсказка — о том, что ищется на самом деле.
    private var searchRow: some View {
        let count = ServerList.filterCount(home.servers, query)
        let highlighted = filterOpen || count > 0
        let shape = RoundedRectangle(cornerRadius: PgRadius.button, style: .continuous)
        return HStack(spacing: 0) {
            PgIcon(data: PgIcons.search, color: Color(PgColor.Text.muted), size: 17, lineWidth: 2)
            TextField("", text: $query.search, prompt: Text(texts.androidServersSearchPlaceholder).foregroundColor(Color(PgColor.Text.muted)))
                .pgText(PgType.body)
                .foregroundStyle(Color(PgColor.Text.primary))
                .textInputAutocapitalization(.never)
                .autocorrectionDisabled()
                .padding(.horizontal, PgSpace.s)
                .accessibilityLabel(texts.serversSearchLabel)
                .accessibilityIdentifier("servers.search")

            Button {
                filterOpen.toggle()
            } label: {
                PgIcon(data: PgIcons.filter, color: Color(highlighted ? PgColor.Accent.text : PgColor.Text.secondary), size: 19, lineWidth: 1.9)
                    .frame(width: 38, height: 38)
                    .background(RoundedRectangle(cornerRadius: PgRadius.input, style: .continuous).fill(highlighted ? Color(PgColor.Accent.tint) : Color.clear))
                    .overlay(alignment: .topTrailing) {
                        if count > 0 {
                            Text(String(count))
                                .pgText(PgTextStyle(size: PgType.overline.size, weight: 700))
                                .foregroundStyle(Color(PgColor.Surface.solid))
                                .lineLimit(1)
                                .padding(.horizontal, PgSpace.xxs)
                                .frame(minWidth: 16, minHeight: 16)
                                .background(Capsule().fill(Color(PgColor.Accent.base)))
                                .offset(x: -3, y: 3)
                                .accessibilityIdentifier("servers.filter.count")
                        }
                    }
                    .frame(width: PgLayout.touchTarget, height: PgLayout.touchTarget)
                    .contentShape(Rectangle())
            }
            .buttonStyle(PgPressStyle())
            .accessibilityLabel(texts.serversFilter)
            .accessibilityValue(count > 0 ? String(count) : "")
            .accessibilityAddTraits(filterOpen ? [.isButton, .isSelected] : .isButton)
            .accessibilityIdentifier("servers.filter")
        }
        .padding(.leading, 14)
        .padding(.trailing, 1)
        .frame(height: 46)
        .background(shape.fill(Color(PgColor.Surface.card)))
        .overlay(shape.strokeBorder(Color(filterOpen ? PgColor.Accent.base : PgColor.Line.border), lineWidth: 1))
        .anchorPreference(key: SearchFieldBounds.self, value: .bounds) { $0 }
    }

    /// Панель фильтра поверх вкладок и списка: на 6 ниже поля поиска и по его ширине; нажатие мимо закрывает ее.
    private func filterLayer(below field: CGRect) -> some View {
        ZStack(alignment: .topLeading) {
            Color.clear
                .contentShape(Rectangle())
                .onTapGesture { filterOpen = false }
                .accessibilityHidden(true)
            filterPanel
                .frame(width: field.width)
                .padding(.leading, field.minX)
                .padding(.top, field.maxY + CGFloat(PgSpace.xs))
        }
    }

    /// «Фильтр и сортировка» (.popup.sv-pop): протоколы, что есть в списке, — флажки в две колонки; порядок — сегменты «По
    /// пингу | По названию»; слева «Сбросить», справа «Готово».
    private var filterPanel: some View {
        VStack(alignment: .leading, spacing: PgSpace.l) {
            Overline(text: texts.serversFilterProtocols)
            LazyVGrid(columns: [GridItem(.flexible(), spacing: PgSpace.s), GridItem(.flexible())], alignment: .leading, spacing: 2) {
                ForEach(ServerList.protocolsIn(home.servers), id: \.self) { proto in
                    PgCheck(title: ServerFacts.protocolName(proto), isOn: !query.hiddenProtocols.contains(proto), identifier: "filter.\(proto.rawValue)") { on in
                        if on {
                            query.hiddenProtocols.remove(proto)
                        } else {
                            query.hiddenProtocols.insert(proto)
                        }
                    }
                }
            }

            Overline(text: texts.serversFilterSort)
            PgSegments(
                options: [PgOption(value: ServerSort.latency, title: texts.serversSortLatency), PgOption(value: ServerSort.name, title: texts.serversSortName)],
                selection: query.sort, identifier: "sort", onSelect: { sort in query.sort = sort }
            )

            HStack(spacing: PgSpace.s) {
                PgActionButton(label: texts.serversFilterReset, kind: .secondary, identifier: "filter.reset") {
                    query = ServersLook.reset(query)
                }
                .fixedSize(horizontal: true, vertical: false)
                Spacer(minLength: 0)
                PgActionButton(label: texts.serversFilterDone, identifier: "filter.done") {
                    filterOpen = false
                }
                .fixedSize(horizontal: true, vertical: false)
            }
        }
        .padding(14)
        .background(RoundedRectangle(cornerRadius: PgRadius.card, style: .continuous).fill(Color(PgColor.Surface.solid)))
        .overlay(RoundedRectangle(cornerRadius: PgRadius.card, style: .continuous).strokeBorder(Color(PgColor.Line.border), lineWidth: 1))
        .pgShadow(PgShadow.popup)
        .accessibilityElement(children: .contain)
        .accessibilityAddTraits(.isModal)
        .accessibilityAction(.escape) { filterOpen = false }
        .accessibilityIdentifier("servers.filter.panel")
    }

    /// Вкладки «Все · N», «Избранные», «Недавние» — сегменты (RadioButton.segment десктопа).
    private var tabs: some View {
        PgSegments(
            options: [ServerListTab.all, .favorites, .recent].map { PgOption(value: $0, title: label($0)) },
            selection: query.tab, identifier: "tab", onSelect: { tab in query.tab = tab }
        )
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

/// Где поле поиска: панель фильтра встает под ним.
private struct SearchFieldBounds: PreferenceKey {
    static var defaultValue: Anchor<CGRect>? { nil }

    static func reduce(value: inout Anchor<CGRect>?, nextValue: () -> Anchor<CGRect>?) {
        value = nextValue() ?? value
    }
}

/// Кнопка без отклика на нажатие (.btn-icon.steady): ее вид не меняется, крутится только значок.
private struct SteadyButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
    }
}

/// Правила строки и фильтра «Серверов» — как `ServersPageViewModel` десктопа.
enum ServersLook {
    /// Метка строки и ее выделение: у сервера в работе (защита идет через него, иначе — выбранного) — «активен», только пока
    /// защита подключена, в остальных состояниях (и в «Подключении», «Переподключении») — «выбран»; у других строк метки нет.
    static func tag(_ server: ServerItem, home: HomeState, _ texts: PgTexts) -> String? {
        guard home.inUse?.id == server.id else {
            return nil
        }

        return home.status.state == .connected ? texts.serversTagActive : texts.serversTagSelected
    }

    /// Подключается ли телефон к серверу: Hysteria2 и TUIC ядру Xray не по силам — их не выбрать и не подключить.
    static func supported(_ server: ServerItem) -> Bool {
        ServerFacts.coreName(server.protocol) != nil
    }

    /// «Сбросить»: все протоколы видны, порядок — по пингу; поиск и вкладка остаются.
    static func reset(_ query: ServerListQuery) -> ServerListQuery {
        var next = query
        next.hiddenProtocols = []
        next.sort = .latency
        return next
    }
}

/// Заголовок группы: «Подписка · 7» и «обновлено 14:20», «Мои серверы · 2», «Избранные · 3», «Недавние · 4».
struct GroupHeader: View {
    let texts: PgTexts
    let group: ServerGroup
    let tab: ServerListTab

    var body: some View {
        HStack(alignment: .lastTextBaseline, spacing: PgSpace.s) {
            Overline(text: title)
                .lineLimit(1)
                .frame(maxWidth: .infinity, alignment: .leading)
            if let subscription = group.subscription {
                Text(subscription.updatedAt.map { texts.serversUpdated(Self.clock($0)) } ?? texts.serversUpdatedNever)
                    .pgText(PgType.caption)
                    .foregroundStyle(Color(PgColor.Text.muted))
                    .lineLimit(1)
            }
        }
        .padding(.horizontal, PgSpace.xxs)
        .padding(.bottom, 2)
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

/// Строка сервера (Border.serverRow, .sv-row): значок страны 40 × 28, название полужирным и под ним протокол моноширинным
/// мелко, справа — метка «активен» или «выбран» и задержка, «⋯» — карточка. Выделенная (сервер в работе) — белая, рамка
/// status.success.base и тень selected; обычная — surface.card и рамка line.border. Адреса во второй строке нет: он запечатан
/// в Связке ключей вместе с ключом (решения 399, 420). Hysteria2 и TUIC телефон не подключает: строку не выбрать (прозрачность
/// 0,6, как у недоступного сегмента), карточка открывается.
struct ServerRow: View {
    let texts: PgTexts
    let server: ServerItem
    /// Метка «активен» или «выбран» (`ServersLook.tag`); есть — строка выделена.
    let tag: String?
    var measuredAt: Date? = nil
    var trackFreshness = false
    let onSelect: () -> Void
    let onMore: () -> Void

    var body: some View {
        let on = tag != nil
        let supported = ServersLook.supported(server)
        let latency = LatencyLook.of(server.latency, texts)
        let fresh = !trackFreshness || LatencyFreshness.isFresh(measuredAt)
        let shape = RoundedRectangle(cornerRadius: PgRadius.button, style: .continuous)
        ZStack(alignment: .trailing) {
            Button(action: onSelect) {
                HStack(spacing: 0) {
                    CountryGlyph(code: server.countryCode)
                    VStack(alignment: .leading, spacing: 2) {
                        Text(server.title).pgText(PgType.bodyStrong).foregroundStyle(Color(PgColor.Text.primary)).lineLimit(1)
                        Text(server.summary)
                            .pgText(PgTextStyle(size: PgType.overline.size, weight: PgType.caption.weight, mono: true))
                            .foregroundStyle(Color(PgColor.Text.muted))
                            .lineLimit(1)
                    }
                    .padding(.leading, PgSpace.l)
                    .padding(.trailing, PgSpace.s)
                    .frame(maxWidth: .infinity, alignment: .leading)

                    if let tag {
                        PgBadge(text: tag, ink: PgColor.Status.Success.badge, tint: PgColor.Status.Success.tint, kind: .pill)
                            .padding(.trailing, PgSpace.m)
                    }

                    VStack(alignment: .trailing, spacing: PgSpace.xxs) {
                        Text(latency.text)
                            .pgText(PgTextStyle(size: PgType.caption.size, weight: 600, mono: true))
                            .foregroundStyle(Color(fresh ? latency.color : PgColor.Text.muted))
                            .lineLimit(1)
                            .accessibilityLabel(latency.hint)
                        if trackFreshness, server.latency != nil {
                            Text(latencyStamp(fresh: fresh))
                                .pgText(PgType.caption)
                                .foregroundStyle(Color(PgColor.Text.muted))
                                .lineLimit(1)
                        }
                    }
                    .frame(minWidth: 52, alignment: .trailing)
                }
                .opacity(supported ? 1 : 0.6)
                .padding(.leading, PgSpace.l)
                .padding(.trailing, PgLayout.touchTarget)
                .padding(.vertical, PgSpace.s)
                .frame(maxWidth: .infinity, minHeight: 58)
                .background(shape.fill(Color(on ? PgColor.Surface.solid : PgColor.Surface.card)))
                .overlay(shape.strokeBorder(Color(on ? PgColor.Status.Success.base : PgColor.Line.border), lineWidth: 1.5))
                .pgShadow(on ? PgShadow.selected : [])
                .contentShape(Rectangle())
            }
            .buttonStyle(SteadyButtonStyle())
            .disabled(!supported)
            .accessibilityAddTraits(on ? [.isButton, .isSelected] : .isButton)
            .accessibilityHint(supported ? "" : texts.androidCardCoreNone)
            .accessibilityIdentifier("server.\(server.id)")

            Button(action: onMore) {
                PgIcon(data: PgIcons.more, color: Color(PgColor.Text.muted), size: 18, lineWidth: 3)
                    .frame(width: PgLayout.touchTarget, height: PgLayout.touchTarget)
                    .contentShape(Rectangle())
            }
            .buttonStyle(PgPressStyle())
            .padding(.trailing, 1)
            .accessibilityLabel(texts.serversMoreName(server.title))
            .accessibilityIdentifier("more.\(server.id)")
        }
    }

    private func latencyStamp(fresh: Bool) -> String {
        let stale = texts.text(key: "latency.stale.short") ?? ""
        guard let measuredAt else { return stale }
        return GroupHeader.clock(measuredAt) + (fresh ? "" : " · " + stale)
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

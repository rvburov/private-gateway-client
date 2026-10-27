import Foundation

/// Итог добавления: новых и уже бывших в списке.
public struct AddResult: Equatable, Sendable {
    public let added: Int
    public let duplicates: Int

    public init(added: Int, duplicates: Int) {
        self.added = added
        self.duplicates = duplicates
    }
}

/// Группа серверов для «Профили и подписки» (доска 12): подписка с адресом (открыто — только маска) или профиль без адреса;
/// последняя неудача обновления — причина и код ответа.
public struct GroupItem: Equatable, Sendable {
    public let id: String
    public let name: String
    public let maskedUrl: String?
    public let servers: Int
    public let updatedAt: Date?
    public let lastAttemptAt: Date?
    public let failure: DownloadFailure?
    public let status: Int?

    /// Обновляется только подписка с адресом; профиль из ссылок — нет.
    public var canUpdate: Bool { maskedUrl != nil }
}

/// Итог обновления подписки: сколько серверов пришло и ушло, что стало с выбранным и его название для уведомления.
public struct UpdateResult: Equatable, Sendable {
    public let added: Int
    public let removed: Int
    public let selected: SelectedChange
    public let selectedName: String?

    public init(added: Int, removed: Int, selected: SelectedChange, selectedName: String?) {
        self.added = added
        self.removed = removed
        self.selected = selected
        self.selectedName = selectedName
    }
}

/// Открытая запись сервера: подписи для списка, порядок, группа, замер. Адрес и ключи — только в ссылке в `SecretVault`.
struct ServerRecord: Codable, Equatable, Sendable {
    var id: String
    var name: String
    var countryCode: String
    var protocolName: String
    var security: String
    var transport: String
    var port: Int
    var position: Int
    var addedAt: Date
    var subscriptionId: String?
    var favorite = false
    var latencyMs: Int?
    var latencyFailure: String?
    var lastUsedAt: Date?
    var missingSince: Date?
    var customName: String?
}

/// Открытая запись группы: подписка или профиль; адрес подписки — только в `SecretVault`, открыто — маска.
struct SubscriptionRecord: Codable, Equatable, Sendable {
    var id: String
    var name: String
    var maskedUrl: String?
    var position: Int
    var addedAt: Date
    var updatedAt: Date?
    var lastAttemptAt: Date?
    var lastFailure: String?
    var lastStatus: Int?
}

/// Все открытые данные хранилища — один файл JSON.
struct StoredState: Codable, Equatable, Sendable {
    var servers: [ServerRecord] = []
    var subscriptions: [SubscriptionRecord] = []
}

/// Серверы пользователя и их группы — как `ServerRepository` Android: сервер — ссылка в Связке ключей, открыто лежат только
/// подписи для списка; адрес подписки (в нем токен доступа) — тоже только в Связке ключей, открыто — маска. Дубль — тот же
/// сервер под любым названием (`ServerKeys`). Актор: записи идут по одной, два добавления сразу не добавят одно дважды.
/// Файл читается заново на каждое действие: его же читает расширение туннеля, кеш в памяти разошелся бы с ним.
public actor ServerStore {
    private let file: any StateFile
    private let vault: any SecretVault
    private let clock: @Sendable () -> Date
    private let newId: @Sendable () -> String

    public init(
        file: any StateFile, vault: any SecretVault, clock: @escaping @Sendable () -> Date = { Date() },
        newId: @escaping @Sendable () -> String = { UUID().uuidString }
    ) {
        self.file = file
        self.vault = vault
        self.clock = clock
        self.newId = newId
    }

    /// Ключ записи Связки ключей со ссылкой сервера и с адресом подписки.
    static func serverKey(_ id: String) -> String { "server.\(id)" }

    static func subscriptionKey(_ id: String) -> String { "subscription.\(id)" }

    // MARK: Чтение

    /// Серверы в порядке добавления; запись с неизвестным протоколом (из будущей версии) не показывается.
    public func servers() throws -> [ServerItem] {
        try read().servers.sorted { $0.position < $1.position }.compactMap(Self.item)
    }

    /// Группы в их порядке с числом серверов.
    public func groups() throws -> [GroupItem] {
        let state = try read()
        return state.subscriptions.sorted { $0.position < $1.position }.map { record in
            GroupItem(
                id: record.id, name: record.name, maskedUrl: record.maskedUrl, servers: state.servers.filter { $0.subscriptionId == record.id }.count,
                updatedAt: record.updatedAt, lastAttemptAt: record.lastAttemptAt, failure: DownloadFailure.parse(record.lastFailure), status: record.lastStatus
            )
        }
    }

    /// Подписки и профили в их порядке — заголовки групп списка серверов.
    public func subscriptionRefs() throws -> [SubscriptionRef] {
        try read().subscriptions.sorted { $0.position < $1.position }.map { SubscriptionRef(id: $0.id, name: $0.name, updatedAt: $0.updatedAt) }
    }

    /// Сколько серверов в «Моих серверах» (без группы).
    public func manualCount() throws -> Int {
        try read().servers.filter { $0.subscriptionId == nil }.count
    }

    /// Сервер целиком — для подключения; нет в списке или ссылка не открылась — nil.
    public func load(_ id: String) throws -> ServerDefinition? {
        guard let record = try read().servers.first(where: { $0.id == id }) else {
            return nil
        }

        return try open(record)
    }

    /// Полный адрес подписки — для загрузки; не открылся или группа — профиль без адреса: nil.
    public func subscriptionUrl(_ id: String) throws -> URLComponents? {
        try vault.load(Self.subscriptionKey(id)).flatMap { SubscriptionAddress.parse($0) }
    }

    /// Ссылка для «Поделиться» — как сохранена: с названием из ссылки, а не своим. Открывается только по нажатию.
    public func shareLink(_ id: String) throws -> String? {
        try vault.load(Self.serverKey(id))
    }

    /// Адрес сервера для карточки — «адрес:порт», IPv6 в скобках; ссылка не открылась — nil.
    public func address(_ id: String) throws -> String? {
        try load(id).map { ServerFacts.address($0.host, $0.port) }
    }

    // MARK: Запись

    /// Добавить серверы; с названием группы — новой группой (подпиской, если есть `url`, иначе профилем), без названия — в
    /// «Мои серверы» (адрес без названия не хранится).
    @discardableResult
    public func add(_ servers: [ServerDefinition], group: String? = nil, url: URLComponents? = nil) throws -> AddResult {
        var state = try read()
        let groupName = group?.trimmingCharacters(in: .whitespacesAndNewlines)
        let named = groupName.flatMap { $0.isEmpty ? nil : $0 }
        var known = Set(try state.servers.compactMap { try open($0) }.map(ServerKeys.key))
        var position = state.servers.map(\.position).max() ?? -1
        let now = clock()
        let groupId = named.map { _ in newId() }
        var fresh: [(ServerRecord, ServerDefinition)] = []
        var duplicates = 0
        for server in servers {
            if !known.insert(ServerKeys.key(server)).inserted {
                duplicates += 1
                continue
            }

            position += 1
            fresh.append((record(server, id: newId(), position: position, now: now, group: groupId), server))
        }

        // Секреты — раньше открытой записи: запись без ссылки была бы сервером, который не открыть.
        for (record, server) in fresh {
            try vault.store(LinkWriter.write(server), for: Self.serverKey(record.id))
        }

        if let named, let groupId {
            let address = url.map { SubscriptionAddress.mask($0) }
            if let url, let text = url.string {
                try vault.store(text, for: Self.subscriptionKey(groupId))
            }

            state.subscriptions.append(
                SubscriptionRecord(
                    id: groupId, name: named, maskedUrl: address, position: (state.subscriptions.map(\.position).max() ?? -1) + 1, addedAt: now,
                    updatedAt: url == nil ? nil : now, lastAttemptAt: url == nil ? nil : now
                )
            )
        }

        state.servers += fresh.map { $0.0 }
        try write(state)
        return AddResult(added: fresh.count, duplicates: duplicates)
    }

    public func delete(_ id: String) throws {
        var state = try read()
        state.servers.removeAll { $0.id == id }
        try write(state)
        try vault.remove(Self.serverKey(id))
    }

    /// Новый список подписки (`SubscriptionMerge`): свои серверы узнаются и остаются со своим названием, избранным, задержкой и
    /// временем выбора; новые добавляются в группу; пропавшие удаляются, кроме выбранного — он помечается «пропал из подписки»
    /// и остается, пока выбран. Подписка — «обновлена сейчас», прошлая неудача снята.
    public func applyUpdate(_ id: String, incoming: [ServerDefinition], selectedId: String?) throws -> UpdateResult {
        var state = try read()
        guard let index = state.subscriptions.firstIndex(where: { $0.id == id }) else {
            return UpdateResult(added: 0, removed: 0, selected: .none, selectedName: nil)
        }

        let now = clock()
        var own: [StoredServer] = []
        var unreadable: [UnreadableServer] = []
        var elsewhere = Set<String>()
        for record in state.servers {
            let server = try open(record)
            if record.subscriptionId != id {
                if let server {
                    elsewhere.insert(ServerKeys.key(server))
                }
            } else if let server {
                own.append(StoredServer(id: record.id, server: server))
            } else {
                unreadable.append(UnreadableServer(id: record.id, name: record.name))
            }
        }

        let merge = SubscriptionMerge.merge(own: own, incoming: incoming, elsewhere: elsewhere, selectedId: selectedId, unreadable: unreadable)
        let missing = merge.missing.flatMap { missingId in state.servers.first { $0.id == missingId } }
        for (keptId, server) in merge.kept {
            guard let at = state.servers.firstIndex(where: { $0.id == keptId }) else {
                continue
            }

            try vault.store(LinkWriter.write(server), for: Self.serverKey(keptId))
            let fresh = record(server, id: keptId, position: state.servers[at].position, now: state.servers[at].addedAt, group: id)
            state.servers[at].name = fresh.name
            state.servers[at].countryCode = fresh.countryCode
            state.servers[at].protocolName = fresh.protocolName
            state.servers[at].security = fresh.security
            state.servers[at].transport = fresh.transport
            state.servers[at].port = fresh.port
            state.servers[at].missingSince = nil
        }

        var position = state.servers.map(\.position).max() ?? -1
        for server in merge.added {
            position += 1
            let added = record(server, id: newId(), position: position, now: now, group: id)
            try vault.store(LinkWriter.write(server), for: Self.serverKey(added.id))
            state.servers.append(added)
        }

        let removed = Set(merge.removed)
        state.servers.removeAll { removed.contains($0.id) }
        if let missingId = merge.missing, let at = state.servers.firstIndex(where: { $0.id == missingId }) {
            state.servers[at].missingSince = now
        }

        state.subscriptions[index].updatedAt = now
        state.subscriptions[index].lastAttemptAt = now
        state.subscriptions[index].lastFailure = nil
        state.subscriptions[index].lastStatus = nil
        try write(state)
        for removedId in merge.removed {
            try vault.remove(Self.serverKey(removedId))
        }

        return UpdateResult(added: merge.added.count, removed: merge.removed.count, selected: merge.selected, selectedName: missing.map { $0.customName ?? $0.name })
    }

    /// Подписка не обновилась: прошлый список остается, запоминаются время попытки, причина и код ответа.
    public func markFailed(_ id: String, failure: DownloadFailure, status: Int?) throws {
        try change { state in
            guard let index = state.subscriptions.firstIndex(where: { $0.id == id }) else {
                return
            }

            state.subscriptions[index].lastAttemptAt = clock()
            state.subscriptions[index].lastFailure = failure.rawValue
            state.subscriptions[index].lastStatus = status
        }
    }

    /// Новое название и, если дан, новый адрес подписки: с новым адресом прошлая неудача снимается.
    public func edit(_ id: String, name: String, url: URLComponents?) throws {
        if let url, let text = url.string {
            try vault.store(text, for: Self.subscriptionKey(id))
        }

        try change { state in
            guard let index = state.subscriptions.firstIndex(where: { $0.id == id }) else {
                return
            }

            let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
            if !trimmed.isEmpty {
                state.subscriptions[index].name = trimmed
            }

            if let url {
                state.subscriptions[index].maskedUrl = SubscriptionAddress.mask(url)
                state.subscriptions[index].lastAttemptAt = nil
                state.subscriptions[index].lastFailure = nil
                state.subscriptions[index].lastStatus = nil
            }
        }
    }

    /// Удалить группу со всеми ее серверами; nil — «Мои серверы». Номера удаленных серверов — чтобы снять выбор.
    @discardableResult
    public func remove(group: String?) throws -> [String] {
        var state = try read()
        let ids = state.servers.filter { $0.subscriptionId == group }.map(\.id)
        state.servers.removeAll { $0.subscriptionId == group }
        if let group {
            state.subscriptions.removeAll { $0.id == group }
        }

        try write(state)
        for id in ids {
            try vault.remove(Self.serverKey(id))
        }

        if let group {
            try vault.remove(Self.subscriptionKey(group))
        }

        return ids
    }

    public func setFavorite(_ id: String, _ favorite: Bool) throws {
        try changeServer(id) { $0.favorite = favorite }
    }

    /// Свое название; пустое — снова название из ссылки.
    public func rename(_ id: String, _ name: String?) throws {
        let trimmed = name?.trimmingCharacters(in: .whitespacesAndNewlines)
        try changeServer(id) { $0.customName = trimmed.flatMap { $0.isEmpty ? nil : $0 } }
    }

    /// Сервер выбран пользователем — для «Недавних».
    public func markUsed(_ id: String, at: Date? = nil) throws {
        let when = at ?? clock()
        try changeServer(id) { $0.lastUsedAt = when }
    }

    /// Итоги замера задержки — одной записью: список обновляется порцией, а не по строке.
    public func setLatency(_ results: [String: Latency]) throws {
        try change { state in
            for index in state.servers.indices {
                if let latency = results[state.servers[index].id] {
                    state.servers[index].latencyMs = latency.ms
                    state.servers[index].latencyFailure = latency.failure?.rawValue
                }
            }
        }
    }

    // MARK: Внутреннее

    private func read() throws -> StoredState {
        guard let data = try file.read() else {
            return StoredState()
        }

        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .millisecondsSince1970
        return try decoder.decode(StoredState.self, from: data)
    }

    private func write(_ state: StoredState) throws {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .millisecondsSince1970
        encoder.outputFormatting = [.sortedKeys]
        try file.write(encoder.encode(state))
    }

    private func change(_ body: (inout StoredState) -> Void) throws {
        var state = try read()
        body(&state)
        try write(state)
    }

    private func changeServer(_ id: String, _ body: (inout ServerRecord) -> Void) throws {
        try change { state in
            if let index = state.servers.firstIndex(where: { $0.id == id }) {
                body(&state.servers[index])
            }
        }
    }

    private func record(_ server: ServerDefinition, id: String, position: Int, now: Date, group: String?) -> ServerRecord {
        ServerRecord(
            id: id, name: server.name, countryCode: server.countryCode, protocolName: server.protocol.rawValue,
            security: server.security.kind.rawValue, transport: server.transport.kind.rawValue, port: server.port, position: position,
            addedAt: now, subscriptionId: group
        )
    }

    /// Сервер из ссылки в Связке ключей; название и страна — из открытой записи (по названию подписка узнает «изменился»),
    /// не свое название пользователя. Нет ссылки или она не разбирается — nil.
    private func open(_ record: ServerRecord) throws -> ServerDefinition? {
        guard let link = try vault.load(Self.serverKey(record.id)), var server = LinkParser.parse(link).server else {
            return nil
        }

        server.name = record.name
        server.countryCode = record.countryCode
        return server
    }

    private static func item(_ record: ServerRecord) -> ServerItem? {
        guard let proto = ProxyProtocol(rawValue: record.protocolName), let security = TransportSecurity(rawValue: record.security),
              let transport = TransportKind(rawValue: record.transport)
        else {
            return nil
        }

        let latency: Latency?
        if let ms = record.latencyMs {
            latency = .of(ms)
        } else if let failure = record.latencyFailure {
            latency = .failed(LatencyFailure(rawValue: failure) ?? .noResponse)
        } else {
            latency = nil
        }

        return ServerItem(
            id: record.id, linkName: record.name, countryCode: record.countryCode, protocol: proto, security: security, transport: transport,
            port: record.port, subscriptionId: record.subscriptionId, favorite: record.favorite, latency: latency, lastUsedAt: record.lastUsedAt,
            missingSince: record.missingSince, customName: record.customName
        )
    }
}

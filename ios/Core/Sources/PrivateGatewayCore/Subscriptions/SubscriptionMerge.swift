/// Что стало с выбранным сервером после обновления подписки.
public enum SelectedChange: Sendable {
    /// На месте или он не из этой подписки.
    case none

    /// Пропал из подписки; оставлен в списке, пока выбран.
    case missing

    /// Пропал, но в подписке есть сервер с тем же названием — у него другой адрес или ключи.
    case changed
}

/// Сервер подписки в списке: свой номер и описание; название в описании — из ссылки, не свое название пользователя.
public struct StoredServer: Sendable {
    public let id: String
    public let server: ServerDefinition

    public init(id: String, server: ServerDefinition) {
        self.id = id
        self.server = server
    }
}

/// Сервер подписки, чье описание не открылось (запечатанное не расшифровать — запись Связки ключей пропала): номер и
/// название из ссылки — оно хранится открыто.
public struct UnreadableServer: Sendable {
    public let id: String
    public let name: String

    public init(id: String, name: String) {
        self.id = id
        self.name = name
    }
}

/// Итог слияния: оставленные (номер → новое описание), новые, удаленные номера, выбранный, который пропал из подписки, и
/// что сказать о выбранном.
public struct SubscriptionMergeResult: Sendable {
    public let kept: [(id: String, server: ServerDefinition)]
    public let added: [ServerDefinition]
    public let removed: [String]
    public let missing: String?
    public let selected: SelectedChange
}

/// Обновление подписки — как `SubscriptionMerge` Android и `ApplyUpdate` десктопа: свой сервер узнается сначала точно
/// (`ServerKeys.key`), потом по выходу (`ServerKeys.exit`) — у него остаются номер, избранное и задержка; сервер, который уже
/// есть в другой группе, второй раз не добавляется; пропавшие удаляются, кроме выбранного: он остается, пока выбран, —
/// другой сервер сам не выбирается. Нечитаемые серверы группы не узнать — для подписки они пропали. Пропал ли выбранный или
/// изменился — по названию из ссылки.
public enum SubscriptionMerge {
    /// Пустой список подписку не заменяет — прошлый остается: вызывающий проверяет это до слияния (`SubscriptionFetch`).
    public static func merge(
        own: [StoredServer], incoming: [ServerDefinition], elsewhere: Set<String>, selectedId: String?, unreadable: [UnreadableServer] = []
    ) -> SubscriptionMergeResult {
        precondition(!incoming.isEmpty, "Пустой список не заменяет подписку — прошлый остается")
        var byKey: [String: StoredServer] = [:]
        var byExit: [String: StoredServer] = [:]
        for stored in own {
            if byKey[ServerKeys.key(stored.server)] == nil {
                byKey[ServerKeys.key(stored.server)] = stored
            }

            if byExit[ServerKeys.exit(stored.server)] == nil {
                byExit[ServerKeys.exit(stored.server)] = stored
            }
        }

        var seen = Set<String>()
        var kept: [(id: String, server: ServerDefinition)] = []
        var keptIds = Set<String>()
        var unmatched: [ServerDefinition] = []
        for server in incoming {
            let key = ServerKeys.key(server)
            if !seen.insert(key).inserted {
                continue
            }

            if let stored = byKey[key], keptIds.insert(stored.id).inserted {
                kept.append((stored.id, server))
            } else {
                unmatched.append(server)
            }
        }

        var added: [ServerDefinition] = []
        for server in unmatched {
            if let stored = byExit[ServerKeys.exit(server)], keptIds.insert(stored.id).inserted {
                kept.append((stored.id, server))
            } else if !elsewhere.contains(ServerKeys.key(server)) {
                added.append(server)
            }
        }

        // Пропавшие — номер и название из ссылки.
        let gone = own.filter { !keptIds.contains($0.id) }.map { (id: $0.id, name: $0.server.name) } + unreadable.map { (id: $0.id, name: $0.name) }
        let selectedGone = gone.first { $0.id == selectedId }
        let change: SelectedChange
        if let selectedGone {
            change = incoming.contains { $0.name == selectedGone.name } ? .changed : .missing
        } else {
            change = .none
        }

        return SubscriptionMergeResult(
            kept: kept, added: added, removed: gone.filter { $0.id != selectedGone?.id }.map { $0.id }, missing: selectedGone?.id, selected: change
        )
    }
}

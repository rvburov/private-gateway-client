/// О чем бывают уведомления (доски 19 и 22) — как `NoticeKind` Android. Важные — со звуком (обрыв, сервер не отвечает,
/// защита не включилась), остальные — без звука; `titleKey` — заголовок.
public enum NoticeKind: String, CaseIterable, Sendable {
    /// «Защищено» сменилось «Переподключением»: связь с сервером пропала.
    case connectionLost
    /// Попытки кончились: «Ошибка» с PGC-NET-001.
    case serverDown
    /// Защита не включилась или остановилась по другой причине — текст по коду ошибки.
    case protectionFailed
    /// Защита включилась или вернулась.
    case connected
    /// Защиту выключили.
    case disconnected
    /// Выбранный сервер пропал из подписки.
    case serverGone
    /// У выбранного сервера в подписке новый адрес или ключи.
    case serverChanged
    /// Подписка не обновилась по расписанию — прошлый список оставлен.
    case serversNotUpdated

    public var important: Bool {
        self == .connectionLost || self == .serverDown || self == .protectionFailed
    }

    public var titleKey: String {
        switch self {
        case .connectionLost: return "notify.lost.title"
        case .serverDown: return "notify.down.title"
        case .protectionFailed: return "notify.failed.title"
        case .connected: return "notify.on.title"
        case .disconnected: return "notify.off.title"
        case .serverGone: return "notify.gone.title"
        case .serverChanged: return "notify.changed.title"
        case .serversNotUpdated: return "notify.stale.title"
        }
    }
}

/// Кнопки уведомления; надписи — notify.action.<имя>.
public enum NoticeAction: String, CaseIterable, Sendable {
    /// «Открыть»: окно приложения.
    case open
    /// «Сменить сервер»: экран «Серверы».
    case changeServer
    /// «Выбрать сервер»: экран «Серверы».
    case chooseServer
    /// «Выбрать другой»: экран «Серверы».
    case chooseOther
    /// «Повторить»: подключиться к выбранному серверу снова.
    case retry
    /// «Позже»: закрыть уведомление.
    case later
    /// «Подключить»: включить защиту.
    case connect
    /// «Открыть» у подписок: экран «Профили и подписки».
    case openSubscriptions

    public var textKey: String { "notify.action." + rawValue }
}

/// Что сказать: событие, ключ текста и кнопки (не больше двух — доска 22). В тексте `{0}` — сервер, `{1}` — подписка, `{2}` —
/// причина: код ошибки словами и сам код или почему подписка не загрузилась. IP-адресов и имен сетей Wi-Fi в уведомлениях нет.
public struct Notice: Equatable, Sendable {
    /// Метка уведомлений о защите: обрыв, сервер, сбой, подключение.
    public static let protectionTag = "protection"
    /// Код сбоя, у которого своего кода нет, — как у десктопа.
    public static let unknownCode = "PGC-SVC"

    public var kind: NoticeKind
    public var textKey: String
    public var actions: [NoticeAction]
    public var server: String?
    public var subscription: String?
    public var code: String?
    public var failure: FailureReason?

    public init(
        _ kind: NoticeKind, _ textKey: String, _ actions: [NoticeAction], server: String? = nil, subscription: String? = nil, code: String? = nil,
        failure: FailureReason? = nil
    ) {
        self.kind = kind
        self.textKey = textKey
        self.actions = actions
        self.server = server
        self.subscription = subscription
        self.code = code
        self.failure = failure
    }

    public var important: Bool { kind.important }

    /// Метка: новое уведомление с той же меткой заменяет прежнее — о защите одно, последнее; у каждой подписки свое.
    public var tag: String {
        switch kind {
        case .serverGone, .serverChanged, .serversNotUpdated: return "servers-" + (subscription ?? "")
        default: return Self.protectionTag
        }
    }
}

/// Какие уведомления показывать (доска 22) — как у десктопа и Android; по умолчанию «Только важные».
public enum NotificationLevel: String, CaseIterable, Sendable {
    /// Важные и обычные.
    case all = "All"
    /// Обрыв, сервер не отвечает, защита не включилась.
    case importantOnly = "ImportantOnly"
    /// Никаких.
    case off = "Off"

    public static let `default` = NotificationLevel.importantOnly

    /// Из сохраненного имени (имена — как у Android): нет значения или неизвестное — по умолчанию.
    public static func read(_ name: String?) -> NotificationLevel {
        name.flatMap(NotificationLevel.init(rawValue:)) ?? .default
    }
}

/// Какое уведомление дает событие — правила `NotificationPolicy` Android. Блок при обрыве на iOS — системный «весь трафик
/// только через VPN»: сдавшийся туннель при нем держит трафик («Интернет заблокирован»), без него — трафик идет напрямую.
public enum NotificationPolicy {
    /// Уведомление о смене состояния туннеля — по двум состояниям подряд; свежие цифры и новые попытки в том же состоянии
    /// уведомлений не дают. `killSwitch` — блок при обрыве включен; nil — сказать нечего.
    public static func forChange(_ before: TunnelStatus, _ after: TunnelStatus, server: String?, killSwitch: Bool) -> Notice? {
        let moved = before.state != after.state
        let changed = moved || before.error != after.error
        let traffic = killSwitch ? "blocked" : "direct"
        var notice: Notice
        switch after.state {
        case .reconnecting where moved && before.state == .connected:
            // Пока идут попытки, маршруты туннеля остаются: трафик ждет — «Интернет заблокирован» при любой настройке.
            notice = Notice(.connectionLost, "notify.lost.blocked", [.changeServer, .open])
        case .error where changed && after.error == TunnelErrors.serverNotResponding:
            notice = Notice(.serverDown, "notify.down.\(traffic)", [.retry, .chooseServer])
        case .error where changed:
            notice = Notice(.protectionFailed, "notify.failed.\(traffic)", [.open], code: after.error ?? Notice.unknownCode)
        case .connected where moved:
            notice = Notice(.connected, before.state == .reconnecting ? "notify.on.back" : "notify.on.text", [])
        case .off where moved:
            notice = Notice(.disconnected, "notify.off.text", [.connect])
        default:
            return nil
        }

        notice.server = server
        return notice
    }

    /// Выбранный сервер пропал из подписки или изменился; на месте — сказать нечего.
    public static func forSelected(_ change: SelectedChange, server: String, subscription: String) -> Notice? {
        switch change {
        case .none: return nil
        case .changed: return Notice(.serverChanged, "notify.changed.text", [.chooseServer, .later], server: server, subscription: subscription)
        case .missing: return Notice(.serverGone, "notify.gone.text", [.chooseOther, .later], server: server, subscription: subscription)
        }
    }

    /// Подписка не обновилась по расписанию — прошлый список оставлен.
    public static func forStale(_ subscription: String, _ failure: FailureReason) -> Notice {
        Notice(.serversNotUpdated, "notify.stale.text", [.openSubscriptions], subscription: subscription, failure: failure)
    }

    /// Сообщение обновления подписки — уведомлением.
    public static func forSubscription(_ notice: SubscriptionNotice) -> Notice? {
        switch notice {
        case let .gone(server, subscription): return forSelected(.missing, server: server, subscription: subscription)
        case let .changed(server, subscription): return forSelected(.changed, server: server, subscription: subscription)
        case let .stale(subscription, reason): return forStale(subscription, reason)
        }
    }

    /// Пускает ли уровень из настроек уведомление.
    public static func allowed(_ level: NotificationLevel, _ notice: Notice) -> Bool {
        switch level {
        case .all: return true
        case .importantOnly: return notice.important
        case .off: return false
        }
    }
}

/// Повтор того же события — не чаще раза в 10 минут, как у десктопа и Android: событие узнается по виду и подписке, сервер не
/// в счет. Отмечается только показанное. Время — мс по одним часам. Не потокобезопасен.
public final class NoticeThrottle {
    public static let repeatAfter = 10 * 60 * 1000

    private var shown: [String: Int] = [:]

    public init() {}

    /// Можно показать: того же события не было последние 10 минут.
    public func allows(_ notice: Notice, now: Int) -> Bool {
        shown[key(notice)].map { now - $0 >= Self.repeatAfter } ?? true
    }

    /// Уведомление показано в `now`.
    public func shown(_ notice: Notice, now: Int) {
        shown[key(notice)] = now
    }

    private func key(_ notice: Notice) -> String {
        notice.kind.rawValue + "|" + (notice.subscription ?? "")
    }
}

/// Что сделать с уведомлением: `show` — показать (nil — нет); `withdrawProtection` — убрать уведомление о защите, которое
/// висит: беда прошла или сменилась другой.
public struct Delivery: Equatable, Sendable {
    public let show: Notice?
    public let withdrawProtection: Bool
}

/// Правила доски 22 — `NoticeDesk` Android: повтор того же события — не чаще раза в 10 минут; окно на экране — уведомлений
/// нет (окно показывает то же само), такое событие считается показанным; «Подключено» и «VPN отключен» — только при скрытом
/// окне; остальное решает уровень. Уведомление о защите одно: новое о другом заменяет его, не показанное новое убирает
/// прежнее — «Соединение потеряно» не висит, когда защита вернулась. Не потокобезопасен.
public final class NoticeDesk {
    private let throttle: NoticeThrottle
    // Какое уведомление о защите сейчас показано.
    private var protectionShown: NoticeKind?

    public init(throttle: NoticeThrottle = NoticeThrottle()) {
        self.throttle = throttle
    }

    /// - Parameters:
    ///   - now: время, мс по одним часам.
    ///   - windowVisible: окно приложения на экране.
    public func deliver(_ notice: Notice, now: Int, windowVisible: Bool, level: NotificationLevel) -> Delivery {
        let state = notice.kind == .connected || notice.kind == .disconnected
        if !throttle.allows(notice, now: now) {
            return Delivery(show: nil, withdrawProtection: withdraw(notice))
        }

        if !state && windowVisible {
            throttle.shown(notice, now: now)
            return Delivery(show: nil, withdrawProtection: withdraw(notice))
        }

        if (state && windowVisible) || !NotificationPolicy.allowed(level, notice) {
            return Delivery(show: nil, withdrawProtection: withdraw(notice))
        }

        throttle.shown(notice, now: now)
        if notice.tag == Notice.protectionTag {
            protectionShown = notice.kind
        }

        return Delivery(show: notice, withdrawProtection: false)
    }

    /// Уведомление о защите нажали или смахнули — его больше нет.
    public func protectionGone() {
        protectionShown = nil
    }

    private func withdraw(_ notice: Notice) -> Bool {
        if notice.tag == Notice.protectionTag, let shown = protectionShown, shown != notice.kind {
            protectionShown = nil
            return true
        }

        return false
    }
}

import Foundation

/// Участник сайта, как его возвращает API приложения: почта и имя для карточки «Аккаунт» в «Настройках».
public struct AccountUser: Equatable, Codable, Sendable {
    public let email: String
    public let displayName: String

    public init(email: String, displayName: String) {
        self.email = email
        self.displayName = displayName
    }
}

/// Устройство в аккаунте сайта: номер на сайте и имя, под которым его видит администратор.
public struct AccountDevice: Equatable, Codable, Sendable {
    public let id: Int64
    public let name: String

    public init(id: Int64, name: String) {
        self.id = id
        self.name = name
    }
}

/// Вход на сайт: токен устройства (секрет — только в Связке ключей), срок, участник и устройство — `AccountSession` Android.
/// В описании токена нет: запись не должна попасть в журнал или отчет даже случайно.
public struct AccountSession: Equatable, Sendable, CustomStringConvertible {
    public let token: String
    public let expiresAt: Date
    public let user: AccountUser
    public let device: AccountDevice

    public init(token: String, expiresAt: Date, user: AccountUser, device: AccountDevice) {
        self.token = token
        self.expiresAt = expiresAt
        self.user = user
        self.device = device
    }

    public var description: String { "AccountSession(expiresAt: \(expiresAt), device: \(device.id))" }
}

/// Устройство при входе: `uid` — случайный идентификатор установки, имя для администратора, платформа «ios» и версия
/// приложения. Ограничения — как у сайта (AppDeviceIn).
public struct DeviceInfo: Equatable, Sendable {
    public static let platform = "ios"
    /// Длина имени устройства у сайта.
    public static let nameMax = 100

    public let uid: String
    public let name: String
    public let appVersion: String

    public init(uid: String, name: String, appVersion: String) {
        self.uid = uid
        self.name = String(name.prefix(Self.nameMax))
        self.appVersion = appVersion
    }

    /// Имя iPhone для сайта: модель и версия системы («iPhone · iOS 17.5»), управляющие знаки и лишние пробелы убраны, не
    /// длиннее `nameMax`; пусто — «iPhone». Имя, которое владелец дал телефону, iOS приложениям не открывает.
    public static func phoneName(model: String?, system: String?) -> String {
        let parts = [model, system].map { clean($0) }.filter { !$0.isEmpty }
        let name = String(parts.joined(separator: " · ").prefix(nameMax)).trimmingCharacters(in: .whitespaces)
        return name.isEmpty ? "iPhone" : name
    }

    private static func clean(_ text: String?) -> String {
        let visible = (text ?? "").unicodeScalars.filter { !CharacterSet.controlCharacters.contains($0) }
        return String(String.UnicodeScalarView(visible)).split(separator: " ").joined(separator: " ")
    }

    /// Подходит ли `uid` сайту: 16–64 знака из латинских букв, цифр, «_» и «-» — UUID подходит.
    public static func validUid(_ uid: String) -> Bool {
        (16 ... 64).contains(uid.count) && uid.allSatisfy { $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "_" || $0 == "-") }
    }
}

/// Почему вход не выполнен — текст account.error.<…>, как у Android и десктопа.
public enum AccountError: String, CaseIterable, Sendable {
    /// 401: неверная почта или пароль; так же — 401 с незнакомым кодом сайта.
    case invalid
    /// 400: код 2FA или резервный код неверный или уже использован.
    case code
    /// 403: у администратора двухфакторная защита на сайте еще не настроена — предложить открыть сайт.
    case setupRequired
    /// 409: устройств у участника уже столько, сколько разрешает сайт, — страница устройств.
    case deviceLimit
    /// 409 device_change_limit: за 30 дней добавлено больше трех новых устройств — страница устройств.
    case deviceChangeLimit
    /// 429: слишком много попыток; минуты — в `LoginResult.failed`.
    case rateLimited
    /// 422: сайт не принял поля.
    case validation
    /// Нет связи с сайтом: сеть, DNS, TLS, таймаут.
    case network
    /// Сайт ответил ошибкой (5xx) или непонятным ответом.
    case server
    /// Связка ключей недоступна: токен без защиты не держим, вход не выполняется.
    case store

    /// Текст ошибки — тот же ключ, что у Android и десктопа.
    public var textKey: String {
        switch self {
        case .deviceChangeLimit: return "account.error.deviceChanges"
        default: return "account.error." + rawValue
        }
    }
}

/// Итог входа.
public enum LoginResult: Equatable, Sendable {
    case ok(AccountSession)
    /// 401 code_required: почта и пароль верны, но аккаунт администратора — нужен код 2FA или резервный код.
    case codeRequired
    /// Вход не выполнен; минуты — только у `rateLimited`, через сколько повторить (не меньше 1).
    case failed(AccountError, retryMinutes: Int = 0)
}

/// Итог проверки доступа (GET /api/app/status).
public enum StatusResult: Equatable, Sendable {
    /// Доступ есть; сайт продлил срок.
    case ok(expiresAt: Date, user: AccountUser, device: AccountDevice)
    /// 401 — и только он: устройство удалено, участник заблокирован или сменил пароль, срок истек.
    case revoked
    /// Сайт не ответил понятно: сеть, таймаут, 5xx, мусор. Это не отзыв — действует последний подтвержденный срок.
    case unreachable
}

/// Ответы API приложения на сайте (/api/app/…) — `AccountApi` Android без сети: раскладка кодов и тел ответа. Тела ошибок
/// наружу не идут: из тела ошибки входа читается только код сайта у 401 и 409 ({"error":{"code":"…"}}).
public enum AccountAnswers {
    /// Сайт Private Gateway Web: адрес постоянный, в настройках не меняется (тот же, что `AlwaysAllowed.site`).
    public static let site = URL(string: "https://" + AlwaysAllowed.site)!
    public static let loginPath = "/api/app/login"
    public static let statusPath = "/api/app/status"
    public static let logoutPath = "/api/app/logout"
    /// Страницы сайта для кнопок ошибок входа: настроить 2FA (403) и освободить место (409).
    public static let loginPage = "/login"
    public static let devicesPage = "/devices"
    /// Ответ API — несколько сотен байт; 64 КБ хватает с запасом.
    public static let maxBytes = 64 * 1024

    /// Тело запроса входа: почта без пробелов по краям, пароль, не больше одного кода и сведения об устройстве.
    public static func loginBody(email: String, password: String, code: String?, recovery: String?, device: DeviceInfo) -> Data {
        var fields: [String: Any] = ["email": email.trimmingCharacters(in: .whitespacesAndNewlines), "password": password]
        if let code {
            fields["code"] = code.trimmingCharacters(in: .whitespacesAndNewlines)
        }

        if let recovery {
            fields["recovery_code"] = recovery.trimmingCharacters(in: .whitespacesAndNewlines)
        }

        fields["device"] = ["uid": device.uid, "name": device.name, "platform": DeviceInfo.platform, "app_version": device.appVersion]
        return (try? JSONSerialization.data(withJSONObject: fields, options: [.sortedKeys])) ?? Data()
    }

    /// Итог входа по коду ответа, телу (только у 200), коду ошибки сайта (у 401 и 409) и Retry-After (секунды, у 429).
    public static func login(status: Int, body: Data?, errorCode: String?, retryAfter: String?) -> LoginResult {
        switch status {
        case 200: return body.flatMap(session).map(LoginResult.ok) ?? .failed(.server)
        // Незнакомый код сайта — безопасный запасной вариант: 401 — «неверная почта или пароль», 409 — «лимит устройств».
        case 401: return errorCode == "code_required" ? .codeRequired : .failed(.invalid)
        case 400: return .failed(.code)
        case 403: return .failed(.setupRequired)
        case 409: return .failed(errorCode == "device_change_limit" ? .deviceChangeLimit : .deviceLimit)
        case 429: return .failed(.rateLimited, retryMinutes: retryMinutes(retryAfter))
        case 422: return .failed(.validation)
        default: return .failed(.server)
        }
    }

    /// Итог проверки: 200 с понятным телом — доступ; только 401 — отзыв; остальное — сайт недоступен.
    public static func status(status: Int, body: Data?) -> StatusResult {
        switch status {
        case 200: return body.flatMap(parseStatus) ?? .unreachable
        case 401: return .revoked
        default: return .unreachable
        }
    }

    /// Код ошибки сайта из тела {"error":{"code":"…"}}; иначе nil.
    public static func errorCode(_ body: Data?) -> String? {
        guard let body, body.count <= maxBytes, let root = object(body), let error = root["error"] as? [String: Any] else {
            return nil
        }

        return error["code"] as? String
    }

    /// Минуты из Retry-After (секунды): вверх, не меньше 1; нет или не число — 1.
    public static func retryMinutes(_ header: String?) -> Int {
        guard let seconds = header.flatMap({ Int64($0.trimmingCharacters(in: .whitespaces)) }), seconds > 0 else {
            return 1
        }

        return Int(min(Int64(Int.max), max(1, (seconds + 59) / 60)))
    }

    /// Время ISO-8601 из ответа сайта: со смещением («Z», «+00:00») или без него — тогда UTC; доли секунды — любой длины (сайт
    /// пишет шесть знаков, а ISO8601DateFormatter понимает не больше трех), поэтому они разбираются отдельно.
    public static func date(_ text: String) -> Date? {
        var text = text.trimmingCharacters(in: .whitespaces)
        var fraction = 0.0
        if let tee = text.firstIndex(of: "T"), let dot = text[tee...].firstIndex(of: ".") {
            let start = text.index(after: dot)
            let digits = text[start...].prefix { $0.isASCII && $0.isNumber }
            guard !digits.isEmpty, let value = Double("0." + digits) else {
                return nil
            }

            fraction = value
            text.removeSubrange(dot ..< text.index(start, offsetBy: digits.count))
        }

        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime]
        guard let date = formatter.date(from: text) ?? formatter.date(from: text + "Z") else {
            return nil
        }

        return date.addingTimeInterval(fraction)
    }

    /// Ответ входа: без токена, срока, участника или устройства — не вход.
    static func session(_ body: Data) -> AccountSession? {
        guard let root = object(body), let token = root["token"] as? String, !token.isEmpty,
              let expires = (root["expires_at"] as? String).flatMap(date),
              let user = user(root["user"]), let device = device(root["device"])
        else {
            return nil
        }

        return AccountSession(token: token, expiresAt: expires, user: user, device: device)
    }

    static func parseStatus(_ body: Data) -> StatusResult? {
        guard let root = object(body), root["status"] as? String == "ok", let expires = (root["expires_at"] as? String).flatMap(date),
              let user = user(root["user"]), let device = device(root["device"])
        else {
            return nil
        }

        return .ok(expiresAt: expires, user: user, device: device)
    }

    private static func object(_ body: Data) -> [String: Any]? {
        guard body.count <= maxBytes else { return nil }
        return (try? JSONSerialization.jsonObject(with: body)) as? [String: Any]
    }

    private static func user(_ node: Any?) -> AccountUser? {
        guard let node = node as? [String: Any], let email = node["email"] as? String else { return nil }
        return AccountUser(email: email, displayName: node["display_name"] as? String ?? "")
    }

    private static func device(_ node: Any?) -> AccountDevice? {
        guard let node = node as? [String: Any], let id = (node["id"] as? NSNumber)?.int64Value else { return nil }
        return AccountDevice(id: id, name: node["name"] as? String ?? "")
    }
}

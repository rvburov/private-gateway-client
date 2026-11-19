import Foundation

/// Что делать со входом после проверки доступа.
public enum AccessVerdict: Equatable, Sendable {
    /// Доступ есть до `expiresAt`: сайт подтвердил (`confirmed`) или не ответил, а срок еще идет.
    case keep(expiresAt: Date, confirmed: Bool)
    /// Сайт ответил 401: стереть вход, выключить защиту, экран входа с «Доступ отозван».
    case revoked
    /// Сайт не ответил, а подтвержденный срок прошел: выход как при истечении («Срок входа истек»), защита выключается.
    case expired
}

/// Правило доступа (одинаково с десктопом и Android, решение 618): решает сайт. Отзыв — только ответ 401; сеть, таймаут и 5xx —
/// не отзыв: действует последний подтвержденный срок, иначе телефон без сети остался бы без защиты из-за сбоя сайта. Срок
/// прошел без подтверждения — выход.
public enum AccountGate {
    /// Проверка, пока приложение на экране: раз в 30 минут (и при запуске, и при возврате в приложение).
    public static let checkEvery: TimeInterval = 30 * 60

    /// Возврат в приложение чаще этого не проверяет снова: запуск и показ окна идут подряд.
    public static let recheckAfter: TimeInterval = 60

    /// Сколько ждать ответа сайта на выход, пока защита еще идет: дольше выход туннель не держит (как у Android).
    public static let logoutWait: TimeInterval = 3

    /// Можно ли подключаться: вход есть и его срок не прошел.
    public static func allowed(_ expiresAt: Date?, now: Date) -> Bool {
        expiresAt.map { now < $0 } ?? false
    }

    /// Итог проверки `status` для входа со сроком `expiresAt`.
    public static func verdict(_ expiresAt: Date, _ status: StatusResult, now: Date) -> AccessVerdict {
        switch status {
        case let .ok(expires, _, _): return .keep(expiresAt: expires, confirmed: true)
        case .revoked: return .revoked
        case .unreachable: return allowed(expiresAt, now: now) ? .keep(expiresAt: expiresAt, confirmed: false) : .expired
        }
    }
}

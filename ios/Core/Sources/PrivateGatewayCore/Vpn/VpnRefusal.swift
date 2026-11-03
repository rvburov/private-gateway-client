import Foundation

/// «Запретить» в системном запросе VPN (DECISIONS.md, п. 5): iOS не дала сохранить профиль — это не ошибка и не «Работает
/// другой VPN», а «Выключено» с подсказкой «iOS не дала разрешения на VPN — защита не включилась», как на Android. Следующее
/// нажатие щита спрашивает снова: iOS показывает запрос при каждом сохранении профиля, пока его не разрешили.
public struct VpnPermissionDenied: Error, Equatable, Sendable {
    public init() {}
}

public enum VpnRefusal {
    /// Домен ошибок NetworkExtension (`NEVPNErrorDomain`) — строкой, чтобы правило проверялось и на Linux.
    public static let domain = "NEVPNErrorDomain"

    /// `NEVPNError.Code.configurationReadWriteFailed` («permission denied»): iOS не дала записать профиль VPN.
    public static let permissionDeniedCode = 5

    /// Отказ сохранить профиль — это «Запретить» пользователя.
    public static func permissionDenied(_ error: any Error) -> Bool {
        let error = error as NSError
        return error.domain == domain && error.code == permissionDeniedCode
    }
}

import Foundation

/// Состояние VPN в системе — `NEVPNStatus` без NetworkExtension, чтобы правило проверялось и на Linux.
public enum SystemVpnStatus: Sendable {
    case invalid
    case disconnected
    case connecting
    case connected
    case reasserting
    case disconnecting
}

/// Что показать в окне: само состояние — от системы (оно верное, даже если расширение упало и не дописало файл), сервер,
/// время подключения, номер попытки и код ошибки — из отчета расширения (`TunnelStatusFile`). Отказ самого приложения (iOS
/// не дала сохранить профиль VPN — разрешение не дано или VPN занят) — `appError`, пока туннель не поднимается.
public enum TunnelView {
    public static func status(system: SystemVpnStatus, report: TunnelStatus?, appError: String? = nil) -> TunnelStatus {
        let serverId = report?.serverId
        switch system {
        case .connecting:
            return TunnelStatus(state: .connecting, serverId: serverId)
        case .connected:
            // Время — только из отчета о подключении: отчет прежнего состояния его не знает.
            return TunnelStatus(state: .connected, serverId: serverId, since: report?.state == .connected ? report?.since : nil)
        case .reasserting:
            return TunnelStatus(state: .reconnecting, serverId: serverId, attempt: report?.state == .reconnecting ? report?.attempt ?? 1 : 1)
        case .disconnecting:
            return TunnelStatus(state: .stopping, serverId: serverId)
        case .disconnected, .invalid:
            if let appError {
                return TunnelStatus(state: .error, error: appError)
            }

            if report?.state == .error, let error = report?.error {
                return TunnelStatus(state: .error, error: error)
            }

            return TunnelStatus()
        }
    }
}

/// Профиль VPN в настройках iOS (`NETunnelProviderProtocol`), его пишет приложение. Блок при обрыве — системный «весь трафик
/// только через VPN» (`includeAllNetworks`): пока туннель поднят или поднимается, мимо него ничего не уходит. Локальная сеть:
/// разрешена — мимо туннеля (`excludeLocalNetworks`), иначе — в туннель, а там ядро ее блокирует; без блока при обрыве iOS
/// пустила бы свою подсеть мимо туннеля — маршруты туннеля тогда главнее (`enforceRoutes`), и `TunPlan` добавляет частные
/// диапазоны (решение 460).
public struct VpnProfile: Equatable, Sendable {
    /// Расширение туннеля (`ios/project.yml`).
    public static let providerBundleIdentifier = "com.privategateway.client.tunnel"

    /// Что iOS показывает в поле «Сервер» профиля — не адрес сервера: он запечатан в Связке ключей.
    public static let serverAddress = "Private Gateway"

    public let includeAllNetworks: Bool
    public let excludeLocalNetworks: Bool
    public let enforceRoutes: Bool

    public init(includeAllNetworks: Bool, excludeLocalNetworks: Bool, enforceRoutes: Bool) {
        self.includeAllNetworks = includeAllNetworks
        self.excludeLocalNetworks = excludeLocalNetworks
        self.enforceRoutes = enforceRoutes
    }

    public static func of(_ settings: AppSettings) -> VpnProfile {
        VpnProfile(
            includeAllNetworks: settings.killSwitch, excludeLocalNetworks: settings.tunnel.allowLan,
            enforceRoutes: !settings.killSwitch && !settings.tunnel.allowLan
        )
    }
}

/// Сообщения приложения расширению туннеля (`sendProviderMessage`) и ответы.
public enum TunnelMessages {
    /// Перечитать выбранный сервер и настройки и перезапустить ядро, не опуская туннель: маршруты остаются, и пока ядро
    /// поднимается заново, трафик ждет в туннеле, а не уходит мимо — как переподключение службы Android (решение 410).
    public static let reload = Data("reload".utf8)

    public static let ok = Data("ok".utf8)

    /// Ответ расширения: nil — перезапуск удался, иначе код ошибки (PGC-…).
    public static func failure(_ response: Data?) -> String? {
        guard let response, response != ok else {
            return response == nil ? TunnelErrors.coreFailed : nil
        }

        return String(decoding: response, as: UTF8.self)
    }
}

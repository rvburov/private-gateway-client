import Foundation

/// Какой выход запомнить для карточки сервера (решение 583, `ExitMemory` десктопа): IP, который сайты видели через этот
/// сервер. У сервера «VPS + прокси» он не совпадает с адресом подключения. Только «Подключено» без паузы для входа в Wi-Fi,
/// замер этого подключения и не реальный IP телефона (утечка): иначе выходом сервера записался бы чужой адрес.
public enum ServerExit {
    /// Сервер и его выход, которые можно запомнить; иначе nil.
    public static func remembered(state: TunnelState, serverId: String?, exit: String?, direct: String?, paused: Bool) -> (id: String, ip: String)? {
        guard state == .connected, !paused, let serverId, !serverId.isEmpty, let exit, !exit.isEmpty, exit != direct else {
            return nil
        }

        return (serverId, exit)
    }
}

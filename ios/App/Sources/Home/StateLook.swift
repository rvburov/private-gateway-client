import PrivateGatewayCore
import PrivateGatewayTheme
import SwiftUI

/// Вид состояния защиты — цвета state.* макета: зеленый — защищено, синий — подключение, красный — выключено или ошибка.
/// Состояние всегда показано иконкой и текстом, не только цветом (как `StateLook` Android).
struct StateLook: Equatable {
    let ring: PgRGBA
    let icon: PgRGBA
    let glow: PgRGBA
    let tint: PgRGBA
    let mark: String
    let power: String
    let title: String

    static func of(_ state: TunnelState, _ texts: PgTexts) -> StateLook {
        switch state {
        case .connected:
            return StateLook(
                ring: PgState.Connected.ring, icon: PgState.Connected.icon, glow: PgState.Connected.glow, tint: PgColor.Status.Success.tint,
                mark: PgIcons.markOk, power: texts.powerLabelConnected, title: texts.statusConnectedTitle
            )
        case .connecting, .stopping:
            return StateLook(
                ring: PgState.Connecting.ring, icon: PgState.Connecting.icon, glow: PgState.Connecting.glow, tint: PgColor.Accent.tint,
                mark: PgIcons.markWait, power: texts.powerLabelConnecting, title: texts.statusConnectingTitle
            )
        case .reconnecting:
            // Переподключение — вид подключения, как у десктопа: туннель держит трафик, пока идут попытки.
            return StateLook(
                ring: PgState.Connecting.ring, icon: PgState.Connecting.icon, glow: PgState.Connecting.glow, tint: PgColor.Accent.tint,
                mark: PgIcons.markWait, power: texts.powerLabelReconnecting, title: texts.statusReconnectingTitle
            )
        case .error:
            return StateLook(
                ring: PgState.Error.ring, icon: PgState.Error.icon, glow: PgState.Error.glow, tint: PgColor.Status.Danger.tint,
                mark: PgIcons.markAlert, power: texts.powerLabelError, title: texts.statusErrorTitle
            )
        case .off:
            return StateLook(
                ring: PgState.Off.ring, icon: PgState.Off.icon, glow: PgState.Off.glow, tint: PgColor.Status.Danger.tint,
                mark: PgIcons.markOff, power: texts.powerLabelOff, title: texts.statusOffTitle
            )
        }
    }
}

/// Вид чипа проверки: работает, ждет, выключено, предупреждение.
enum ChipLook: Equatable {
    case ok
    case waiting
    case off
    case warning

    var tint: PgRGBA {
        switch self {
        case .ok: return PgColor.Status.Success.tint
        case .waiting: return PgColor.Accent.tint
        case .off: return PgColor.Status.Danger.tint
        case .warning: return PgColor.Status.Caution.tint
        }
    }

    var base: PgRGBA {
        switch self {
        case .ok: return PgColor.Status.Success.base
        case .waiting: return PgColor.Accent.base
        case .off: return PgColor.Status.Danger.base
        case .warning: return PgColor.Status.Caution.base
        }
    }

    var mark: String {
        switch self {
        case .ok: return PgIcons.chipOk
        case .waiting: return PgIcons.chipDots
        case .off: return PgIcons.chipDash
        case .warning: return PgIcons.chipWarn
        }
    }
}

/// Чип защиты на главной и строка на «Проверке»: заголовок, значение, вид и объяснение.
struct ProtectionChip: Equatable {
    let title: String
    let value: String
    let look: ChipLook
    let text: String
}

/// Четыре чипа главной (доска 25): весь трафик, DNS, IPv6, блок при обрыве — по состоянию туннеля. Блок при обрыве на
/// iOS — «весь трафик только через VPN» самой системы (includeAllNetworks): пока туннель поднят или поднимается, трафик
/// мимо него не уходит; выключен туннель — блока нет.
enum ProtectionChips {
    static func of(_ state: TunnelState, killSwitch: Bool, _ texts: PgTexts) -> [ProtectionChip] {
        let connected = state == .connected
        // Подключение и переподключение: маршруты туннеля стоят, трафик ждет — «поднимается», а не «идет напрямую».
        let waiting = state == .connecting || state == .reconnecting || state == .stopping
        func pick(_ ok: String, _ wait: String, _ off: String) -> (String, ChipLook) {
            if connected {
                return (ok, .ok)
            }

            return waiting ? (wait, .waiting) : (off, .off)
        }

        let all = pick(texts.checkAllOk, texts.checkAllWaiting, texts.checkAllOff)
        let dns = pick(texts.checkDnsOk, texts.checkDnsWaiting, texts.checkDnsOff)
        let ipv6 = pick(texts.checkIpv6Ok, texts.checkIpv6Waiting, texts.checkIpv6Off)
        let ks: (String, ChipLook)
        if killSwitch, connected {
            ks = (texts.checkKsOk, .ok)
        } else if killSwitch, waiting {
            ks = (texts.checkKsWaiting, .ok)
        } else {
            ks = (texts.checkKsOff, .off)
        }

        return [
            ProtectionChip(title: texts.checkAllTitle, value: all.0, look: all.1, text: texts.checkAllText),
            ProtectionChip(title: texts.checkDnsTitle, value: dns.0, look: dns.1, text: texts.checkDnsText),
            ProtectionChip(title: texts.checkIpv6Title, value: ipv6.0, look: ipv6.1, text: texts.checkIpv6Text),
            ProtectionChip(title: texts.checkKsTitle, value: ks.0, look: ks.1, text: texts.checkKsText),
        ]
    }
}

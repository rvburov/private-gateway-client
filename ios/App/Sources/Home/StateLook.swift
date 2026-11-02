import PrivateGatewayCore
import PrivateGatewayTheme
import SwiftUI

/// Вид состояния защиты — цвета state.* макета: зеленый — защищено, синий — подключение, оранжевый — блокировка, красный —
/// выключено, ошибка или утечка. Состояние всегда показано иконкой и текстом, не только цветом (как `StateLook` Android).
struct StateLook: Equatable {
    let ring: PgRGBA
    let icon: PgRGBA
    let glow: PgRGBA
    /// Заливка круга щита при нажатии (state.*.fill).
    let fill: PgRGBA
    let tint: PgRGBA
    let mark: String
    let power: String
    let title: String

    /// - Parameters:
    ///   - blocked: «Ошибка», а туннель оставлен и держит трафик закрытым — «Блокировка» (`AppModel.blocked`).
    ///   - leak: «Защищено», а замер через туннель увидел реальный IP — «Утечка IP» (`HomeFacts.leak`): тон «Ошибки», своя
    ///     подпись под щитом.
    ///   - paused: «Выключено» по паузе для входа в Wi-Fi (решение 557) — янтарный тон, «Защита на паузе», подпись «Пауза».
    static func of(_ state: TunnelState, _ texts: PgTexts, blocked: Bool = false, leak: Bool = false, paused: Bool = false) -> StateLook {
        if state == .off, paused {
            return StateLook(
                ring: PgState.Blocked.ring, icon: PgState.Blocked.icon, glow: PgState.Blocked.glow, fill: PgState.Blocked.fill,
                tint: PgColor.Status.Caution.tint, mark: PgIcons.markAlert, power: texts.powerLabelPaused, title: texts.statusPausedTitle
            )
        }

        if state == .error, blocked {
            return StateLook(
                ring: PgState.Blocked.ring, icon: PgState.Blocked.icon, glow: PgState.Blocked.glow, fill: PgState.Blocked.fill,
                tint: PgColor.Status.Warning.tint, mark: PgIcons.markAlert, power: texts.powerLabelBlocked, title: texts.statusBlockedTitle
            )
        }

        if state == .connected, leak {
            return StateLook(
                ring: PgState.Error.ring, icon: PgState.Error.icon, glow: PgState.Error.glow, fill: PgState.Error.fill,
                tint: PgColor.Status.Danger.tint, mark: PgIcons.markAlert, power: texts.powerLabelLeak, title: texts.statusLeakTitle
            )
        }

        switch state {
        case .connected:
            return StateLook(
                ring: PgState.Connected.ring, icon: PgState.Connected.icon, glow: PgState.Connected.glow, fill: PgState.Connected.fill,
                tint: PgColor.Status.Success.tint, mark: PgIcons.markOk, power: texts.powerLabelConnected, title: texts.statusConnectedTitle
            )
        case .connecting, .stopping:
            return StateLook(
                ring: PgState.Connecting.ring, icon: PgState.Connecting.icon, glow: PgState.Connecting.glow, fill: PgState.Connecting.fill,
                tint: PgColor.Accent.tint, mark: PgIcons.markWait, power: texts.powerLabelConnecting, title: texts.statusConnectingTitle
            )
        case .reconnecting:
            // Переподключение — вид подключения, как у десктопа: туннель держит трафик, пока идут попытки.
            return StateLook(
                ring: PgState.Connecting.ring, icon: PgState.Connecting.icon, glow: PgState.Connecting.glow, fill: PgState.Connecting.fill,
                tint: PgColor.Accent.tint, mark: PgIcons.markWait, power: texts.powerLabelReconnecting, title: texts.statusReconnectingTitle
            )
        case .error:
            return StateLook(
                ring: PgState.Error.ring, icon: PgState.Error.icon, glow: PgState.Error.glow, fill: PgState.Error.fill,
                tint: PgColor.Status.Danger.tint, mark: PgIcons.markAlert, power: texts.powerLabelError, title: texts.statusErrorTitle
            )
        case .off:
            return StateLook(
                ring: PgState.Off.ring, icon: PgState.Off.icon, glow: PgState.Off.glow, fill: PgState.Off.fill,
                tint: PgColor.Status.Danger.tint, mark: PgIcons.markOff, power: texts.powerLabelOff, title: texts.statusOffTitle
            )
        }
    }
}

/// Вид плитки проверки — CheckState десктопа: работает (зеленый), ждет (синий), выключено (красный), частично (янтарный),
/// остановлено блоком — утечки нет (оранжевый). Цвета — CheckBrushConverter: подложка, подложка выбранной, кружок и рамка,
/// подпись значения.
enum ChipLook: Equatable {
    case ok
    case waiting
    case off
    case warning
    case stopped

    var tint: PgRGBA {
        switch self {
        case .ok: return PgColor.Status.Success.tint
        case .waiting: return PgColor.Accent.tint
        case .off: return PgColor.Status.Danger.tint
        case .warning: return PgColor.Status.Caution.tint
        case .stopped: return PgColor.Status.Warning.tint
        }
    }

    /// Подложка выбранной плитки (.btn-chip.selected).
    var hover: PgRGBA {
        switch self {
        case .ok: return PgColor.Status.Success.hover
        case .waiting: return PgColor.Accent.hover
        case .off: return PgColor.Status.Danger.hover
        case .warning: return PgColor.Status.Caution.hover
        case .stopped: return PgColor.Status.Warning.hover
        }
    }

    /// Кружок с отметкой и рамка выбранной плитки.
    var base: PgRGBA {
        switch self {
        case .ok: return PgColor.Status.Success.base
        case .waiting: return PgColor.Accent.base
        case .off: return PgColor.Status.Danger.base
        case .warning: return PgColor.Status.Caution.base
        case .stopped: return PgColor.Status.Warning.base
        }
    }

    /// Подпись значения («через туннель», «остановлен») — цветом тона (.chip-value).
    var text: PgRGBA {
        switch self {
        case .ok: return PgColor.Status.Success.text
        case .waiting: return PgColor.Accent.text
        case .off: return PgColor.Status.Danger.text
        case .warning: return PgColor.Status.Caution.text
        case .stopped: return PgColor.Status.Warning.text
        }
    }

    var mark: String {
        switch self {
        case .ok: return PgIcons.chipOk
        case .waiting: return PgIcons.chipDots
        case .off, .stopped: return PgIcons.chipDash
        case .warning: return PgIcons.chipWarn
        }
    }
}

/// Чип защиты на главной: заголовок, значение, вид и объяснение. У «Проверки» свои строки — `CheckRows` (тексты diag.*).
struct ProtectionChip: Equatable {
    let title: String
    let value: String
    let look: ChipLook
    let text: String
}

/// Четыре чипа главной (доски 09 и 25): весь трафик, DNS, IPv6, блок при обрыве — по состоянию туннеля, как
/// `ProtectionChecks.For` десктопа. Блок при обрыве на iOS — «весь трафик только через VPN» самой системы
/// (includeAllNetworks): пока туннель поднят или поднимается, трафик мимо него не уходит; выключен туннель — блока нет.
enum ProtectionChips {
    /// - Parameters:
    ///   - blocked: «Блокировка» — туннель оставлен без ядра, трафик остановлен, утечки нет.
    ///   - leak: «Утечка IP» — замер через туннель увидел реальный IP.
    static func of(_ state: TunnelState, killSwitch: Bool, _ texts: PgTexts, blocked: Bool = false, leak: Bool = false) -> [ProtectionChip] {
        let all: (String, ChipLook, String)
        let dns: (String, ChipLook)
        let ipv6: (String, ChipLook)
        let ks: (String, ChipLook)
        let ksOn = (texts.checkKsOk, ChipLook.ok)
        let ksOff = (texts.checkKsOff, ChipLook.off)
        switch state {
        case .connected:
            all = leak ? (texts.checkAllSameIp, ChipLook.warning, texts.checkAllSameIpText) : (texts.checkAllOk, ChipLook.ok, texts.checkAllText)
            dns = (texts.checkDnsOk, .ok)
            ipv6 = (texts.checkIpv6Ok, .ok)
            ks = killSwitch ? ksOn : ksOff
        case .connecting, .reconnecting, .stopping:
            // Трафик ждет в туннеле: весь трафик «поднимается», а DNS и IPv6 уже не уходят мимо — как у десктопа.
            all = (texts.checkAllWaiting, .waiting, texts.checkAllText)
            dns = (texts.checkDnsOk, .ok)
            ipv6 = (texts.checkIpv6Ok, .ok)
            ks = killSwitch ? ksOn : ksOff
        case .error where blocked:
            // Туннель оставлен без ядра: трафик остановлен блоком, утечки нет.
            all = (texts.checkAllStopped, .stopped, texts.checkAllText)
            dns = (texts.checkDnsOk, .ok)
            ipv6 = (texts.checkIpv6Ok, .ok)
            ks = killSwitch ? ksOn : ksOff
        case .error, .off:
            // Туннеля нет — трафик идет напрямую, блока нет.
            all = (texts.checkAllOff, .off, texts.checkAllText)
            dns = (texts.checkDnsOff, .off)
            ipv6 = (texts.checkIpv6Off, .off)
            ks = ksOff
        }

        return [
            ProtectionChip(title: texts.checkAllTitle, value: all.0, look: all.1, text: all.2),
            ProtectionChip(title: texts.checkDnsTitle, value: dns.0, look: dns.1, text: texts.checkDnsText),
            ProtectionChip(title: texts.checkIpv6Title, value: ipv6.0, look: ipv6.1, text: texts.checkIpv6Text),
            ProtectionChip(title: texts.checkKsTitle, value: ks.0, look: ks.1, text: texts.checkKsText),
        ]
    }
}

/// Вопросы главной, как у десктопа (доска 18).
enum HomeDialogs {
    /// Щит в «Блокировке»: снять защиту — значит открыть сайтам реальный IP. «Да» — опасная вторая кнопка «Снять защиту».
    static func unprotect(_ texts: PgTexts) -> PgDialog {
        PgDialog(
            kind: .warning, icon: PgIcons.shield, detail: PgIcons.markAlert, title: texts.dialogUnprotectTitle, text: texts.dialogUnprotectText,
            primary: texts.dialogUnprotectKeep, secondary: texts.dialogUnprotectDrop, secondaryDanger: true
        )
    }

    /// «Вход в Wi-Fi»: открыть интернет без VPN на 5 минут (решение 557). «Да» — опасная вторая «Открыть на 5 минут».
    static func pause(_ texts: PgTexts) -> PgDialog {
        PgDialog(
            kind: .warning, icon: PgIcons.clock, title: texts.pauseDialogTitle, text: texts.pauseDialogText, primary: texts.pauseDialogKeep,
            secondary: texts.pauseDialogOpen, secondaryDanger: true
        )
    }
}

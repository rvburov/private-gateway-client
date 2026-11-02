import Foundation
import PrivateGatewayCore
import PrivateGatewayTheme

/// Проверка на «Проверке» — как DiagnosticCheck десктопа; ключи текстов — diag.<id>.*. Службы на телефоне нет: на ее месте —
/// «Туннель», состояние профиля VPN в системе (`NEVPNStatus`). «Защиты WSL2 и Hyper-V» тоже нет — она только у Windows.
enum CheckId: String, CaseIterable {
    case tunnel
    case core
    case allTraffic = "all_traffic"
    case killSwitch = "kill_switch"
    case dns
    case ipv6
    case exitIp = "exit_ip"
    case otherVpn = "other_vpn"
}

/// Итог проверки — CheckState десктопа: работает, ждет, остановлено блоком (утечки нет), выключено, нужно внимание.
enum CheckOutcome: String {
    case ok
    case waiting
    case stopped
    case off
    case warning

    /// Цвета и значок — те же, что у плиток главной.
    var look: ChipLook {
        switch self {
        case .ok: return .ok
        case .waiting: return .waiting
        case .stopped: return .stopped
        case .off: return .off
        case .warning: return .warning
        }
    }
}

/// Строка «Проверки» и блока «Что может мешать» (.check-row): кружок цвета итога, заголовок, пояснение и чип справа.
struct CheckRow: Equatable {
    /// Для VoiceOver и тестов: «tunnel», «exit_ip», «hinder.http».
    let key: String
    let title: String
    let detail: String
    let chip: String
    let outcome: CheckOutcome

    var look: ChipLook { outcome.look }
}

/// Что «Проверка» знает о защите и о сети: состояние туннеля и системы, «Блокировка», «Утечка IP», блок при обрыве, версия
/// ядра, внешний IP (замер инспектора), реальный IP этой сети, прокси и VPN других приложений, идет ли проверка.
struct CheckInput: Equatable {
    var state = TunnelState.off
    var system = SystemVpnStatus.invalid
    var error: String?
    var blocked = false
    var leak = false
    var killSwitch = true
    var core: String?
    var ip = PublicIp()
    var direct: String?
    var proxy = ProxyFindings()
    var running = false
}

/// Строки результатов (доска 7, Refresh и Detail десктопа): порядок — туннель, ядро, весь трафик, блок при обрыве, DNS, IPv6,
/// IP для сайтов, другие VPN и прокси. Пояснение — diag.<id>.<причина>, иначе diag.<id>.<итог>, иначе diag.state.<итог>.text;
/// чип — то же с «.chip». Пока идет проверка — все строки «Проверяется» и «…».
enum CheckRows {
    /// Чем iOS держит блок при обрыве — слово ОС макета (data-os-word="engine").
    static let engine = "includeAllNetworks"
    static let coreName = "Xray-core"
    /// Реальный IP вместо адреса, когда он не измерен.
    static let hidden = "***"

    static func of(_ input: CheckInput, _ texts: PgTexts) -> [CheckRow] {
        CheckId.allCases.map { row($0, input, texts) }
    }

    static func row(_ id: CheckId, _ input: CheckInput, _ texts: PgTexts) -> CheckRow {
        let title = first(texts, ["diag.\(id.rawValue).title"]) ?? texts.networkKindTunnel
        guard !input.running else {
            return CheckRow(key: id.rawValue, title: title, detail: texts.diagChecking, chip: texts.diagStateWaitingChip, outcome: .waiting)
        }

        let (outcome, reason) = result(id, input)
        return CheckRow(
            key: id.rawValue, title: title, detail: detail(id, outcome, reason, input, texts), chip: chip(id, outcome, reason, texts), outcome: outcome
        )
    }

    /// Итог и причина (код уточнения) проверки — правила службы десктопа (`ProtectionChecks.For`, проверки утечек) для
    /// телефона: «Ошибка» без туннеля — трафик идет напрямую; «Блокировка» — туннель оставлен без ядра, трафик остановлен.
    static func result(_ id: CheckId, _ input: CheckInput) -> (CheckOutcome, String?) {
        let phase = Phase.of(input)
        switch id {
        case .tunnel:
            // «Блокировка» — туннель поднят (iOS говорит «подключено» или «переподключается»), а трафик остановлен.
            if input.blocked {
                return (.stopped, nil)
            }

            switch input.system {
            case .connected: return (.ok, nil)
            case .connecting, .reasserting, .disconnecting: return (.waiting, nil)
            case .disconnected, .invalid: return (.off, nil)
            }
        case .core:
            switch phase {
            case .on: return (.ok, nil)
            case .waiting: return (.waiting, nil)
            case .blocked: return (.stopped, input.error == TunnelErrors.serverNotResponding ? "no_reply" : nil)
            case .failed: return (.stopped, nil)
            case .off: return (.off, nil)
            }
        case .allTraffic:
            switch phase {
            case .on:
                if input.leak {
                    return (.warning, "same_ip")
                }

                return (.ok, nil)
            case .waiting: return (.waiting, nil)
            case .blocked: return (.stopped, nil)
            case .failed, .off: return (.off, nil)
            }
        case .killSwitch:
            switch phase {
            case .on, .waiting: return (input.killSwitch ? CheckOutcome.ok : .off, nil)
            case .blocked: return (.ok, nil)
            case .failed, .off: return (.off, nil)
            }
        case .dns, .ipv6:
            // Под туннелем DNS идет в туннель, а IPv6 мимо него не уходит никогда (решение 459).
            switch phase {
            case .on, .waiting, .blocked: return (.ok, nil)
            case .failed, .off: return (.off, nil)
            }
        case .exitIp:
            switch phase {
            case .on:
                if input.leak {
                    return (.warning, "same_ip")
                }

                if input.ip.viaServer, input.ip.address != nil {
                    return (.ok, nil)
                }

                if input.ip.failed {
                    return (.warning, "no_answer")
                }

                return (.waiting, "measuring")
            case .waiting: return (.waiting, nil)
            case .blocked: return (.stopped, nil)
            case .failed, .off: return (.off, nil)
            }
        case .otherVpn:
            if input.proxy.proxy {
                return (.warning, "system_proxy")
            }

            // Пока свой туннель поднят, интерфейс VPN в системе — он сам: два VPN сразу iOS не держит.
            if phase == .failed || phase == .off, !input.proxy.vpn.isEmpty {
                return (.warning, "vpn_adapter")
            }

            return (.ok, nil)
        }
    }

    private static func detail(_ id: CheckId, _ outcome: CheckOutcome, _ reason: String?, _ input: CheckInput, _ texts: PgTexts) -> String {
        switch (id, outcome) {
        case (.core, .ok):
            return texts.diagCoreOk(input.core.map { coreName + " " + $0 } ?? coreName)
        case (.core, .stopped):
            // На iOS в «Блокировке» ядро остановлено (туннель оставлен без него), поэтому не «Запущено, но сервер не отвечает»;
            // код ошибки — здесь: карточки ошибки на «Проверке» нет (доска 7).
            let why = reason == "no_reply" ? texts.statusBlockedSub : input.error
            return [texts.diagCoreStopped, why].compactMap { $0 }.joined(separator: " · ")
        case (.killSwitch, .ok):
            return texts.diagKillSwitchOk(engine)
        case (.exitIp, .ok):
            let place = input.ip.info?.city
            return [input.ip.address, place].compactMap { $0 }.joined(separator: " · ")
        case (.exitIp, .off):
            return texts.diagExitIpOff(real(input) ?? hidden)
        case (.exitIp, .waiting) where reason == "measuring":
            return texts.diagChecking
        default:
            var keys: [String] = []
            if let reason {
                keys.append("diag.\(id.rawValue).\(reason)")
            }

            keys += ["diag.\(id.rawValue).\(outcome.rawValue)", "diag.state.\(outcome.rawValue).text"]
            return first(texts, keys) ?? ""
        }
    }

    private static func chip(_ id: CheckId, _ outcome: CheckOutcome, _ reason: String?, _ texts: PgTexts) -> String {
        var keys: [String] = []
        if let reason {
            keys.append("diag.\(id.rawValue).\(reason).chip")
        }

        keys += ["diag.\(id.rawValue).\(outcome.rawValue).chip", "diag.state.\(outcome.rawValue).chip"]
        return first(texts, keys) ?? ""
    }

    /// Реальный IP этой сети: замер без защиты (адрес инспектора не через сервер) или запомненный для главной.
    static func real(_ input: CheckInput) -> String? {
        input.ip.viaServer ? input.direct : input.ip.address ?? input.direct
    }

    /// Первый текст из словаря по ключам; ни одного — nil.
    static func first(_ texts: PgTexts, _ keys: [String]) -> String? {
        for key in keys {
            if let text = texts.text(key: key) {
                return text
            }
        }

        return nil
    }

    /// Состояние защиты для проверок.
    private enum Phase {
        case on
        case waiting
        case blocked
        case failed
        case off

        static func of(_ input: CheckInput) -> Phase {
            switch input.state {
            case .connected: return .on
            case .connecting, .reconnecting, .stopping: return .waiting
            case .error: return input.blocked ? .blocked : .failed
            case .off: return .off
            }
        }
    }
}

/// «Что на этом телефоне может мешать» (доска 7, Hindrances десктопа) — то, что видно на iPhone: прокси сети, скрипт
/// автонастройки и VPN другого приложения (пока свой туннель не поднят); ничего — «Ничего не найдено». «Исправить» на телефоне
/// нет: прокси задают в настройках Wi-Fi или профилем, приложение их не меняет.
enum HinderRows {
    static func of(_ proxy: ProxyFindings, state: TunnelState, blocked: Bool, _ texts: PgTexts) -> [CheckRow] {
        var rows: [CheckRow] = []
        if let http = proxy.http {
            rows.append(CheckRow(
                key: "hinder.http", title: texts.diagHinderSystem, detail: texts.diagHinderValue(http) + " " + texts.diagHinderHowSystem,
                chip: texts.diagHinderChipSet, outcome: .warning
            ))
        }

        if let pac = proxy.pac {
            rows.append(CheckRow(
                key: "hinder.pac", title: texts.diagHinderPac, detail: texts.diagHinderValue(pac) + " " + texts.diagHinderHowSystem,
                chip: texts.diagHinderChipSet, outcome: .warning
            ))
        }

        let tunnelUp = state != .off && (state != .error || blocked)
        if !tunnelUp, !proxy.vpn.isEmpty {
            rows.append(CheckRow(
                key: "hinder.vpn", title: texts.diagHinderOther, detail: texts.diagOtherVpnVpnAdapter + " · " + proxy.vpn.joined(separator: ", "),
                chip: texts.diagHinderChipLive, outcome: .warning
            ))
        }

        if rows.isEmpty {
            rows.append(CheckRow(key: "hinder.none", title: texts.diagHinderNone, detail: texts.diagHinderNoneText, chip: texts.diagHinderChipNone, outcome: .ok))
        }

        return rows
    }
}

/// Сеть iPhone словами — для карточки «Сеть iPhone» и отчета: вид, интерфейс и отметки.
enum PhoneNetworkText {
    static func kind(_ kind: PhoneNetworkKind, _ texts: PgTexts) -> String {
        switch kind {
        case .wifi: return texts.networkKindWifi
        case .cellular: return texts.networkKindCellular
        case .ethernet: return texts.networkKindEthernet
        case .vpn: return texts.networkKindTunnel
        case .other: return texts.networkKindOther
        }
    }

    /// «Wi-Fi · en0».
    static func title(_ network: PhoneNetwork, _ texts: PgTexts) -> String {
        [kind(network.kind, texts), network.name].compactMap { $0 }.joined(separator: " · ")
    }

    /// «по умолчанию · лимитная · экономия данных».
    static func marks(_ network: PhoneNetwork, _ texts: PgTexts) -> [String] {
        [
            network.isDefault ? texts.androidInspectDefault : nil, network.expensive ? texts.networkMetered : nil,
            network.constrained ? texts.iosInspectConstrained : nil,
        ].compactMap { $0 }
    }

    /// Строка отчета: вид, интерфейс, отметки, выход в интернет у сети по умолчанию.
    static func line(_ network: PhoneNetwork, _ texts: PgTexts) -> String {
        var parts = [title(network, texts)] + marks(network, texts)
        if network.isDefault {
            parts.append(network.satisfied ? texts.iosInspectOnline : texts.iosInspectOffline)
        }

        return parts.joined(separator: " · ")
    }
}

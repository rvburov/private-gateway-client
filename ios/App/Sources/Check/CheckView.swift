import PrivateGatewayCore
import PrivateGatewayTheme
import SwiftUI
import UIKit
import UniformTypeIdentifiers

/// «Проверка» — как `CheckScreen` Android: ошибка туннеля, четыре пункта защиты с объяснением, ядро, инспектор сети (сети
/// iPhone, внешний IP и его провайдер, замеры DNS, TCP и TLS) и журнал расширения туннеля.
struct CheckView: View {
    let texts: PgTexts
    let status: TunnelStatus
    let killSwitch: Bool
    let core: String?
    let networks: [PhoneNetwork]
    let ip: PublicIp
    let log: [String]
    @Binding var probe: ProbeState
    var onMeasure: () -> Void = {}
    var onDescribe: () -> Void = {}
    var onProbe: () -> Void = {}
    var onRefreshLog: () -> Void = {}

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: PgSpace.l) {
                Text(texts.navCheck).pgText(PgType.h1).foregroundStyle(Color(PgColor.Text.primary)).padding(.top, PgSpace.l)
                if status.state == .error {
                    VStack(alignment: .leading, spacing: PgSpace.xs) {
                        Text(status.error ?? "").pgText(PgType.mono).foregroundStyle(Color(PgColor.Status.Danger.text))
                        Text(texts.text(key: TextKeys.error(status.error)) ?? "").pgText(PgType.body).foregroundStyle(Color(PgColor.Text.primary))
                    }
                    .pgCard()
                    .accessibilityIdentifier("check.error")
                }

                VStack(alignment: .leading, spacing: PgSpace.l) {
                    ForEach(ProtectionChips.of(status.state, killSwitch: killSwitch, texts), id: \.title) { chip in
                        VStack(alignment: .leading, spacing: PgSpace.xxs) {
                            HStack(spacing: PgSpace.m) {
                                ZStack {
                                    Circle().fill(Color(chip.look.base))
                                    PgIcon(data: chip.look.mark, color: Color(PgColor.Surface.solid), size: 14, lineWidth: 3)
                                }
                                .frame(width: 22, height: 22)
                                Text(chip.title).pgText(PgType.bodyStrong).foregroundStyle(Color(PgColor.Text.primary))
                                Spacer(minLength: PgSpace.s)
                                Text(chip.value).pgText(PgType.small).foregroundStyle(Color(chip.look.base))
                            }

                            Text(chip.text).pgText(PgType.small).foregroundStyle(Color(PgColor.Text.secondary))
                        }
                        .accessibilityElement(children: .combine)
                    }
                }
                .pgCard()

                VStack(alignment: .leading, spacing: PgSpace.xs) {
                    Overline(text: texts.cardCore)
                    Text("Xray-core " + (core ?? "—")).pgText(PgType.mono).foregroundStyle(Color(PgColor.Text.primary)).accessibilityIdentifier("check.core")
                }
                .pgCard()

                NetworksCard(texts: texts, networks: networks)
                PublicIpCard(texts: texts, ip: ip, onMeasure: onMeasure, onDescribe: onDescribe)
                ProbeCard(texts: texts, probe: $probe, protected: status.state == .connected, onRun: onProbe)
                LogCard(texts: texts, lines: log, onRefresh: onRefreshLog)
            }
            .padding(.horizontal, PgSpace.windowSideMobile)
            .padding(.bottom, PgLayout.scrollBottomPadding)
        }
        .accessibilityIdentifier("check")
    }
}

/// Сети iPhone так, как их видит iOS: вид и имя, по умолчанию ли, дорогая ли, есть ли выход в интернет, что умеет.
private struct NetworksCard: View {
    let texts: PgTexts
    let networks: [PhoneNetwork]

    var body: some View {
        VStack(alignment: .leading, spacing: PgSpace.xs) {
            Overline(text: texts.iosInspectTitle)
            Text(texts.iosInspectNote).pgText(PgType.small).foregroundStyle(Color(PgColor.Text.secondary))
            if networks.isEmpty {
                Text(texts.iosInspectNone).pgText(PgType.body).foregroundStyle(Color(PgColor.Text.muted)).padding(.top, PgSpace.s)
            }

            ForEach(Array(networks.enumerated()), id: \.offset) { _, network in
                VStack(alignment: .leading, spacing: 2) {
                    Text([kind(network.kind), network.name].compactMap { $0 }.joined(separator: " · "))
                        .pgText(PgType.bodyStrong)
                        .foregroundStyle(Color(PgColor.Text.primary))
                    let marks = [
                        network.isDefault ? texts.androidInspectDefault : nil, network.expensive ? texts.networkMetered : nil,
                        network.constrained ? texts.iosInspectConstrained : nil,
                    ].compactMap { $0 }
                    if !marks.isEmpty {
                        Text(marks.joined(separator: " · ")).pgText(PgType.caption).foregroundStyle(Color(PgColor.Accent.text))
                    }

                    if network.isDefault {
                        Text(network.satisfied ? texts.iosInspectOnline : texts.iosInspectOffline)
                            .pgText(PgType.small)
                            .foregroundStyle(Color(network.satisfied ? PgColor.Status.Success.text : PgColor.Status.Warning.text))
                        let supports = [network.ipv4 ? texts.networkIpv4 : nil, network.ipv6 ? texts.networkIpv6 : nil, network.dns ? texts.networkDns : nil]
                            .compactMap { $0 }
                        if !supports.isEmpty {
                            Text(texts.iosInspectSupports(supports.joined(separator: ", "))).pgText(PgType.small).foregroundStyle(Color(PgColor.Text.secondary))
                        }
                    }
                }
                .padding(.top, PgSpace.m)
                .accessibilityIdentifier("inspect.network.\(network.name ?? network.kind.rawValue)")
            }
        }
        .pgCard()
        .accessibilityIdentifier("inspect.networks")
    }

    private func kind(_ kind: PhoneNetworkKind) -> String {
        switch kind {
        case .wifi: return texts.networkKindWifi
        case .cellular: return texts.networkKindCellular
        case .ethernet: return texts.networkKindEthernet
        case .vpn: return texts.networkKindTunnel
        case .other: return texts.networkKindOther
        }
    }
}

/// Внешний IP и кто провайдер: адрес, через сервер ли он, «Измерить» и «Кто провайдер» (RIPEstat — только по кнопке).
private struct PublicIpCard: View {
    let texts: PgTexts
    let ip: PublicIp
    let onMeasure: () -> Void
    let onDescribe: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: PgSpace.xs) {
            Overline(text: texts.networkPublic)
            if let address = ip.address {
                Text(address).pgText(PgType.mono).foregroundStyle(Color(PgColor.Text.primary)).accessibilityIdentifier("inspect.ip.address")
                Text(ip.viaServer ? texts.networkPublicExit : texts.networkPublicDirect).pgText(PgType.small).foregroundStyle(Color(PgColor.Text.secondary))
            } else if ip.failed {
                Text(texts.networkPublicUnreachable).pgText(PgType.small).foregroundStyle(Color(PgColor.Status.Warning.text))
            } else if !ip.measuring {
                Text(texts.networkPublicNone).pgText(PgType.small).foregroundStyle(Color(PgColor.Text.muted))
            }

            PgButton(label: ip.measuring ? texts.networkPublicMeasuring : texts.networkPublicMeasure, enabled: !ip.measuring, identifier: "inspect.ip.measure",
                     action: onMeasure)
                .padding(.top, PgSpace.s)
            if ip.address != nil {
                PgTextAction(label: ip.describing ? texts.networkPublicAsking : texts.networkPublicDescribe, enabled: !ip.describing,
                             identifier: "inspect.ip.describe", action: onDescribe)
            }

            if let info = ip.info {
                provider(info)
            }
        }
        .pgCard()
        .accessibilityIdentifier("inspect.ip")
    }

    @ViewBuilder
    private func provider(_ info: AddressInfo) -> some View {
        switch info.failure {
        case .private?:
            Text(texts.networkPublicPrivate).pgText(PgType.small).foregroundStyle(Color(PgColor.Text.secondary))
        case .unreachable?:
            Text(texts.networkPublicUnreachable).pgText(PgType.small).foregroundStyle(Color(PgColor.Status.Warning.text))
        case .notFound?:
            Text(texts.networkPublicNotfound).pgText(PgType.small).foregroundStyle(Color(PgColor.Text.secondary))
        case nil:
            let provider = [info.asn.map { "AS\($0)" }, info.holder].compactMap { $0 }.joined(separator: " · ")
            Fact(title: texts.networkPublicProvider, value: provider.isEmpty ? nil : provider)
            Fact(title: texts.networkPublicPrefix, value: info.prefix)
            let country = info.country.map { Locale(identifier: texts.language.rawValue).localizedString(forRegionCode: $0) ?? $0 }
            let place = [country, info.city].compactMap { $0 }.joined(separator: ", ")
            Fact(title: texts.networkPublicPlace, value: place.isEmpty ? nil : texts.networkPublicApprox(place))
            Text(texts.networkPublicSource).pgText(PgType.caption).foregroundStyle(Color(PgColor.Text.muted)).padding(.top, PgSpace.s)
        }
    }
}

/// Строка «подпись — значение»; значения нет — строки нет.
struct Fact: View {
    let title: String
    let value: String?

    var body: some View {
        if let value {
            HStack(alignment: .top, spacing: PgSpace.s) {
                Text(title).pgText(PgType.small).foregroundStyle(Color(PgColor.Text.secondary)).frame(width: 96, alignment: .leading)
                Text(value).pgText(PgType.mono).foregroundStyle(Color(PgColor.Text.primary)).frame(maxWidth: .infinity, alignment: .leading)
            }
            .padding(.top, 2)
        }
    }
}

/// Замер до узла — как `ProbeCard` Android: вид (DNS, TCP, TLS) и как он идет, узел, порт (у TCP и TLS), путь замера,
/// «Проверить», итог и ход по шагам, у TLS — сведения о сертификате.
private struct ProbeCard: View {
    let texts: PgTexts
    @Binding var probe: ProbeState
    let protected: Bool
    let onRun: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: PgSpace.xs) {
            Overline(text: texts.probeKind)
            ForEach(ProbeKind.allCases, id: \.self) { kind in
                PgChoiceRow(title: texts.text(key: TextKeys.probeKind(kind)) ?? kind.rawValue, selected: probe.kind == kind, identifier: "inspect.probe.\(kind.rawValue)") {
                    probe.kind = kind
                    probe.error = nil
                }
            }

            Text(texts.text(key: TextKeys.probeMethod(probe.kind)) ?? "").pgText(PgType.small).foregroundStyle(Color(PgColor.Text.secondary))
            ProbeField(label: texts.probeTarget, placeholder: texts.probeTargetPlaceholder, text: $probe.target, keyboard: .URL, identifier: "inspect.probe.target")
            if probe.kind != .dns {
                ProbeField(label: texts.probePortLabel, placeholder: "443", text: $probe.port, keyboard: .numberPad, identifier: "inspect.probe.port")
            }

            Text(protected ? texts.probePathVpn : texts.androidProbePathDirect)
                .pgText(PgType.caption)
                .foregroundStyle(Color(PgColor.Text.muted))
                .padding(.top, PgSpace.s)
            PgButton(label: texts.probeRun, enabled: !probe.running, identifier: "inspect.probe.run", action: onRun)
                .padding(.top, PgSpace.m)
            if let error = probe.error {
                Text(error == .target ? texts.probeErrorTarget : texts.probeErrorPort)
                    .pgText(PgType.small)
                    .foregroundStyle(Color(PgColor.Status.Warning.text))
                    .padding(.top, PgSpace.s)
                    .accessibilityIdentifier("inspect.probe.error")
            }

            if let result = probe.result {
                ProbeResultView(texts: texts, result: result)
            }
        }
        .pgCard()
        .accessibilityIdentifier("inspect.probe")
    }
}

/// Поле замера с подписью сверху; набор — моноширинный, без автозамены.
private struct ProbeField: View {
    let label: String
    let placeholder: String
    @Binding var text: String
    let keyboard: UIKeyboardType
    let identifier: String

    var body: some View {
        VStack(alignment: .leading, spacing: PgSpace.xxs) {
            Text(label).pgText(PgType.caption).foregroundStyle(Color(PgColor.Text.secondary))
            PgField(placeholder: placeholder, text: $text, style: PgType.mono, identifier: identifier)
                .keyboardType(keyboard)
        }
        .padding(.top, PgSpace.s)
    }
}

/// Итог замера строкой и ход по шагам: «1. 203.0.113.10 ответил 12 мс».
private struct ProbeResultView: View {
    let texts: PgTexts
    let result: ProbeResult

    var body: some View {
        let summary = result.summary()
        VStack(alignment: .leading, spacing: 2) {
            Text(texts.text(key: summary.key, summary.arguments) ?? "")
                .pgText(PgType.bodyStrong)
                .foregroundStyle(Color(PgColor.Text.primary))
                .accessibilityIdentifier("inspect.probe.summary")
            ForEach(Array(result.steps.enumerated()), id: \.offset) { _, step in
                Text(line(step)).pgText(PgType.mono).foregroundStyle(Color(PgColor.Text.secondary))
                if let tls = step.tls {
                    TlsFactsView(texts: texts, tls: tls)
                }
            }
        }
        .padding(.top, PgSpace.m)
    }

    private func line(_ step: ProbeStep) -> String {
        let time = step.timeMs.map { texts.probeMs(String($0)) }
        return ["\(step.index).", step.address, texts.text(key: TextKeys.probeOutcome(step.outcome)), time].compactMap { $0 }.joined(separator: " ")
    }
}

/// Сведения рукопожатия TLS: протокол, шифр, ALPN, кому и кем выдан, срок, имена и проверка по пунктам.
private struct TlsFactsView: View {
    let texts: PgTexts
    let tls: TlsFacts

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            Fact(title: texts.probeTlsProtocol, value: tls.protocol)
            Fact(title: texts.probeTlsCipher, value: tls.cipher)
            Fact(title: texts.probeTlsAlpn, value: tls.alpn)
            Fact(title: texts.probeTlsSubject, value: tls.subject)
            Fact(title: texts.probeTlsIssuer, value: tls.issuer)
            Fact(title: texts.probeTlsValid, value: valid)
            Fact(title: texts.probeTlsNames, value: tls.names.isEmpty ? nil : tls.names.joined(separator: "\n"))
            Fact(
                title: texts.probeTlsCheck,
                value: tls.problems.isEmpty ? texts.probeTlsOk : tls.problems.compactMap { texts.text(key: TextKeys.tlsProblem($0)) }.joined(separator: "\n")
            )
        }
    }

    /// «до 12 мар. 2027 г. · осталось 136 дн.» или «истек 1 янв. 2026 г.».
    private var valid: String? {
        guard let notAfter = tls.notAfter else {
            return nil
        }

        let format = DateFormatter()
        format.locale = Locale(identifier: texts.language.rawValue)
        format.dateStyle = .medium
        format.timeStyle = .none
        let now = Date()
        let date = format.string(from: notAfter)
        return now > notAfter ? texts.probeTlsExpired(date) : texts.probeTlsUntil(date, String(Int(notAfter.timeIntervalSince(now) / 86400)))
    }
}

/// Журнал расширения туннеля: последние записи сверху вниз, «Обновить» и «Скопировать журнал» (только на этом iPhone и на
/// 2 минуты, как ссылка сервера).
private struct LogCard: View {
    let texts: PgTexts
    let lines: [String]
    let onRefresh: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: PgSpace.xs) {
            Overline(text: texts.iosLogTitle)
            Text(texts.iosLogNote).pgText(PgType.small).foregroundStyle(Color(PgColor.Text.secondary))
            if lines.isEmpty {
                Text(texts.iosLogEmpty).pgText(PgType.body).foregroundStyle(Color(PgColor.Text.muted)).padding(.top, PgSpace.s)
            } else {
                Text(lines.suffix(50).joined(separator: "\n"))
                    .font(.system(size: PgType.caption.size, design: .monospaced))
                    .foregroundStyle(Color(PgColor.Text.primary))
                    .textSelection(.enabled)
                    .padding(.top, PgSpace.s)
                    .accessibilityIdentifier("check.log")
            }

            HStack(spacing: PgSpace.l) {
                PgTextAction(label: texts.iosLogRefresh, identifier: "check.log.refresh", action: onRefresh)
                if !lines.isEmpty {
                    PgTextAction(label: texts.iosLogCopy, identifier: "check.log.copy") {
                        UIPasteboard.general.setItems(
                            [[UTType.utf8PlainText.identifier: lines.joined(separator: "\n")]],
                            options: [.localOnly: true, .expirationDate: Date().addingTimeInterval(120)]
                        )
                    }
                }
            }
        }
        .pgCard()
        .accessibilityIdentifier("check.logs")
    }
}

import PrivateGatewayCore
import PrivateGatewayTheme
import SwiftUI
import UIKit
import UniformTypeIdentifiers

/// «Проверка» — как `CheckScreen` Android: ошибка туннеля, четыре пункта защиты с объяснением, ядро, инспектор сети (сети
/// iPhone, внешний IP и его провайдер) и журнал расширения туннеля. Замеры DNS, TCP и TLS — `ProbeCard`.
struct CheckView: View {
    let texts: PgTexts
    let status: TunnelStatus
    let killSwitch: Bool
    let core: String?
    let networks: [PhoneNetwork]
    let ip: PublicIp
    let log: [String]
    var onMeasure: () -> Void = {}
    var onDescribe: () -> Void = {}
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

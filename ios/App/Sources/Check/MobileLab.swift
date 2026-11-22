import Foundation
import PrivateGatewayCore
import PrivateGatewayTheme
import SwiftUI

/// Реальные возможности инспектора iOS: DNS, TCP и TLS, данные NWPathMonitor и события своего туннеля.
/// Трассировку, шлюзы и пакеты других приложений iOS обычному приложению не открывает.
enum MobileLabPage: String, CaseIterable, Hashable {
    case path, layers, events, trouble, compare

    var titleKey: String { "page.\(rawValue).title" }
    var icon: String {
        switch self {
        case .events: return PgIcons.clock
        case .layers, .compare: return PgIcons.netEthernet
        case .path, .trouble: return PgIcons.pulse
        }
    }
}

struct MobileLabSnapshot: Equatable {
    let time: Date
    let connected: Bool
    let address: String?
    let interfaces: [String]

    init(_ input: CheckInput, _ networks: [PhoneNetwork], now: Date = Date()) {
        time = now
        connected = input.state == .connected && !input.localSiteFilter
        address = input.ip.viaServer == connected && !input.ip.measuring && !input.ip.failed ? input.ip.address : nil
        interfaces = networks.filter(\.satisfied).map(\.name)
    }
}

@MainActor
final class MobileLabModel: ObservableObject {
    @Published var target = ""
    @Published private(set) var running = false
    @Published private(set) var invalidTarget = false
    @Published private(set) var results: [ProbeResult] = []
    @Published private(set) var before: MobileLabSnapshot?
    @Published private(set) var after: MobileLabSnapshot?
    private var generation: UInt64 = 0

    func capture(_ input: CheckInput, _ networks: [PhoneNetwork]) {
        let value = MobileLabSnapshot(input, networks)
        if value.connected { after = value } else if input.state == .off || input.localSiteFilter { before = value }
    }

    /// Первое неуспешное измерение останавливает цепочку; TLS с неверным сертификатом не считается успехом.
    func run() async {
        guard !running else { return }
        guard let host = ProbeTarget.normalize(target) else {
            invalidTarget = true
            return
        }
        invalidTarget = false
        generation &+= 1
        let current = generation
        running = true
        results = []
        for kind in ProbeKind.allCases {
            let result: ProbeResult
            switch kind {
            case .dns: result = await ProbeRunner.dns(host)
            case .tcp: result = await ProbeRunner.tcp(host, 443)
            case .tls: result = await ProbeRunner.tls(host, 443)
            }
            guard current == generation, !Task.isCancelled else { return }
            results.append(result)
            if !Self.succeeded(result) { break }
        }
        if current == generation { running = false }
    }

    func cancel() {
        generation &+= 1
        running = false
    }

    static func succeeded(_ result: ProbeResult) -> Bool {
        !result.steps.isEmpty && result.steps.allSatisfy {
            $0.outcome == .ok && ($0.tls?.problems.isEmpty ?? (result.kind != .tls))
        }
    }
}

struct MobileLabView: View {
    let texts: PgTexts
    let page: MobileLabPage
    let input: CheckInput
    let networks: [PhoneNetwork]
    let log: [TunnelLogEntry]
    let onBack: () -> Void
    @StateObject private var model = MobileLabModel()

    private func text(_ key: String) -> String { texts.text(key: key) ?? "" }

    var body: some View {
        VStack(spacing: 0) {
            PgPageHeader(title: text(page.titleKey), texts: texts, fixed: true, onBack: onBack)
            ScrollView {
                VStack(alignment: .leading, spacing: PgSpace.l) {
                    Text(text("ios.lab.scope"))
                        .pgText(PgType.caption)
                        .foregroundStyle(Color(PgColor.Text.muted))
                    switch page {
                    case .path, .trouble: site
                    case .layers: layers
                    case .events: events
                    case .compare: comparison
                    }
                }
                .padding(.horizontal, PgSpace.windowSideMobile)
                .padding(.bottom, PgLayout.scrollBottomPadding)
            }
        }
        .onChange(of: input.state) { _ in model.cancel() }
        .onChange(of: input.sessionKey) { _ in model.cancel() }
        .onChange(of: networks) { _ in model.cancel() }
        .onDisappear { model.cancel() }
        .accessibilityIdentifier("lab.\(page.rawValue)")
    }

    private var site: some View {
        VStack(alignment: .leading, spacing: PgSpace.m) {
            Text(text(input.state == .connected && !input.localSiteFilter ? "trouble.via.vpn" : "trouble.via.off"))
                .pgText(PgType.caption)
                .foregroundStyle(Color(PgColor.Text.muted))
            TextField(text("path.site"), text: $model.target)
                .textInputAutocapitalization(.never)
                .autocorrectionDisabled()
                .keyboardType(.URL)
                .pgText(PgType.body)
                .padding(PgSpace.m)
                .pgCard(padding: 0)
                .accessibilityIdentifier("lab.site")
                .disabled(model.running)
            if model.invalidTarget {
                Text(text("ios.lab.target.invalid")).pgText(PgType.caption).foregroundStyle(Color(PgColor.Status.Danger.text))
            }
            PgActionButton(label: text(model.running ? "path.checking" : "path.check"), kind: .primary, identifier: "lab.run") {
                Task { await model.run() }
            }
            .disabled(model.running || [.connecting, .reconnecting, .stopping].contains(input.state) || input.blocked)
            ForEach(Array(model.results.enumerated()), id: \.offset) { _, result in
                VStack(alignment: .leading, spacing: PgSpace.s) {
                    Text(result.kind.rawValue.uppercased()).pgText(PgType.bodyStrong).foregroundStyle(Color(PgColor.Text.primary))
                    ProbeResultView(texts: texts, result: result)
                }
                .pgCard()
            }
            if !model.running, model.results.count == ProbeKind.allCases.count,
               model.results.allSatisfy(MobileLabModel.succeeded) {
                Text(text("ios.lab.transport.ok"))
                    .pgText(PgType.caption)
                    .foregroundStyle(Color(PgColor.Text.muted))
            }
        }
    }

    private var layers: some View {
        VStack(alignment: .leading, spacing: PgSpace.m) {
            Text(text("ios.lab.layers.note")).pgText(PgType.caption).foregroundStyle(Color(PgColor.Text.muted))
            ForEach(Array((1...7).reversed()), id: \.self) { number in
                VStack(alignment: .leading, spacing: PgSpace.s) {
                    Text("\(number). " + text("layer.\(number).name")).pgText(PgType.bodyStrong).foregroundStyle(Color(PgColor.Text.primary))
                    Text(text("layer.\(number).text")).pgText(PgType.body).foregroundStyle(Color(PgColor.Text.secondary))
                    Text(text("layer.\(number).protocols")).pgText(PgType.caption).foregroundStyle(Color(PgColor.Text.muted))
                }
                .pgCard()
            }
            ForEach(networks, id: \.name) { network in
                Text(network.name + " · " + (network.satisfied ? text("diag.state.ok.chip") : text("lab.notMeasured")))
                    .pgText(PgType.mono).foregroundStyle(Color(PgColor.Text.secondary))
            }
        }
    }

    private var events: some View {
        VStack(alignment: .leading, spacing: PgSpace.m) {
            Text(text("ios.lab.events.scope")).pgText(PgType.caption).foregroundStyle(Color(PgColor.Text.muted))
            if log.isEmpty {
                Text(text("events.empty")).pgText(PgType.body).foregroundStyle(Color(PgColor.Text.muted))
            }
            ForEach(Array(log.reversed().enumerated()), id: \.offset) { _, entry in
                VStack(alignment: .leading, spacing: PgSpace.xs) {
                    Text(entry.time.map(GroupHeader.clock) ?? "—").pgText(PgType.caption).foregroundStyle(Color(PgColor.Text.muted))
                    Text(SecretMasker.mask(entry.message)).pgText(PgType.mono).foregroundStyle(Color(PgColor.Text.primary))
                }
                .pgCard()
            }
        }
    }

    private var comparison: some View {
        VStack(alignment: .leading, spacing: PgSpace.m) {
            Text(text("ios.lab.compare.scope")).pgText(PgType.caption).foregroundStyle(Color(PgColor.Text.muted))
            PgActionButton(label: text("ios.lab.compare.capture"), kind: .secondary, identifier: "lab.compare.capture") {
                model.capture(input, networks)
            }
            snapshot(model.before, title: text("compare.before"))
            snapshot(model.after, title: text("compare.now"))
        }
    }

    private func snapshot(_ value: MobileLabSnapshot?, title: String) -> some View {
        VStack(alignment: .leading, spacing: PgSpace.s) {
            Text(title).pgText(PgType.bodyStrong).foregroundStyle(Color(PgColor.Text.primary))
            if let value {
                Text(GroupHeader.clock(value.time)).pgText(PgType.caption).foregroundStyle(Color(PgColor.Text.muted))
                Text(value.address ?? text("lab.notMeasured")).pgText(PgType.mono).foregroundStyle(Color(PgColor.Text.primary))
                Text(value.interfaces.joined(separator: ", ")).pgText(PgType.caption).foregroundStyle(Color(PgColor.Text.secondary))
            } else {
                Text(text("lab.notMeasured")).pgText(PgType.body).foregroundStyle(Color(PgColor.Text.muted))
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .pgCard()
    }
}

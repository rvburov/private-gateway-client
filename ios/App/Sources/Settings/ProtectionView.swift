import PrivateGatewayCore
import PrivateGatewayTheme
import SwiftUI

/// «Защита и утечки» для телефона (доска 5 в одну колонку) — как `ProtectionScreen` Android и блок при обрыве: на iOS он —
/// свойство профиля VPN (`includeAllNetworks`), включить его может само приложение (решение 460). DNS в туннеле, IPv6,
/// «Постоянная защита» — подключение по требованию iOS (решение 469). DNS в туннеле, IPv6, локальная сеть. Под защитой
/// изменения применяются сразу: ядро перечитывает настройки, а профиль VPN — переподключением.
struct ProtectionView: View {
    let texts: PgTexts
    let tunnel: TunnelSettings
    let killSwitch: Bool
    let alwaysOn: Bool
    let onTunnel: (TunnelSettings) -> Void
    let onKillSwitch: (Bool) -> Void
    let onAlwaysOn: (Bool) -> Void
    let onBack: () -> Void

    @State private var editingDns: Bool

    init(
        texts: PgTexts, tunnel: TunnelSettings, killSwitch: Bool, alwaysOn: Bool = false, editingDns: Bool = false,
        onTunnel: @escaping (TunnelSettings) -> Void, onKillSwitch: @escaping (Bool) -> Void, onAlwaysOn: @escaping (Bool) -> Void = { _ in },
        onBack: @escaping () -> Void
    ) {
        self.texts = texts
        self.tunnel = tunnel
        self.killSwitch = killSwitch
        self.alwaysOn = alwaysOn
        self.onTunnel = onTunnel
        self.onKillSwitch = onKillSwitch
        self.onAlwaysOn = onAlwaysOn
        self.onBack = onBack
        _editingDns = State(initialValue: editingDns)
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: PgSpace.l) {
                PgPageHeader(title: texts.settingsAdvProtectionTitle, texts: texts, onBack: onBack)

                VStack(alignment: .leading, spacing: PgSpace.xs) {
                    Overline(text: texts.protGroupKs)
                    PgToggleRow(title: texts.protKsTitle, text: texts.iosProtKsText, isOn: killSwitch, identifier: "killSwitch", onChange: onKillSwitch)
                    PgToggleRow(title: texts.iosProtAlwaysTitle, text: texts.iosProtAlwaysText, isOn: alwaysOn, identifier: "alwaysOn", onChange: onAlwaysOn)
                }
                .pgCard()

                VStack(alignment: .leading, spacing: PgSpace.xs) {
                    Overline(text: texts.protGroupDns)
                    Text(texts.protDnsTitle).pgText(PgType.title).foregroundStyle(Color(PgColor.Text.primary)).padding(.top, PgSpace.xs)
                    Text(texts.protDnsText).pgText(PgType.small).foregroundStyle(Color(PgColor.Text.secondary))
                    ForEach([(DnsChoice.profile, texts.protDnsProfile), (.cloudflareDoh, texts.protDnsCloudflare), (.quad9Doh, texts.protDnsQuad9)], id: \.0) { item in
                        PgChoiceRow(title: item.1, selected: tunnel.dns == item.0, identifier: "dns.\(item.0.rawValue)") {
                            var next = tunnel
                            next.dns = item.0
                            onTunnel(next)
                        }
                    }

                    // Свой адрес: выбор открывает ввод; без верного адреса выбор не сохраняется.
                    let custom = tunnel.customDns.flatMap { TunnelSettings.validCustomDns($0) ? $0 : nil }
                    PgChoiceRow(title: texts.protDnsCustom, text: custom, selected: tunnel.dns == .custom, identifier: "dns.custom") {
                        editingDns = true
                    }

                    if editingDns {
                        CustomDnsEditor(texts: texts, initial: tunnel.customDns ?? "") { address in
                            editingDns = false
                            var next = tunnel
                            next.dns = .custom
                            next.customDns = address
                            onTunnel(next)
                        } onCancel: {
                            editingDns = false
                        }
                    }
                }
                .pgCard()

                VStack(alignment: .leading, spacing: PgSpace.xs) {
                    Overline(text: texts.protGroupIpv6)
                    Text(texts.protAddrTitle).pgText(PgType.title).foregroundStyle(Color(PgColor.Text.primary)).padding(.top, PgSpace.xs)
                    Text(texts.protAddrText).pgText(PgType.small).foregroundStyle(Color(PgColor.Text.secondary))
                    // Без IPv6 через сервер ядро отправляет IPv6 в блок, а DNS отвечает только адресами IPv4 (решение 459).
                    PgChoiceRow(title: texts.protAddrOnly4, selected: !tunnel.ipv6, identifier: "ipv6.off") { setIpv6(false) }
                    PgChoiceRow(title: texts.protAddrIpv4, selected: tunnel.ipv6, identifier: "ipv6.on") { setIpv6(true) }
                }
                .pgCard()

                VStack(alignment: .leading, spacing: PgSpace.xs) {
                    Overline(text: texts.protGroupLan)
                    PgToggleRow(title: texts.protLanTitle, text: texts.protLanText, isOn: tunnel.allowLan, identifier: "lan") { allow in
                        var next = tunnel
                        next.allowLan = allow
                        onTunnel(next)
                    }
                }
                .pgCard()
            }
            .padding(.horizontal, PgSpace.windowSideMobile)
            .padding(.bottom, PgLayout.scrollBottomPadding)
        }
        .accessibilityIdentifier("protection")
    }

    private func setIpv6(_ on: Bool) {
        var next = tunnel
        next.ipv6 = on
        onTunnel(next)
    }
}

/// Свой адрес DNS: IP или DoH по IP (https://IP/…); неверный не сохраняется — ядру ушел бы DNS по умолчанию молча.
struct CustomDnsEditor: View {
    let texts: PgTexts
    let onSave: (String) -> Void
    let onCancel: () -> Void

    @State private var text: String
    @State private var tried = false

    init(texts: PgTexts, initial: String, onSave: @escaping (String) -> Void, onCancel: @escaping () -> Void) {
        self.texts = texts
        self.onSave = onSave
        self.onCancel = onCancel
        _text = State(initialValue: initial)
    }

    var body: some View {
        let valid = TunnelSettings.validCustomDns(text)
        VStack(alignment: .leading, spacing: PgSpace.s) {
            Text(texts.protDnsCustomLabel).pgText(PgType.caption).foregroundStyle(Color(PgColor.Text.secondary))
            PgField(placeholder: texts.androidDnsCustomPlaceholder, text: $text, style: PgType.mono, identifier: "dns.custom.text")
                .keyboardType(.URL)
                .overlay {
                    if tried && !valid {
                        RoundedRectangle(cornerRadius: PgRadius.input, style: .continuous).stroke(Color(PgColor.Status.Danger.base), lineWidth: 1)
                    }
                }
            if tried && !valid {
                Text(texts.protDnsCustomBad).pgText(PgType.caption).foregroundStyle(Color(PgColor.Status.Danger.text))
            } else {
                Text(texts.androidDnsCustomNote).pgText(PgType.caption).foregroundStyle(Color(PgColor.Text.muted))
            }

            HStack(spacing: PgSpace.l) {
                Spacer()
                PgTextAction(label: texts.dialogCancel, identifier: "dns.custom.cancel", action: onCancel)
                PgTextAction(label: texts.cardRenameSave, identifier: "dns.custom.save") {
                    tried = true
                    if valid {
                        onSave(text.trimmingCharacters(in: .whitespacesAndNewlines))
                    }
                }
            }
        }
        .padding(.top, PgSpace.s)
    }
}

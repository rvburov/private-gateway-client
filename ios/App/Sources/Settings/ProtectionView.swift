import PrivateGatewayCore
import PrivateGatewayTheme
import SwiftUI

/// «Защита и утечки» для телефона (доска 05, ProtectionPage десктопа): шапка с «Назад» стоит на месте, прокручиваются группы
/// с подписью над карточкой — «Блок при обрыве» (на iOS он — свойство профиля VPN `includeAllNetworks`, включить его может само
/// приложение, решение 460; выключение — через вопрос; «Постоянная защита» — подключение по требованию iOS, решение 469),
/// DNS (выбор меню у кнопки, «Свой адрес…» — нижний лист), IPv6 (мимо туннеля IPv6 не идет никогда — строка всегда включена;
/// приоритет адресов — меню), локальная сеть. Чего телефон не умеет (строгий режим, пауза для входа в Wi-Fi, DNS провайдера,
/// FakeDNS, обнаружение устройств, другие VPN), здесь нет. Под защитой изменения применяются сразу: ядро перечитывает
/// настройки, а профиль VPN — переподключением.
struct ProtectionView: View {
    let texts: PgTexts
    let tunnel: TunnelSettings
    let killSwitch: Bool
    let alwaysOn: Bool
    let onTunnel: (TunnelSettings) -> Void
    let onKillSwitch: (Bool) -> Void
    let onAlwaysOn: (Bool) -> Void
    let onBack: () -> Void
    /// «Вход в Wi-Fi отеля или кафе» → «Открыть»: вопрос о паузе на 5 минут (решение 557).
    let onCaptive: () -> Void

    /// Открыт ли лист «Свой адрес DNS».
    @State private var editingDns: Bool

    init(
        texts: PgTexts, tunnel: TunnelSettings, killSwitch: Bool, alwaysOn: Bool = false, editingDns: Bool = false,
        onTunnel: @escaping (TunnelSettings) -> Void, onKillSwitch: @escaping (Bool) -> Void, onAlwaysOn: @escaping (Bool) -> Void = { _ in },
        onBack: @escaping () -> Void, onCaptive: @escaping () -> Void = {}
    ) {
        self.texts = texts
        self.tunnel = tunnel
        self.killSwitch = killSwitch
        self.alwaysOn = alwaysOn
        self.onTunnel = onTunnel
        self.onKillSwitch = onKillSwitch
        self.onAlwaysOn = onAlwaysOn
        self.onBack = onBack
        self.onCaptive = onCaptive
        _editingDns = State(initialValue: editingDns)
    }

    var body: some View {
        VStack(spacing: 0) {
            PgPageHeader(title: texts.settingsAdvProtectionTitle, texts: texts, fixed: true, onBack: onBack)
            ScrollView {
                VStack(alignment: .leading, spacing: PgSpace.card) {
                    SettingsGroup(title: texts.protGroupKs) {
                        PgToggleRow(
                            title: texts.protKsTitle, text: texts.iosProtKsText, isOn: killSwitch, padded: true, confirmOff: SettingsDialogs.ksOff(texts),
                            identifier: "killSwitch", onChange: onKillSwitch
                        )
                        PgDivider().padding(.horizontal, PgSpace.card)
                        PgToggleRow(
                            title: texts.iosProtAlwaysTitle, text: texts.iosProtAlwaysText, isOn: alwaysOn, padded: true, identifier: "alwaysOn",
                            onChange: onAlwaysOn
                        )
                        PgDivider().padding(.horizontal, PgSpace.card)
                        PgSettingRow(title: texts.protCaptiveTitle, caption: texts.protCaptiveText) {
                            PgActionButton(label: texts.protCaptiveOpen, kind: .secondary, identifier: "prot.captive", action: onCaptive)
                                .fixedSize()
                        }
                    }

                    SettingsGroup(title: texts.protGroupDns) {
                        PgSettingRow(title: texts.protDnsTitle, caption: texts.protDnsText) {
                            PgSelect(
                                options: ProtectionLook.dnsOptions(texts), selection: tunnel.dns, label: texts.protDnsTitle, identifier: "dns",
                                valueTitle: ProtectionLook.customDnsTitle(tunnel),
                                link: PgMenuLink(title: texts.protDnsCustom) { editingDns = true },
                                onSelect: { choice in
                                    var next = tunnel
                                    next.dns = choice
                                    onTunnel(next)
                                }
                            )
                        }
                    }

                    SettingsGroup(title: texts.protGroupIpv6) {
                        // Маршрут ::/0 всегда в туннеле, а без IPv6 через сервер ядро отправляет IPv6 в блок (решение 459): мимо
                        // туннеля IPv6 не идет — выключить это нельзя.
                        PgToggleRow(title: texts.protIpv6Title, text: texts.protIpv6Text, isOn: true, enabled: false, padded: true, identifier: "ipv6.block") { _ in }
                        PgDivider().padding(.horizontal, PgSpace.card)
                        PgSettingRow(title: texts.protAddrTitle, caption: texts.protAddrText) {
                            PgSelect(
                                options: ProtectionLook.addressOptions(texts), selection: tunnel.ipv6, label: texts.protAddrTitle, identifier: "ipv6",
                                onSelect: { on in
                                    var next = tunnel
                                    next.ipv6 = on
                                    onTunnel(next)
                                }
                            )
                        }
                    }

                    SettingsGroup(title: texts.protGroupLan) {
                        PgToggleRow(title: texts.protLanTitle, text: texts.protLanText, isOn: tunnel.allowLan, padded: true, identifier: "lan") { allow in
                            var next = tunnel
                            next.allowLan = allow
                            onTunnel(next)
                        }
                    }
                }
                .padding(.horizontal, PgSpace.windowSideMobile)
                .padding(.bottom, PgLayout.scrollBottomPadding)
            }
        }
        .pgSheet(isPresented: $editingDns) {
            CustomDnsSheet(texts: texts, initial: tunnel.customDns ?? "") { address in
                editingDns = false
                var next = tunnel
                next.dns = .custom
                next.customDns = address
                onTunnel(next)
            } onClose: {
                editingDns = false
            }
        }
        .accessibilityIdentifier("protection")
    }
}

/// Правила «Защиты и утечек» — как ProtectionPageViewModel десктопа.
enum ProtectionLook {
    /// Пункты «DNS в туннеле» по порядку макета; «Свой адрес…» — ссылкой под чертой, он открывает лист.
    static func dnsOptions(_ texts: PgTexts) -> [PgOption<DnsChoice>] {
        [
            PgOption(value: DnsChoice.profile, title: texts.protDnsProfile), PgOption(value: DnsChoice.cloudflareDoh, title: texts.protDnsCloudflare),
            PgOption(value: DnsChoice.quad9Doh, title: texts.protDnsQuad9),
        ]
    }

    /// Подпись кнопки при своем адресе — сам адрес, как DnsText десктопа: IP как есть, DoH — «DoH: адрес» (путь не помещается
    /// в кнопку и ничего не говорит). Выбран не свой адрес — nil: на кнопке подпись пункта.
    static func customDnsTitle(_ tunnel: TunnelSettings) -> String? {
        guard tunnel.dns == .custom, let custom = tunnel.customDns?.trimmingCharacters(in: .whitespacesAndNewlines), !custom.isEmpty else {
            return nil
        }

        let scheme = "https://"
        guard custom.lowercased().hasPrefix(scheme) else {
            return custom
        }

        let host = String(custom.dropFirst(scheme.count).prefix { $0 != "/" })
        return host.isEmpty ? custom : "DoH: " + host
    }

    /// «Приоритет адресов» по порядку макета: «Предпочитать IPv4» — IPv4 и IPv6 через сервер, «Только IPv4» — IPv6 в блок,
    /// DNS отвечает только адресами IPv4 (решение 459). «Предпочитать IPv6» телефону не по силам: в туннеле семейство адреса
    /// выбирает само приложение, ядро может отдать оба семейства или одно.
    static func addressOptions(_ texts: PgTexts) -> [PgOption<Bool>] {
        [PgOption(value: true, title: texts.protAddrIpv4), PgOption(value: false, title: texts.protAddrOnly4)]
    }
}

/// Лист «Свой адрес DNS» (.sheet доски 05, DnsAddressView десктопа): заголовок и крестик «Закрыть», поле «Адрес» моноширинным,
/// подпись под полем, внизу рядом «Отмена» и «Сохранить». Годится IP или DoH по IP (https://IP/…); неверный адрес не
/// сохраняется — под полем красным, почему: ядру ушел бы DNS по умолчанию молча.
struct CustomDnsSheet: View {
    let texts: PgTexts
    let onSave: (String) -> Void
    let onClose: () -> Void

    @State private var text: String
    @State private var tried: Bool

    init(texts: PgTexts, initial: String, tried: Bool = false, onSave: @escaping (String) -> Void, onClose: @escaping () -> Void) {
        self.texts = texts
        self.onSave = onSave
        self.onClose = onClose
        _text = State(initialValue: initial)
        _tried = State(initialValue: tried)
    }

    var body: some View {
        let bad = tried && !TunnelSettings.validCustomDns(text)
        VStack(spacing: 0) {
            HStack(spacing: PgSpace.s) {
                Text(texts.protDnsCustomTitle)
                    .pgText(PgType.status)
                    .foregroundStyle(Color(PgColor.Text.primary))
                    .lineLimit(1)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .accessibilityAddTraits(.isHeader)
                Button(action: onClose) {
                    PgIcon(data: PgIcons.close, color: Color(PgColor.Text.secondary), size: 20, lineWidth: 2.2)
                        .frame(width: PgLayout.touchTarget, height: PgLayout.touchTarget)
                        .contentShape(Rectangle())
                }
                .buttonStyle(PgPressStyle())
                .accessibilityLabel(texts.cardClose)
                .accessibilityIdentifier("dns.custom.close")
            }

            ScrollView {
                VStack(alignment: .leading, spacing: PgSpace.xs) {
                    Text(texts.protDnsCustomLabel).pgText(PgType.small.with(weight: PgType.bodyStrong.weight)).foregroundStyle(Color(PgColor.Text.secondary))
                    PgField(
                        placeholder: texts.androidDnsCustomPlaceholder, text: $text,
                        style: PgTextStyle(size: PgType.caption.size, weight: PgType.mono.weight, mono: true), identifier: "dns.custom.text"
                    )
                    .keyboardType(.URL)
                    .accessibilityLabel(texts.protDnsCustomLabel)
                    .overlay {
                        if bad {
                            RoundedRectangle(cornerRadius: PgRadius.input, style: .continuous).stroke(Color(PgColor.Status.Danger.base), lineWidth: 1)
                        }
                    }
                    if bad {
                        Text(texts.protDnsCustomBad)
                            .pgText(PgType.caption)
                            .foregroundStyle(Color(PgColor.Status.Danger.text))
                            .fixedSize(horizontal: false, vertical: true)
                            .accessibilityIdentifier("dns.custom.bad")
                    } else {
                        Text(texts.androidDnsCustomNote)
                            .pgText(PgType.caption)
                            .foregroundStyle(Color(PgColor.Text.muted))
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }
                .padding(.vertical, PgSpace.card)
            }

            HStack(spacing: PgSpace.s) {
                PgActionButton(label: texts.dialogCancel, kind: .secondary, identifier: "dns.custom.cancel", height: 50, action: onClose)
                PgActionButton(label: texts.cardRenameSave, identifier: "dns.custom.save", height: 50) {
                    tried = true
                    if TunnelSettings.validCustomDns(text) {
                        onSave(text.trimmingCharacters(in: .whitespacesAndNewlines))
                    }
                }
            }
        }
        .onChange(of: text) { _ in tried = false }
        .accessibilityIdentifier("dns.custom")
    }
}

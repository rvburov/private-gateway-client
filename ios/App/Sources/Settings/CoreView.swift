import PrivateGatewayCore
import PrivateGatewayTheme
import SwiftUI

/// «Подключение и ядро» для телефона (доска 06, CorePage десктопа): шапка с «Назад» стоит на месте, прокручиваются группы с
/// подписью над карточкой. «Сетевое ядро» — версия Xray-core (ее сообщает расширение туннеля после первого подключения),
/// Hysteria2 и TUIC — что телефон их не подключает (решение 423), уровень журнала ядра. «TUN (режим VPN)» — MTU меню с
/// «Свое…» (нижний лист) и определение домена. «Соединение» — отпечаток TLS, Mux, TLS-фрагментация, таймаут подключения
/// (сколько расширение ждет ответа сервера на пробе связи) и переподключение. Стека TUN, строгих маршрутов, локального и
/// системного прокси здесь нет: ядро телефона стек не выбирает, маршруты держит сама iOS (`includeAllNetworks`,
/// `enforceRoutes`), а входа прокси для программ у телефона нет (решение 527). Под защитой изменения применяются сразу —
/// ядро перезапускается в том же туннеле (`SettingsActions`).
struct CoreView: View {
    let texts: PgTexts
    let connection: ConnectionSettings
    let reconnect: ReconnectPolicy
    /// Версия Xray-core от расширения туннеля; до первого подключения — nil.
    let coreVersion: String?
    let onConnection: (ConnectionSettings) -> Void
    let onReconnect: (ReconnectPolicy) -> Void
    let onBack: () -> Void

    /// Открыт ли лист «Свой MTU».
    @State private var editingMtu: Bool

    init(
        texts: PgTexts, connection: ConnectionSettings, reconnect: ReconnectPolicy, coreVersion: String?, editingMtu: Bool = false,
        onConnection: @escaping (ConnectionSettings) -> Void, onReconnect: @escaping (ReconnectPolicy) -> Void, onBack: @escaping () -> Void
    ) {
        self.texts = texts
        self.connection = connection
        self.reconnect = reconnect
        self.coreVersion = coreVersion
        self.onConnection = onConnection
        self.onReconnect = onReconnect
        self.onBack = onBack
        _editingMtu = State(initialValue: editingMtu)
    }

    var body: some View {
        VStack(spacing: 0) {
            PgPageHeader(title: texts.settingsAdvCoreTitle, texts: texts, fixed: true, onBack: onBack)
            ScrollView {
                VStack(alignment: .leading, spacing: PgSpace.card) {
                    SettingsGroup(title: texts.coreGroupCore) {
                        coreGroup
                    }

                    SettingsGroup(title: texts.coreGroupTun) {
                        tunGroup
                    }

                    SettingsGroup(title: texts.coreGroupConn) {
                        connectionGroup
                    }
                }
                .padding(.horizontal, PgSpace.windowSideMobile)
                .padding(.bottom, PgLayout.scrollBottomPadding)
            }
        }
        .pgSheet(isPresented: $editingMtu) {
            MtuSheet(texts: texts, initial: CoreLook.mtuSelection(connection).map { String($0) } ?? "") { mtu in
                editingMtu = false
                change { $0.mtu = mtu }
            } onClose: {
                editingMtu = false
            }
        }
        .accessibilityIdentifier("core")
    }

    /// «Сетевое ядро»: Xray-core с версией в рамке, Hysteria2 и TUIC — «не установлено», журнал ядра меню.
    @ViewBuilder
    private var coreGroup: some View {
        PgSettingRow(title: texts.coreXrayTitle, caption: texts.coreXrayText) {
            PgValueBox(text: CoreLook.version(coreVersion, texts))
                .accessibilityIdentifier("core.xray")
        }
        PgDivider().padding(.horizontal, PgSpace.card)
        PgSettingRow(title: texts.coreSecondTitle, caption: CoreLook.secondNote(texts)) {
            PgValueBox(text: texts.coreVersionNone)
                .accessibilityIdentifier("core.second")
        }
        PgDivider().padding(.horizontal, PgSpace.card)
        PgSettingRow(title: texts.coreLogTitle, caption: texts.coreLogText) {
            PgSelect(
                options: CoreLook.logOptions(texts), selection: connection.coreLog, label: texts.coreLogTitle, identifier: "core.log",
                onSelect: { level in change { $0.coreLog = level } }
            )
        }
    }

    /// «TUN (режим VPN)»: MTU меню («Свое…» — ссылкой под чертой, открывает лист) и определение домена.
    @ViewBuilder
    private var tunGroup: some View {
        PgSettingRow(title: texts.coreMtuTitle, caption: texts.coreMtuText) {
            PgSelect(
                options: CoreLook.mtuOptions(texts), selection: CoreLook.mtuSelection(connection), label: texts.coreMtuTitle, identifier: "core.mtu",
                valueTitle: CoreLook.customMtuTitle(connection), link: PgMenuLink(title: texts.coreMtuCustom) { editingMtu = true },
                onSelect: { mtu in change { $0.mtu = mtu } }
            )
        }
        PgDivider().padding(.horizontal, PgSpace.card)
        PgToggleRow(title: texts.coreSniffTitle, text: texts.coreSniffText, isOn: connection.sniffing, padded: true, identifier: "core.sniff") { on in
            change { $0.sniffing = on }
        }
    }

    /// «Соединение»: отпечаток TLS, Mux, TLS-фрагментация, таймаут и переподключение — последней строкой группы.
    @ViewBuilder
    private var connectionGroup: some View {
        PgSettingRow(title: texts.coreFpTitle, caption: texts.coreFpText) {
            PgSelect(
                options: CoreLook.fingerprintOptions(texts), selection: connection.fingerprint, label: texts.coreFpTitle, identifier: "core.fp",
                onSelect: { fingerprint in change { $0.fingerprint = fingerprint } }
            )
        }
        PgDivider().padding(.horizontal, PgSpace.card)
        PgToggleRow(title: texts.coreMuxTitle, text: texts.coreMuxText, isOn: connection.mux, padded: true, identifier: "core.mux") { on in
            change { $0.mux = on }
        }
        PgDivider().padding(.horizontal, PgSpace.card)
        PgToggleRow(title: texts.coreFragTitle, text: texts.coreFragText, isOn: connection.tlsFragment, padded: true, identifier: "core.frag") { on in
            change { $0.tlsFragment = on }
        }
        PgDivider().padding(.horizontal, PgSpace.card)
        PgSettingRow(title: texts.coreTimeoutTitle, caption: texts.coreTimeoutText) {
            PgSelect(
                options: CoreLook.timeoutOptions(texts), selection: connection.connectTimeout, label: texts.coreTimeoutTitle, identifier: "core.timeout",
                valueTitle: CoreLook.customTimeoutTitle(connection, texts), onSelect: { seconds in change { $0.connectTimeout = seconds } }
            )
        }
        PgDivider().padding(.horizontal, PgSpace.card)
        PgSettingRow(title: texts.coreReconnectTitle, caption: texts.coreReconnectText) {
            PgSelect(
                options: CoreLook.reconnectOptions(texts), selection: reconnect, label: texts.coreReconnectTitle, identifier: "core.reconnect",
                onSelect: onReconnect
            )
        }
    }

    /// Изменить одну настройку и отдать подключение целиком.
    private func change(_ edit: (inout ConnectionSettings) -> Void) {
        var next = connection
        edit(&next)
        onConnection(next)
    }
}

/// Правила «Подключения и ядра» — как CorePageViewModel десктопа.
enum CoreLook {
    /// Версия Xray-core от расширения туннеля; до первого подключения ее нет — «не установлено», как у десктопа без службы.
    static func version(_ version: String?, _ texts: PgTexts) -> String {
        guard let version, !version.isEmpty else {
            return texts.coreVersionNone
        }

        return version
    }

    /// Подпись строки «Hysteria2, TUIC» у телефона — с заглавной буквы: «Нет: Hysteria2 и TUIC телефон не подключает». Подпись
    /// десктопа («Их ведет sing-box за Xray…») телефону не годится: sing-box на телефоне нет (решение 423).
    static func secondNote(_ texts: PgTexts) -> String {
        let text = texts.androidCardCoreNone
        return text.prefix(1).uppercased() + text.dropFirst()
    }

    /// Уровни журнала по порядку доски.
    static func logOptions(_ texts: PgTexts) -> [PgOption<CoreLogLevel>] {
        [
            PgOption(value: CoreLogLevel.warning, title: texts.coreLogWarning), PgOption(value: CoreLogLevel.info, title: texts.coreLogInfo),
            PgOption(value: CoreLogLevel.debug, title: texts.coreLogDebug),
        ]
    }

    /// MTU по порядку доски: «Авто», 1500, 1420, 1280; «Свое…» — ссылкой под чертой.
    static func mtuOptions(_ texts: PgTexts) -> [PgOption<Int?>] {
        ConnectionSettings.mtuChoices.map { value in
            PgOption(value: value, title: value.map { String($0) } ?? texts.coreMtuAuto)
        }
    }

    /// Выбранный MTU: свой годный — он, иначе «Авто» (nil).
    static func mtuSelection(_ connection: ConnectionSettings) -> Int? {
        guard let mtu = connection.mtu, ConnectionSettings.validMtu(mtu) else {
            return nil
        }

        return mtu
    }

    /// Подпись кнопки при своем MTU не из меню — само число; MTU из меню или «Авто» — nil: на кнопке подпись пункта.
    static func customMtuTitle(_ connection: ConnectionSettings) -> String? {
        guard let mtu = mtuSelection(connection), !ConnectionSettings.mtuChoices.contains(mtu) else {
            return nil
        }

        return String(mtu)
    }

    /// Отпечатки по порядку доски; имена — как их пишут в ссылках (fp=chrome): так их узнают и ищут.
    static func fingerprintOptions(_ texts: PgTexts) -> [PgOption<TlsFingerprint>] {
        TlsFingerprint.allCases.map { value in
            PgOption(value: value, title: value.xray ?? texts.coreFpProfile)
        }
    }

    /// Таймаут по порядку доски: 10 с (по умолчанию), 5, 20, 30.
    static func timeoutOptions(_ texts: PgTexts) -> [PgOption<Int>] {
        ConnectionSettings.timeoutChoices.map { PgOption(value: $0, title: texts.coreTimeoutValue($0)) }
    }

    /// Подпись кнопки при таймауте не из меню (сохранен другой версией) — «N с»; из меню — nil.
    static func customTimeoutTitle(_ connection: ConnectionSettings, _ texts: PgTexts) -> String? {
        ConnectionSettings.timeoutChoices.contains(connection.connectTimeout) ? nil : texts.coreTimeoutValue(connection.connectTimeout)
    }

    /// Переподключение — пункты меню по порядку доски.
    static func reconnectOptions(_ texts: PgTexts) -> [PgOption<ReconnectPolicy>] {
        [
            PgOption(value: ReconnectPolicy.backoff60Seconds, title: texts.coreReconnectBackoff),
            PgOption(value: ReconnectPolicy.upTo5Minutes, title: texts.coreReconnect5min), PgOption(value: ReconnectPolicy.never, title: texts.coreReconnectNever),
        ]
    }
}

/// Лист «Свой MTU» (.sheet доски 06, MtuView десктопа): заголовок и крестик «Закрыть», поле «MTU» моноширинным до 4 цифр с
/// подсказкой 1420, подпись под полем, внизу рядом «Отмена» и «Сохранить». Число вне 1280–1500 не сохраняется — под полем
/// красным, почему.
struct MtuSheet: View {
    /// Больше цифр в MTU не бывает.
    static let maxDigits = 4

    let texts: PgTexts
    let onSave: (Int) -> Void
    let onClose: () -> Void

    @State private var text: String
    @State private var tried: Bool

    init(texts: PgTexts, initial: String, tried: Bool = false, onSave: @escaping (Int) -> Void, onClose: @escaping () -> Void) {
        self.texts = texts
        self.onSave = onSave
        self.onClose = onClose
        _text = State(initialValue: initial)
        _tried = State(initialValue: tried)
    }

    var body: some View {
        let bad = tried && ConnectionSettings.parseMtu(text) == nil
        VStack(spacing: 0) {
            PgSheetHeader(title: texts.coreMtuCustomTitle, closeLabel: texts.cardClose, identifier: "mtu.close", onClose: onClose)

            ScrollView {
                VStack(alignment: .leading, spacing: PgSpace.xs) {
                    Text(texts.coreMtuTitle).pgText(PgType.small.with(weight: PgType.bodyStrong.weight)).foregroundStyle(Color(PgColor.Text.secondary))
                    PgField(
                        placeholder: texts.coreMtuPlaceholder, text: $text,
                        style: PgTextStyle(size: PgType.caption.size, weight: PgType.mono.weight, mono: true), identifier: "mtu.text"
                    )
                    .keyboardType(.numberPad)
                    .accessibilityLabel(texts.coreMtuTitle)
                    .overlay {
                        if bad {
                            RoundedRectangle(cornerRadius: PgRadius.input, style: .continuous).stroke(Color(PgColor.Status.Danger.base), lineWidth: 1)
                        }
                    }
                    if bad {
                        Text(texts.coreMtuCustomBad)
                            .pgText(PgType.caption)
                            .foregroundStyle(Color(PgColor.Status.Danger.text))
                            .fixedSize(horizontal: false, vertical: true)
                            .accessibilityIdentifier("mtu.bad")
                    } else {
                        Text(texts.coreMtuCustomNote)
                            .pgText(PgType.caption)
                            .foregroundStyle(Color(PgColor.Text.muted))
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }
                .padding(.vertical, PgSpace.card)
            }

            HStack(spacing: PgSpace.s) {
                PgActionButton(label: texts.dialogCancel, kind: .secondary, identifier: "mtu.cancel", height: 50, action: onClose)
                PgActionButton(label: texts.subsEditSave, identifier: "mtu.save", height: 50) {
                    tried = true
                    if let mtu = ConnectionSettings.parseMtu(text) {
                        onSave(mtu)
                    }
                }
            }
        }
        .onChange(of: text) { next in
            tried = false
            if next.count > Self.maxDigits {
                text = String(next.prefix(Self.maxDigits))
            }
        }
        .accessibilityIdentifier("mtu.custom")
    }
}

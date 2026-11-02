import PrivateGatewayCore
import PrivateGatewayTheme
import SwiftUI
import UIKit
import UniformTypeIdentifiers

/// Страница вкладки «Проверка»: сама «Проверка» и ее подстраницы — сеть iPhone и проверка узла.
enum CheckPage: Hashable {
    case main
    case network
    case probe
}

/// «Проверка» (доска 07, CheckPage десктопа): шапка стоит на месте, прокручиваются «Проверить» и «Экспорт отчета» с пояснением,
/// результаты проверки, «Задержка», «Что может мешать», инспектор строками-ссылками и журнал туннеля (новые записи сверху,
/// секреты скрыты). Подстраницы инспектора — те, данные для которых телефон получает сам: сеть iPhone с внешним IP и
/// провайдером и проверка узла (DNS, TCP, TLS). «Соединений и трафика», «До и после VPN» и «Лаборатории» на iPhone нет.
struct CheckView: View {
    /// Место блока «Задержка» — сюда прокручивает плитка «Пинг» главной.
    static let delayAnchor = "check.delay"

    let texts: PgTexts
    var input = CheckInput()
    var findings = CheckFindings()
    /// Сервер в работе — для слова рукопожатия в объяснении задержки.
    var server: ServerItem?
    var networks: [PhoneNetwork] = []
    var log: [TunnelLogEntry] = []
    var about = AboutInfo()
    @Binding var page: CheckPage
    @Binding var probe: ProbeState
    /// Открыли плиткой «Пинг» — прокрутить к «Задержке» (`onFocused` — прокрутили).
    var focusDelay = false
    var onRun: () -> Void = {}
    var onDelay: () -> Void = {}
    var onFocused: () -> Void = {}
    var onMeasure: () -> Void = {}
    var onDescribe: () -> Void = {}
    var onProbe: () -> Void = {}

    @State private var report: ReportFile?
    @State private var reportName = ""
    @State private var exporting = false
    @Environment(\.pgLayers) private var layers

    var body: some View {
        switch page {
        case .network:
            CheckNetworkPage(texts: texts, networks: networks, ip: input.ip, onMeasure: onMeasure, onDescribe: onDescribe) { page = .main }
        case .probe:
            CheckProbePage(texts: texts, probe: $probe, protected: input.state == .connected, onRun: onProbe) { page = .main }
        case .main:
            main
        }
    }

    private var main: some View {
        let rows = CheckRows.of(input, texts)
        let hinder = HinderRows.of(findings.proxy, state: input.state, blocked: input.blocked, texts)
        let delay = input.state == .connected ? findings.delay : nil
        // «Отчет сохранен» — подсказкой слоев окна: в ответ системы на сохранение передаются только слои и текст.
        let toast = layers
        let exported = texts.diagExported
        return VStack(spacing: 0) {
            Text(texts.pageCheckTitle)
                .pgText(PgType.h1)
                .foregroundStyle(Color(PgColor.Text.primary))
                .lineLimit(1)
                .accessibilityAddTraits(.isHeader)
                .frame(maxWidth: .infinity, minHeight: 60, alignment: .leading)
                .padding(.horizontal, PgSpace.windowSideMobile)
            ScrollViewReader { scroll in
                ScrollView {
                    VStack(alignment: .leading, spacing: PgSpace.l) {
                        actions(rows: rows, hinder: hinder, delay: delay)
                        Text(texts.diagNote)
                            .pgText(PgType.caption.with(lineHeight: PgType.body.size * (PgType.body.lineHeight ?? 1.45) / PgType.caption.size))
                            .foregroundStyle(Color(PgColor.Text.muted))
                            .fixedSize(horizontal: false, vertical: true)
                            .padding(.horizontal, PgSpace.xs)
                            .accessibilityIdentifier("check.note")
                        CheckCard(rows: rows, identifier: "check.results")
                        delaySection(delay)
                            .id(Self.delayAnchor)
                        section(texts.diagHinderTitle) {
                            CheckCard(rows: hinder, top: true, identifier: "check.hinder")
                        }
                        inspector
                        journal
                    }
                    .padding(.horizontal, PgSpace.windowSideMobile)
                    .padding(.bottom, PgLayout.scrollBottomPadding)
                }
                .onAppear { focus(scroll) }
                .onChange(of: focusDelay) { _ in focus(scroll) }
            }
        }
        .accessibilityIdentifier("check")
        .fileExporter(isPresented: $exporting, document: report, contentType: .plainText, defaultFilename: reportName) { result in
            if case .success = result {
                Task { @MainActor in toast?.show(exported) }
            }
        }
    }

    /// «Проверить» (стрелка крутится, пока идет проверка; кнопка не гаснет — нажатие проверит еще раз) и «Экспорт отчета»:
    /// две равные кнопки высотой 46.
    private func actions(rows: [CheckRow], hinder: [CheckRow], delay: DelayBreakdown?) -> some View {
        HStack(spacing: PgSpace.s) {
            Button(action: onRun) {
                HStack(spacing: PgSpace.s) {
                    PgSpinningIcon(data: PgIcons.refresh, spinning: input.running, color: Color(PgColor.Surface.solid), size: 18, lineWidth: 2.2)
                    Text(input.running ? texts.diagRunning : texts.diagRun)
                        .pgText(PgType.bodyStrong)
                        .foregroundStyle(Color(PgColor.Surface.solid))
                        .lineLimit(1)
                        .minimumScaleFactor(0.8)
                }
                .padding(.horizontal, PgSpace.m)
                .frame(maxWidth: .infinity, minHeight: 46)
                .background(RoundedRectangle(cornerRadius: PgRadius.button, style: .continuous).fill(Color(PgColor.Accent.base)))
                .pgShadow(PgShadow.accent)
                .contentShape(Rectangle())
            }
            .buttonStyle(PgPressStyle())
            .accessibilityIdentifier("check.run")

            PgActionButton(
                label: texts.diagExport, kind: .secondary, identifier: "check.export", height: 46, style: PgType.body.with(weight: 500),
                radius: PgRadius.button
            ) {
                export(rows: rows, hinder: hinder, delay: delay)
            }
        }
    }

    /// «Задержка» (решение 509): рубрика с «Обновить», три числа, почему чисел нет, откуда число нового соединения, когда
    /// обновлено. Под защитой замер входит в проверку: пока она идет, здесь уже «Меряем…».
    private func delaySection(_ delay: DelayBreakdown?) -> some View {
        let measuring = findings.measuringDelay || (input.running && input.state == .connected)
        return VStack(alignment: .leading, spacing: PgSpace.s) {
            HStack(spacing: PgSpace.s) {
                Overline(text: texts.diagDelayTitle)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .accessibilityAddTraits(.isHeader)
                Button(action: onDelay) {
                    Text(texts.diagDelayRefresh)
                        .pgText(PgType.small.with(weight: PgType.bodyStrong.weight))
                        .foregroundStyle(Color(measuring ? PgColor.Text.muted : PgColor.Accent.text))
                        .padding(.vertical, PgSpace.xxs)
                        .contentShape(Rectangle())
                }
                .buttonStyle(PgPressStyle())
                .disabled(measuring)
                .accessibilityIdentifier("check.delay.refresh")
            }
            .padding(.horizontal, PgSpace.xs)
            .padding(.top, PgSpace.xs)

            DelayCard(
                rows: DelayLook.rows(delay, texts), hint: DelayLook.hint(input.state, measuring: measuring, texts),
                why: measuring ? nil : DelayLook.why(delay, server: server, texts), updated: DelayLook.updated(delay, measuring: measuring, texts)
            )
        }
    }

    /// Сетевой инспектор строками-ссылками: сеть iPhone (сети, внешний IP и провайдер) и проверка узла (DNS, TCP, TLS).
    private var inspector: some View {
        VStack(spacing: 0) {
            PgLinkRow(
                icon: PgIcons.netEthernet, title: texts.iosInspectTitle,
                caption: [texts.networkAdapters, texts.networkPublic, texts.networkPublicProvider].joined(separator: " · "), large: true,
                identifier: "check.network"
            ) { page = .network }
            PgDivider().padding(.horizontal, PgSpace.card)
            PgLinkRow(
                icon: PgIcons.pulse, title: texts.diagProbeTitle,
                caption: ProbeKind.allCases.compactMap { texts.text(key: TextKeys.probeKind($0)) }.joined(separator: ", "), large: true,
                identifier: "check.probe"
            ) { page = .probe }
        }
        .padding(.vertical, PgSpace.xxs)
        .pgCard(padding: 0)
        .accessibilityIdentifier("check.inspector")
    }

    /// «Журнал · секреты скрыты»: записи туннеля, новые сверху.
    private var journal: some View {
        VStack(alignment: .leading, spacing: PgSpace.s) {
            HStack(alignment: .lastTextBaseline, spacing: PgSpace.s) {
                Overline(text: texts.diagJournal)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .accessibilityAddTraits(.isHeader)
                Text(texts.diagJournalMasked).pgText(PgType.caption).foregroundStyle(Color(PgColor.Text.muted))
            }
            .padding(.horizontal, PgSpace.xs)
            JournalCard(lines: JournalLook.lines(log), empty: texts.diagJournalEmpty)
        }
    }

    /// Рубрика над карточкой (.sec-head): надзаголовок в полях 6, до карточки 8.
    private func section<Content: View>(_ title: String, @ViewBuilder content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: PgSpace.s) {
            Overline(text: title)
                .padding(.horizontal, PgSpace.xs)
                .padding(.top, PgSpace.xs)
                .accessibilityAddTraits(.isHeader)
            content()
        }
    }

    /// Прокрутить к «Задержке», если экран открыла плитка «Пинг».
    private func focus(_ scroll: ScrollViewProxy) {
        guard focusDelay else {
            return
        }

        scroll.scrollTo(Self.delayAnchor, anchor: .top)
        onFocused()
    }

    /// «Экспорт отчета»: обезличенный отчет (`CheckReport`) файлом — iOS спрашивает, куда сохранить; сохранили — подсказка
    /// «Отчет сохранен».
    private func export(rows: [CheckRow], hinder: [CheckRow], delay: DelayBreakdown?) {
        let now = Date()
        let state = StateLook.of(input.state, texts, blocked: input.blocked, leak: input.leak).power
        let real = [CheckRows.real(input), input.direct].compactMap { $0 }
        let content = CheckReportInput(
            rows: rows, delay: DelayLook.rows(delay, texts), why: DelayLook.why(delay, server: server, texts), hinder: hinder, networks: networks,
            journal: log, about: about, state: state, real: real, now: now
        )
        reportName = CheckReport.fileName(now)
        report = ReportFile(text: CheckReport.text(content, texts))
        exporting = true
    }
}

/// Карточка строк проверки (.check-card): поля 4 · 16, строки разделены полосой, у последней ее нет.
struct CheckCard: View {
    let rows: [CheckRow]
    /// Заголовок переносится, кружок — у первой строки (.check-row.top): строки «Что может мешать».
    var top = false
    var identifier = ""

    var body: some View {
        VStack(spacing: 0) {
            ForEach(Array(rows.enumerated()), id: \.offset) { index, row in
                CheckRowView(row: row, top: top)
                if index < rows.count - 1 {
                    PgDivider()
                }
            }
        }
        .padding(.vertical, PgSpace.xxs)
        .padding(.horizontal, PgSpace.card)
        .pgCard(padding: 0)
        .accessibilityIdentifier(identifier)
    }
}

/// Строка проверки (.check-row): кружок 22 цвета итога со значком, заголовок и пояснение столбцом, справа чип-плашка — фон
/// tint, текст цвета итога, overline 600, скругление 10. Цвета — те же, что у плиток главной.
struct CheckRowView: View {
    let row: CheckRow
    var top = false

    var body: some View {
        HStack(alignment: top ? .top : .center, spacing: PgSpace.l) {
            ZStack {
                Circle().fill(Color(row.look.base))
                PgIcon(data: row.look.mark, color: Color(PgColor.Surface.solid), size: 12, lineWidth: 3.2)
            }
            .frame(width: 22, height: 22)
            .padding(.top, top ? 1 : 0)
            .accessibilityHidden(true)

            VStack(alignment: .leading, spacing: 2) {
                Text(row.title)
                    .pgText(PgType.bodyStrong)
                    .foregroundStyle(Color(PgColor.Text.primary))
                    .lineLimit(top ? nil : 1)
                    .fixedSize(horizontal: false, vertical: top)
                Text(row.detail)
                    .pgText(PgType.caption)
                    .foregroundStyle(Color(PgColor.Text.muted))
                    .fixedSize(horizontal: false, vertical: true)
            }
            .frame(maxWidth: .infinity, alignment: .leading)

            Text(row.chip)
                .pgText(PgTextStyle(size: PgType.overline.size, weight: 600))
                .foregroundStyle(Color(row.look.text))
                .lineLimit(1)
                .fixedSize()
                .padding(.horizontal, 9)
                .padding(.vertical, 3)
                .background(RoundedRectangle(cornerRadius: PgRadius.input, style: .continuous).fill(Color(row.look.tint)))
        }
        .padding(.vertical, 11)
        .accessibilityElement(children: .combine)
        .accessibilityIdentifier("check.row.\(row.key)")
    }
}

/// Карточка «Задержки»: строки «что — пояснение — число моноширинным» с полосой под каждой, ниже — пояснения и «Обновлено».
private struct DelayCard: View {
    let rows: [DelayRow]
    let hint: String?
    let why: String?
    let updated: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            ForEach(Array(rows.enumerated()), id: \.offset) { index, row in
                HStack(spacing: PgSpace.l) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text(row.title).pgText(PgType.bodyStrong).foregroundStyle(Color(PgColor.Text.primary))
                        Text(row.text).pgText(PgType.caption).foregroundStyle(Color(PgColor.Text.muted)).fixedSize(horizontal: false, vertical: true)
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)

                    Text(row.value)
                        .pgText(PgTextStyle(size: PgType.title.size, weight: PgType.title.weight, mono: true))
                        .foregroundStyle(Color(PgColor.Text.primary))
                        .lineLimit(1)
                        .fixedSize()
                        .accessibilityIdentifier("check.delay.\(index)")
                }
                .padding(.vertical, 11)
                .accessibilityElement(children: .combine)
                PgDivider()
            }

            ForEach([hint, why].compactMap { $0 }, id: \.self) { note in
                Text(note)
                    .pgText(PgType.caption)
                    .foregroundStyle(Color(PgColor.Text.muted))
                    .fixedSize(horizontal: false, vertical: true)
                    .padding(.top, PgSpace.m)
                    .padding(.bottom, PgSpace.xxs)
            }

            Text(updated ?? "")
                .pgText(PgType.caption)
                .foregroundStyle(Color(PgColor.Text.muted))
                .frame(minHeight: 15)
                .padding(.top, PgSpace.xxs)
                .padding(.bottom, PgSpace.m)
                .accessibilityIdentifier("check.delay.updated")
        }
        .padding(.vertical, PgSpace.xxs)
        .padding(.horizontal, PgSpace.card)
        .pgCard(padding: 0)
        .accessibilityIdentifier("check.delay")
    }
}

/// Журнал (.card.log): моноширинный 11, строка не переносится — многоточие; время серым, уровень цветом и полужирным.
private struct JournalCard: View {
    let lines: [JournalLine]
    let empty: String

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            if lines.isEmpty {
                Text(empty).pgText(PgType.caption).foregroundStyle(Color(PgColor.Text.muted))
            } else {
                ForEach(Array(lines.enumerated()), id: \.offset) { _, line in
                    (Text(line.time).foregroundColor(Color(PgColor.Text.muted))
                        + Text("  " + line.level.rawValue).fontWeight(.semibold).foregroundColor(Color(JournalLook.color(line.level)))
                        + Text("  " + line.message).foregroundColor(Color(PgColor.Text.primary)))
                        .pgText(PgTextStyle(size: PgType.overline.size, weight: 400, lineHeight: PgType.body.lineHeight, mono: true))
                        .lineLimit(1)
                        .truncationMode(.tail)
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
            }
        }
        .padding(.vertical, PgSpace.l)
        .padding(.horizontal, 14)
        .pgCard(padding: 0)
        .textSelection(.enabled)
        .accessibilityIdentifier("check.log")
    }
}

/// «Сеть iPhone» (NetworkPage десктопа в пределах iOS): что видит iOS, внешний IP с «Кто провайдер» (RIPEstat — только по
/// кнопке) и сети iPhone.
struct CheckNetworkPage: View {
    let texts: PgTexts
    let networks: [PhoneNetwork]
    let ip: PublicIp
    let onMeasure: () -> Void
    let onDescribe: () -> Void
    let onBack: () -> Void

    var body: some View {
        VStack(spacing: 0) {
            PgPageHeader(title: texts.iosInspectTitle, texts: texts, fixed: true, onBack: onBack)
            ScrollView {
                VStack(alignment: .leading, spacing: PgSpace.l) {
                    Text(texts.iosInspectNote)
                        .pgText(PgType.caption)
                        .foregroundStyle(Color(PgColor.Text.muted))
                        .fixedSize(horizontal: false, vertical: true)
                        .padding(.horizontal, PgSpace.xs)
                    VStack(alignment: .leading, spacing: PgSpace.s) {
                        Overline(text: texts.networkPublic).padding(.horizontal, PgSpace.xs).accessibilityAddTraits(.isHeader)
                        PublicIpCard(texts: texts, ip: ip, onMeasure: onMeasure, onDescribe: onDescribe)
                    }
                    Text(texts.networkPublicSource)
                        .pgText(PgType.caption)
                        .foregroundStyle(Color(PgColor.Text.muted))
                        .fixedSize(horizontal: false, vertical: true)
                        .padding(.horizontal, PgSpace.xs)
                    VStack(alignment: .leading, spacing: PgSpace.s) {
                        Overline(text: texts.networkAdapters).padding(.horizontal, PgSpace.xs).accessibilityAddTraits(.isHeader)
                        NetworksCard(texts: texts, networks: networks)
                    }
                }
                .padding(.horizontal, PgSpace.windowSideMobile)
                .padding(.bottom, PgLayout.scrollBottomPadding)
            }
        }
        .accessibilityIdentifier("check.network.page")
    }
}

/// Сети iPhone так, как их видит iOS: вид и имя, по умолчанию ли, дорогая ли, есть ли выход в интернет, что умеет.
private struct NetworksCard: View {
    let texts: PgTexts
    let networks: [PhoneNetwork]

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            if networks.isEmpty {
                Text(texts.iosInspectNone).pgText(PgType.body).foregroundStyle(Color(PgColor.Text.muted))
            }

            ForEach(Array(networks.enumerated()), id: \.offset) { index, network in
                VStack(alignment: .leading, spacing: 2) {
                    Text(PhoneNetworkText.title(network, texts)).pgText(PgType.bodyStrong).foregroundStyle(Color(PgColor.Text.primary))
                    let marks = PhoneNetworkText.marks(network, texts)
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
                .padding(.vertical, PgSpace.m)
                .frame(maxWidth: .infinity, alignment: .leading)
                .accessibilityIdentifier("inspect.network.\(network.name ?? network.kind.rawValue)")
                if index < networks.count - 1 {
                    PgDivider()
                }
            }
        }
        .padding(.vertical, networks.isEmpty ? PgSpace.l : PgSpace.xxs)
        .padding(.horizontal, PgSpace.card)
        .pgCard(padding: 0)
        .accessibilityIdentifier("inspect.networks")
    }
}

/// Внешний IP: адрес, через сервер ли он измерен, «Измерить» (стрелка крутится, пока идет замер) и «Кто провайдер» — ссылкой,
/// как у десктопа; ответ RIPEstat — провайдер, сеть и место.
private struct PublicIpCard: View {
    let texts: PgTexts
    let ip: PublicIp
    let onMeasure: () -> Void
    let onDescribe: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: PgSpace.xs) {
            if let address = ip.address {
                Text(address).pgText(PgType.mono.with(weight: 600)).foregroundStyle(Color(PgColor.Text.primary)).accessibilityIdentifier("inspect.ip.address")
                Text(ip.viaServer ? texts.networkPublicExit : texts.networkPublicDirect).pgText(PgType.small).foregroundStyle(Color(PgColor.Text.secondary))
            } else if ip.failed {
                Text(texts.networkPublicUnreachable).pgText(PgType.small).foregroundStyle(Color(PgColor.Status.Warning.text))
            } else if !ip.measuring {
                Text(texts.networkPublicNone).pgText(PgType.small).foregroundStyle(Color(PgColor.Text.muted))
            }

            if let info = ip.info {
                provider(info)
            }

            HStack(spacing: PgSpace.l) {
                PgSmallButton(
                    label: ip.measuring ? texts.networkPublicMeasuring : texts.networkPublicMeasure, icon: PgIcons.refresh, spinning: ip.measuring,
                    identifier: "inspect.ip.measure", action: onMeasure
                )
                if ip.address != nil {
                    PgTextAction(
                        label: ip.describing ? texts.networkPublicAsking : texts.networkPublicDescribe, enabled: !ip.describing,
                        identifier: "inspect.ip.describe", action: onDescribe
                    )
                }
            }
            .padding(.top, PgSpace.xs)
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

/// «Проверка узла» (ProbePage десктопа в пределах iOS): вид замера сегментами — DNS, TCP, TLS, — как он идет, узел, порт (у
/// TCP и TLS), путь замера, «Проверить», итог и ход по шагам, у TLS — сведения о сертификате. Пинга и пути нет: сырых сокетов
/// у приложения нет (решение 467).
struct CheckProbePage: View {
    let texts: PgTexts
    @Binding var probe: ProbeState
    let protected: Bool
    let onRun: () -> Void
    let onBack: () -> Void

    var body: some View {
        VStack(spacing: 0) {
            PgPageHeader(title: texts.pageProbeTitle, texts: texts, fixed: true, onBack: onBack)
            ScrollView {
                ProbeCard(texts: texts, probe: $probe, protected: protected, onRun: onRun)
                    .padding(.horizontal, PgSpace.windowSideMobile)
                    .padding(.bottom, PgLayout.scrollBottomPadding)
            }
        }
        .accessibilityIdentifier("check.probe.page")
    }
}

/// Замер до узла: вид сегментами, пояснение, поля, путь замера, «Проверить», итог.
private struct ProbeCard: View {
    let texts: PgTexts
    @Binding var probe: ProbeState
    let protected: Bool
    let onRun: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: PgSpace.xs) {
            Overline(text: texts.probeKind)
            PgSegments(
                options: ProbeKind.allCases.map { PgOption(value: $0, title: texts.text(key: TextKeys.probeKind($0)) ?? $0.rawValue) }, selection: probe.kind,
                identifier: "inspect.probe.kind",
                onSelect: { kind in
                    probe.kind = kind
                    probe.error = nil
                }
            )
            .padding(.top, PgSpace.xxs)

            Text(texts.text(key: TextKeys.probeMethod(probe.kind)) ?? "")
                .pgText(PgType.small)
                .foregroundStyle(Color(PgColor.Text.secondary))
                .fixedSize(horizontal: false, vertical: true)
                .padding(.top, PgSpace.xs)
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

import PrivateGatewayCore
import PrivateGatewayTheme
import SwiftUI
import UIKit
import UniformTypeIdentifiers
import XCTest
@testable import PrivateGateway

/// Вызовы, которые записывает тест: модель «Проверки» зовет их со своего потока (главного).
@MainActor
private final class Calls {
    var names: [String] = []
}

/// «Проверка» по доске 7: строки результатов по состоянию защиты, как у десктопа (тексты diag.*), «Что может мешать» по
/// настройкам прокси iOS, «Задержка», журнал новыми сверху, обезличенный отчет и порядок работы модели проверки.
final class CheckTests: XCTestCase {
    private let texts = PgTexts(.russian)
    private let utc = TimeZone(identifier: "UTC")!
    private let at = Date(timeIntervalSince1970: 1_793_029_320)

    private let known = PublicIp(address: "203.0.113.24", viaServer: true, info: AddressInfo(
        address: "203.0.113.24", asn: 64500, holder: "EXAMPLE-AS", prefix: "203.0.113.0/24", country: "NL", city: "Amsterdam", failure: nil
    ))

    @MainActor
    func testProtectedRowsAreAllOkInTheBoardOrder() {
        let rows = CheckRows.of(CheckInput(state: .connected, system: .connected, killSwitch: true, core: "26.3.27", ip: known, direct: "198.51.100.7"), texts)
        XCTAssertEqual(rows.map(\.key), ["tunnel", "core", "all_traffic", "kill_switch", "dns", "ipv6", "exit_ip", "other_vpn"])
        XCTAssertEqual(rows.map(\.outcome), Array(repeating: CheckOutcome.ok, count: 8))
        XCTAssertEqual(rows.map(\.title), [
            texts.networkKindTunnel, texts.diagCoreTitle, texts.diagAllTrafficTitle, texts.diagKillSwitchTitle, texts.diagDnsTitle, texts.diagIpv6Title,
            texts.diagExitIpTitle, texts.diagOtherVpnTitle,
        ])
        XCTAssertEqual(rows.map(\.detail), [
            texts.diagStateOkText, texts.diagCoreOk("Xray-core 26.3.27"), texts.diagAllTrafficOk, texts.diagKillSwitchOk("includeAllNetworks"),
            texts.diagDnsOk, texts.diagIpv6Ok, "203.0.113.24 · Amsterdam", texts.diagOtherVpnOk,
        ])
        XCTAssertEqual(rows.map(\.chip), [
            texts.diagStateOkChip, texts.diagStateOkChip, texts.diagStateOkChip, texts.diagStateOkChip, texts.diagDnsOkChip, texts.diagIpv6OkChip,
            texts.diagExitIpOkChip, texts.diagOtherVpnOkChip,
        ])
        XCTAssertEqual(rows.map(\.look), Array(repeating: ChipLook.ok, count: 8), "цвета — как у плиток главной")
        XCTAssertEqual(rows[1].detail, "Xray-core 26.3.27 запущен, конфигурация применена")
        XCTAssertEqual(rows[3].detail, "Правила брандмауэра активны: includeAllNetworks", "слово ОС макета для iOS")
    }

    @MainActor
    func testWhileCheckingEveryRowWaits() {
        let rows = CheckRows.of(CheckInput(state: .connected, system: .connected, ip: known, running: true), texts)
        XCTAssertEqual(rows.count, CheckId.allCases.count)
        XCTAssertTrue(rows.allSatisfy { $0.outcome == .waiting && $0.detail == texts.diagChecking && $0.chip == texts.diagStateWaitingChip })
        XCTAssertEqual(rows.first?.look.mark, PgIcons.chipDots)
    }

    @MainActor
    func testBlockedStopsTrafficWithoutALeak() {
        let input = CheckInput(state: .error, system: .connected, error: TunnelErrors.serverNotResponding, blocked: true, killSwitch: true, ip: known)
        let rows = CheckRows.of(input, texts)
        XCTAssertEqual(rows.map(\.outcome), [.stopped, .stopped, .stopped, .ok, .ok, .ok, .stopped, .ok])
        // Ядро в «Блокировке» iOS остановлено (туннель оставлен без него) — так и сказано, а не «Запущено».
        XCTAssertEqual(rows[1].detail, texts.diagCoreStopped + " · " + texts.statusBlockedSub)
        XCTAssertEqual(rows[1].chip, texts.diagCoreNoReplyChip)
        XCTAssertEqual(rows[2].detail, texts.diagAllTrafficStopped)
        XCTAssertEqual(rows[2].chip, texts.diagStateStoppedChip)
        XCTAssertEqual(rows[6].detail, texts.diagExitIpStopped)
        XCTAssertEqual(rows[6].chip, texts.diagExitIpStoppedChip)
        XCTAssertEqual(rows[2].look, .stopped, "оранжевый — утечки нет")
    }

    @MainActor
    func testOffShowsTheRealIpAndNothingProtected() {
        let rows = CheckRows.of(CheckInput(state: .off, system: .disconnected, ip: PublicIp(address: "198.51.100.7")), texts)
        XCTAssertEqual(rows.map(\.outcome), [.off, .off, .off, .off, .off, .off, .off, .ok])
        XCTAssertEqual(rows[1].detail, texts.diagCoreOff)
        XCTAssertEqual(rows[2].chip, texts.diagAllTrafficOffChip)
        XCTAssertEqual(rows[3].detail, texts.diagKillSwitchOff)
        XCTAssertEqual(rows[4].chip, texts.diagDnsOffChip)
        XCTAssertEqual(rows[5].chip, texts.diagIpv6OffChip)
        XCTAssertEqual(rows[6].detail, texts.diagExitIpOff("198.51.100.7"))
        XCTAssertEqual(rows[6].chip, texts.diagExitIpOffChip)
        XCTAssertEqual(CheckRows.row(.exitIp, CheckInput(), texts).detail, texts.diagExitIpOff(CheckRows.hidden), "реальный IP не измерен — звездочки")

        // «Ошибка» без туннеля — трафик напрямую, код ошибки — в строке ядра (карточки ошибки нет).
        let failed = CheckRows.of(CheckInput(state: .error, system: .disconnected, error: TunnelErrors.coreFailed), texts)
        XCTAssertEqual(failed[1].outcome, .stopped)
        XCTAssertEqual(failed[1].detail, texts.diagCoreStopped + " · " + TunnelErrors.coreFailed)
        XCTAssertEqual(failed[2].outcome, .off)
    }

    @MainActor
    func testConnectingWaitsAndTheKillSwitchFollowsTheSetting() {
        let rows = CheckRows.of(CheckInput(state: .connecting, system: .connecting, killSwitch: false), texts)
        XCTAssertEqual(rows.map(\.outcome), [.waiting, .waiting, .waiting, .off, .ok, .ok, .waiting, .ok])
        XCTAssertEqual(rows[1].detail, texts.diagCoreWaiting)
        XCTAssertEqual(rows[6].detail, texts.diagExitIpWaiting)
        XCTAssertEqual(CheckRows.row(.killSwitch, CheckInput(state: .reconnecting, killSwitch: true), texts).outcome, .ok)
    }

    @MainActor
    func testExitIpAndLeakUnderProtection() {
        let leak = CheckRows.of(CheckInput(state: .connected, system: .connected, leak: true, ip: known), texts)
        XCTAssertEqual(leak[2].outcome, .warning)
        XCTAssertEqual(leak[2].chip, texts.diagAllTrafficSameIpChip)
        XCTAssertEqual(leak[6].detail, texts.diagExitIpSameIp)
        XCTAssertEqual(leak[6].chip, texts.diagExitIpSameIpChip)

        let measuring = CheckRows.row(.exitIp, CheckInput(state: .connected, ip: PublicIp(measuring: true)), texts)
        XCTAssertEqual(measuring.outcome, .waiting)
        XCTAssertEqual(measuring.detail, texts.diagChecking)
        let silent = CheckRows.row(.exitIp, CheckInput(state: .connected, ip: PublicIp(failed: true)), texts)
        XCTAssertEqual(silent.outcome, .warning)
        XCTAssertEqual(silent.detail, texts.diagExitIpNoAnswer)
        XCTAssertEqual(silent.chip, texts.diagExitIpNoAnswerChip)
        XCTAssertEqual(CheckRows.row(.exitIp, CheckInput(state: .connected, ip: PublicIp(address: "198.51.100.7")), texts).outcome, .waiting,
                       "адрес без защиты — еще не адрес выхода")
    }

    @MainActor
    func testOtherVpnAndProxy() {
        let proxy = CheckRows.row(.otherVpn, CheckInput(state: .connected, proxy: ProxyFindings(http: "192.0.2.5:3128")), texts)
        XCTAssertEqual(proxy.outcome, .warning)
        XCTAssertEqual(proxy.detail, texts.diagOtherVpnSystemProxy)
        XCTAssertEqual(proxy.chip, texts.diagStateWarningChip)
        XCTAssertEqual(proxy.look.mark, PgIcons.chipWarn)

        let vpn = CheckRows.row(.otherVpn, CheckInput(state: .off, proxy: ProxyFindings(vpn: ["utun3"])), texts)
        XCTAssertEqual(vpn.outcome, .warning)
        XCTAssertEqual(vpn.detail, texts.diagOtherVpnVpnAdapter)
        XCTAssertEqual(CheckRows.row(.otherVpn, CheckInput(state: .connected, proxy: ProxyFindings(vpn: ["utun3"])), texts).outcome, .ok,
                       "под защитой интерфейс VPN — свой туннель")
    }

    @MainActor
    func testHindrancesAreWhatTheIphoneShows() {
        let none = HinderRows.of(ProxyFindings(), state: .off, blocked: false, texts)
        XCTAssertEqual(none.map(\.key), ["hinder.none"])
        XCTAssertEqual(none.first?.title, texts.diagHinderNone)
        XCTAssertEqual(none.first?.chip, texts.diagHinderChipNone)
        XCTAssertEqual(none.first?.outcome, .ok)

        let found = HinderRows.of(ProxyFindings(http: "192.0.2.5:3128", pac: "https://example.com/[скрыто]", vpn: ["utun3"]), state: .off, blocked: false, texts)
        XCTAssertEqual(found.map(\.key), ["hinder.http", "hinder.pac", "hinder.vpn"])
        XCTAssertTrue(found[0].detail.hasPrefix(texts.diagHinderValue("192.0.2.5:3128")))
        XCTAssertEqual(found[0].chip, texts.diagHinderChipSet)
        XCTAssertEqual(found[2].chip, texts.diagHinderChipLive)
        XCTAssertTrue(found.allSatisfy { $0.outcome == .warning })
        XCTAssertEqual(HinderRows.of(ProxyFindings(vpn: ["utun3"]), state: .connected, blocked: false, texts).map(\.key), ["hinder.none"])
        XCTAssertEqual(HinderRows.of(ProxyFindings(vpn: ["utun3"]), state: .error, blocked: true, texts).map(\.key), ["hinder.none"])
    }

    func testSystemProxySettingsAreRead() {
        let settings: [String: Any] = [
            "HTTPEnable": 1, "HTTPProxy": "192.0.2.5", "HTTPPort": 3128, "ProxyAutoConfigEnable": 1,
            "ProxyAutoConfigURLString": "https://example.com/proxy/0123456789abcdef.pac",
            "__SCOPED__": ["en0": [String: Any](), "utun3": [String: Any](), "ipsec0": [String: Any]()],
        ]
        let found = SystemProxy.parse(settings)
        XCTAssertEqual(found.http, "192.0.2.5:3128")
        XCTAssertEqual(found.pac, "https://example.com/" + SecretMasker.hidden, "путь скрипта — как адрес подписки: в нем бывает токен")
        XCTAssertEqual(found.vpn, ["ipsec0", "utun3"])
        XCTAssertTrue(found.proxy)

        XCTAssertEqual(SystemProxy.parse([:]), ProxyFindings())
        XCTAssertNil(SystemProxy.parse(["HTTPEnable": 0, "HTTPProxy": "192.0.2.5", "HTTPPort": 3128]).http, "выключенный прокси — не в счет")
        XCTAssertEqual(SystemProxy.parse(["HTTPSEnable": 1, "HTTPSProxy": "2001:db8::5", "HTTPSPort": 8080]).http, "[2001:db8::5]:8080")
    }

    @MainActor
    func testDelayNumbersAndWhy() {
        XCTAssertEqual(DelayLook.rows(nil, texts).map(\.value), ["—", "—", "—"])
        XCTAssertEqual(DelayLook.rows(nil, texts).map(\.title), [texts.diagDelayToServer, texts.diagDelayThrough, texts.diagDelayFresh])

        let delay = DelayBreakdown(toServer: 38, through: 42, fresh: 160, measuredAt: at.addingTimeInterval(12))
        XCTAssertEqual(DelayLook.rows(delay, texts).map(\.value), [texts.diagDelayMs(38), texts.diagDelayMs(42), texts.diagDelayMs(160)])
        XCTAssertEqual(delay.rounds, 4.2)
        let reality = ServerItem(id: "a", linkName: "NL", countryCode: "NL", protocol: .vless, security: .reality, transport: .tcp, port: 443)
        XCTAssertEqual(DelayLook.why(delay, server: reality, texts), texts.diagDelayWhy(160, 4, texts.diagDelayRoundsFew, "REALITY", 38))
        var tls = reality
        tls.security = .tls
        XCTAssertEqual(DelayLook.handshake(tls, texts), "TLS")
        XCTAssertEqual(DelayLook.handshake(nil, texts), texts.diagDelayHandshakeProtocol)
        XCTAssertNil(DelayLook.why(DelayBreakdown(toServer: nil, through: 42, fresh: 160, measuredAt: at), server: reality, texts), "без «До сервера» кругов не счесть")

        XCTAssertEqual(DelayLook.hint(.connected, measuring: true, texts), texts.diagDelayMeasuring)
        XCTAssertNil(DelayLook.hint(.connected, measuring: false, texts))
        XCTAssertEqual(DelayLook.hint(.off, measuring: false, texts), texts.diagDelayOff)
        XCTAssertEqual(DelayLook.updated(delay, measuring: false, texts, timeZone: utc), texts.diagDelayUpdated("15:42:12"))
        XCTAssertNil(DelayLook.updated(delay, measuring: true, texts, timeZone: utc))
    }

    @MainActor
    func testJournalIsNewestFirstWithLevels() {
        let entries = (0..<60).map { index in
            TunnelLogEntry(time: at.addingTimeInterval(Double(index)), level: index % 2 == 0 ? .info : .warning, message: "event \(index)")
        }
        let lines = JournalLook.lines(entries, timeZone: utc)
        XCTAssertEqual(lines.count, JournalLook.shown)
        XCTAssertEqual(lines.first, JournalLine(time: "15:42:59", level: .warning, message: "event 59"), "новые сверху, время телефона")
        XCTAssertEqual(lines.last?.message, "event 10")
        let secret = TunnelLogEntry(time: nil, level: .error, message: "vless://11111111-2222-3333-4444-555555555555@203.0.113.10:443")
        XCTAssertEqual(JournalLook.lines([secret]).first?.message, "vless://" + SecretMasker.hidden)
        XCTAssertEqual(JournalLook.color(.info), PgColor.Accent.text)
        XCTAssertEqual(JournalLook.color(.warning), PgColor.Status.Warning.text)
        XCTAssertEqual(JournalLook.color(.error), PgColor.Status.Danger.text)
        XCTAssertEqual(JournalLook.color(.debug), PgColor.Text.muted)
    }

    func testAnonymizerKeepsTheShapeAndHidesTheAddresses() {
        let anonymizer = ReportAnonymizer(label: "адрес")
        XCTAssertEqual(anonymizer.clean("exit 203.0.113.24, again 203.0.113.24, other 198.51.100.7"), "exit [адрес-1], again [адрес-1], other [адрес-2]")
        XCTAssertEqual(anonymizer.clean("lan 192.168.1.10 tun 10.0.0.2 dns 1.1.1.1 route 0.0.0.0/1"), "lan 192.168.1.10 tun 10.0.0.2 dns 1.1.1.1 route 0.0.0.0/1")
        XCTAssertEqual(anonymizer.clean("net 203.0.113.0/24 wide 203.0.0.0/8"), "net [адрес-3]/24 wide 203.0.0.0/8")
        XCTAssertEqual(anonymizer.clean("v6 2001:db8::10 loop ::1 dns 2606:4700:4700::1111"), "v6 [адрес-4] loop ::1 dns 2606:4700:4700::1111")
        XCTAssertEqual(anonymizer.clean("mac aa:bb:cc:dd:ee:ff"), "mac [MAC]")
        XCTAssertEqual(anonymizer.clean("15:42:12 INF connected"), "15:42:12 INF connected", "время — не адрес")
        XCTAssertEqual(anonymizer.clean("vless://11111111-2222-3333-4444-555555555555@203.0.113.10:443"), "vless://" + SecretMasker.hidden)
        XCTAssertEqual(anonymizer.clean("again 203.0.113.24"), "again [адрес-1]", "одна метка на весь отчет")
    }

    @MainActor
    func testReportIsAnonymousAndHasNoRealIp() {
        let input = CheckInput(state: .off, system: .disconnected, ip: PublicIp(address: "198.51.100.7"), direct: "198.51.100.7")
        let rows = CheckRows.of(input, texts)
        let hinder = HinderRows.of(ProxyFindings(http: "203.0.113.5:3128"), state: .off, blocked: false, texts)
        let journal = [
            TunnelLogEntry(time: at, level: .error, message: "gave up: PGC-NET-001"), TunnelLogEntry(time: at.addingTimeInterval(5), level: .info, message: "connected"),
        ]
        let content = CheckReportInput(
            rows: rows, delay: DelayLook.rows(nil, texts), hinder: hinder, networks: [PhoneNetwork(kind: .wifi, name: "en0", isDefault: true)],
            journal: journal, about: AboutInfo(app: "0.1.0", system: "iOS 18.1", arch: "arm64", core: "26.3.27"), state: texts.powerLabelOff,
            real: ["198.51.100.7"], now: at
        )
        let report = CheckReport.text(content, texts, timeZone: utc)
        XCTAssertFalse(report.contains("198.51.100.7"), "реального IP в отчете нет даже меткой")
        XCTAssertTrue(report.hasPrefix(texts.diagReportTitle("0.1.0") + "\n"))
        XCTAssertTrue(report.contains("iOS 18.1 · arm64 · Xray-core 26.3.27"))
        XCTAssertTrue(report.contains(texts.diagReportCheckedAt("2026-10-26 15:42:00 +00:00")))
        XCTAssertTrue(report.contains("[\(texts.diagExitIpOffChip)] \(texts.diagExitIpTitle) — \(texts.diagExitIpOff(CheckRows.hidden))"))
        XCTAssertTrue(report.contains("[адрес-1]:3128"), "публичный адрес прокси — меткой")
        XCTAssertFalse(report.contains("203.0.113.5"))
        XCTAssertTrue(report.contains(texts.iosInspectTitle))
        XCTAssertTrue(report.contains(texts.diagReportAnonymous))
        let newest = report.range(of: "INF  connected")
        let oldest = report.range(of: "ERR  gave up: PGC-NET-001")
        XCTAssertNotNil(newest)
        XCTAssertNotNil(oldest)
        if let newest, let oldest {
            XCTAssertLessThan(newest.lowerBound, oldest.lowerBound, "журнал — новые сверху")
        }

        XCTAssertEqual(CheckReport.fileName(at, timeZone: utc), "private-gateway-report-20261026-1542")
        let file = ReportFile(text: report)
        XCTAssertEqual(ReportFile.readableContentTypes, [.plainText])
        XCTAssertEqual(file.text, report)
    }

    @MainActor
    func testRunReadsTheProxyAndTheIpThenMeasuresTheDelay() async {
        let calls = Calls()
        let model = CheckModel(
            through: {
                calls.names.append("through")
                return 42
            },
            fresh: {
                calls.names.append("fresh")
                return 160
            },
            proxy: {
                calls.names.append("proxy")
                return ProxyFindings(http: "192.0.2.5:3128")
            }
        )
        await model.run(
            connected: { true },
            measureIp: {
                XCTAssertTrue(model.findings.running, "пока меряется IP, строки ждут")
                calls.names.append("ip")
            },
            toServer: {
                calls.names.append("server")
                return 38
            }
        )
        XCTAssertEqual(calls.names, ["proxy", "ip", "server", "through", "fresh"])
        XCTAssertFalse(model.findings.running)
        XCTAssertFalse(model.findings.measuringDelay)
        XCTAssertEqual(model.findings.proxy.http, "192.0.2.5:3128")
        XCTAssertEqual(model.findings.delay?.toServer, 38)
        XCTAssertEqual(model.findings.delay?.through, 42)
        XCTAssertEqual(model.findings.delay?.fresh, 160)

        await model.run(connected: { false }, measureIp: {}, toServer: { 38 })
        XCTAssertNil(model.findings.delay, "без защиты задержки нет")
    }

    @MainActor
    func testPressDuringACheckChecksOnceMoreRightAfter() async {
        let calls = Calls()
        let model = CheckModel(through: { nil }, fresh: { nil }, proxy: {
            calls.names.append("proxy")
            return ProxyFindings()
        })
        await model.run(
            connected: { false },
            measureIp: {
                calls.names.append("ip")
                if calls.names.count < 3 {
                    await model.run(connected: { false }, measureIp: { calls.names.append("nested") }, toServer: { nil })
                }
            },
            toServer: { nil }
        )
        XCTAssertEqual(calls.names, ["proxy", "ip", "proxy", "ip"], "второе нажатие — еще одна проверка после текущей, не параллельно")
        XCTAssertFalse(model.findings.running)
    }

    @MainActor
    func testCheckPagesDraw() throws {
        let networks = PhoneNetworks.order([
            PhoneNetwork(kind: .vpn, name: "utun4", isDefault: true, expensive: true, constrained: true, ipv6: true),
            PhoneNetwork(kind: .wifi, name: "en0", isDefault: false),
        ])
        for language in PgTexts.Language.allCases {
            let texts = PgTexts(language)
            for page in [CheckPage.network, .probe] {
                for ip in [PublicIp(), PublicIp(measuring: true), known, PublicIp(address: "192.0.2.1", info: .failed("192.0.2.1", .private))] {
                    let view = CheckView(
                        texts: texts, input: CheckInput(state: .connected, ip: ip), networks: page == .network ? networks : [], page: .constant(page),
                        probe: .constant(ProbeState(kind: .tls, target: "example.com"))
                    )
                    let image = try XCTUnwrap(ImageRenderer(content: view.frame(width: 393, height: 1600)).uiImage)
                    XCTAssertEqual(image.size.width, 393, accuracy: 1)
                }
            }
        }
    }
}

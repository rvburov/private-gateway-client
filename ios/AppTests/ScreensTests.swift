import PrivateGatewayCore
import PrivateGatewayTheme
import SwiftUI
import UIKit
import XCTest
@testable import PrivateGateway

/// Подписка без сети: ответ, который задал тест.
private struct FixedSource: SubscriptionSource {
    let text: String

    func download(_ url: URLComponents) async throws -> SubscriptionContent {
        SubscriptionContent(text: text, title: "Мой VPN")
    }
}

/// Экраны «Серверы», «Добавить», карточка и островок на симуляторе; QR туда и обратно по всем эталонным ссылкам; добавление
/// через модель экрана — в общую папку.
final class ScreensTests: XCTestCase {
    private static let testData: URL = {
        var url = URL(fileURLWithPath: #filePath)
        url.deleteLastPathComponent()
        url.deleteLastPathComponent()
        url.deleteLastPathComponent()
        return url.appendingPathComponent("shared/testdata")
    }()

    private static func links() -> [String] {
        let folder = testData.appendingPathComponent("links")
        let files = (FileManager.default.enumerator(atPath: folder.path)?.compactMap { $0 as? String } ?? []).filter { $0.hasSuffix(".txt") }.sorted()
        return files.compactMap { try? String(contentsOf: folder.appendingPathComponent($0), encoding: .utf8) }
            .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
            .filter { !$0.isEmpty }
    }

    private var directory: URL!

    override func setUpWithError() throws {
        directory = FileManager.default.temporaryDirectory.appendingPathComponent("pg-app-\(UUID().uuidString)", isDirectory: true)
    }

    override func tearDownWithError() throws {
        if FileManager.default.fileExists(atPath: directory.path) {
            try FileManager.default.removeItem(at: directory)
        }
    }

    private let servers = [
        ServerItem(id: "a", linkName: "Нидерланды 01", countryCode: "NL", protocol: .vless, security: .reality, transport: .tcp, port: 443, subscriptionId: "s",
                   favorite: true, latency: .of(48)),
        ServerItem(id: "b", linkName: "Германия", countryCode: "DE", protocol: .trojan, security: .tls, transport: .webSocket, port: 443, latency: .of(180),
                   missingSince: Date()),
        ServerItem(id: "c", linkName: "Финляндия", countryCode: "", protocol: .hysteria2, security: .tls, transport: .quic, port: 8443,
                   latency: .failed(.noResponse)),
    ]

    @MainActor
    private func render(_ view: some View, height: CGFloat = 852) throws -> UIImage {
        try XCTUnwrap(ImageRenderer(content: view.frame(width: 393, height: height)).uiImage)
    }

    @MainActor
    func testQrCodeGoesThereAndBackForEveryFixtureLink() throws {
        let links = Self.links() + ["https://example.com/sub/0123456789abcdef0123456789abcdef?flag=1"]
        XCTAssertGreaterThanOrEqual(links.count, 15, "эталонов слишком мало")
        for link in links {
            let image = try XCTUnwrap(QrCode.image(link), link)
            let cg = try XCTUnwrap(image.cgImage)
            XCTAssertEqual(QrCode.read(cg), link)
        }
    }

    @MainActor
    func testPictureWithoutACodeIsNil() throws {
        let blank = UIGraphicsImageRenderer(size: CGSize(width: 64, height: 64)).image { context in
            UIColor.white.setFill()
            context.fill(CGRect(x: 0, y: 0, width: 64, height: 64))
        }
        XCTAssertNil(try QrCode.read(try XCTUnwrap(blank.pngData())))
        XCTAssertThrowsError(try QrCode.read(Data("not an image".utf8)))
    }

    @MainActor
    func testServersDrawInEveryTabAndLanguage() throws {
        let subscriptions = [SubscriptionRef(id: "s", name: "Работа", updatedAt: Date())]
        for language in PgTexts.Language.allCases {
            for tab in [ServerListTab.all, .favorites, .recent] {
                let home = HomeState(status: TunnelStatus(state: .connected, serverId: "a"), servers: servers, selectedId: "b", subscriptions: subscriptions)
                let view = ServersView(texts: PgTexts(language), home: home, query: .constant(ServerListQuery(tab: tab)))
                XCTAssertEqual(try render(view).size.width, 393, accuracy: 1)
            }

            let empty = ServersView(texts: PgTexts(language), home: HomeState(), query: .constant(ServerListQuery()))
            XCTAssertNotNil(try render(empty))
        }
    }

    @MainActor
    func testCardAndIslandDraw() throws {
        let texts = PgTexts(.russian)
        let home = HomeState(status: TunnelStatus(state: .connected, serverId: "a"), servers: servers, selectedId: "a")
        for id in ["a", "b", "c"] {
            XCTAssertNotNil(try render(ServerCardView(texts: texts, home: home, id: id, actions: CardActions())))
        }

        for state in TunnelState.allCases {
            for tab in [AppTab.home, .servers, .check, .settings] {
                let island = Island(texts: texts, tab: tab, state: state, hasServer: state != .off, onTab: { _ in }, onPower: {})
                XCTAssertNotNil(try render(island, height: 160))
            }
        }
    }

    @MainActor
    func testImportChecksAddsAndSelectsTheFirstServer() async throws {
        let links = Self.links()
        let container = SharedContainer(directory: directory, vault: MemoryVault())
        let model = ImportModel(container: container, source: FixedSource(text: links.joined(separator: "\n")))
        var added = 0
        model.onAdded = { added += 1 }

        await model.check("https://example.com/sub/0123456789abcdef")
        let batch = try XCTUnwrap(model.preview?.batch)
        XCTAssertEqual(batch.found, links.count)
        XCTAssertEqual(model.name, "Мой VPN", "название — из Profile-Title")
        XCTAssertNotNil(try render(ImportView(model: model, texts: PgTexts(.russian), onClose: {})))

        await model.add()
        let good = batch.found - batch.failed
        XCTAssertEqual((model.added ?? 0) + model.duplicates, good)
        XCTAssertEqual(added, 1)
        let stored = try await container.servers.servers()
        XCTAssertEqual(stored.count, model.added)
        let selected = try await container.settings.load().selectedServerId
        XCTAssertEqual(selected, stored.first?.id, "первый импорт выбирает первый новый сервер")
        let groups = try await container.servers.groups()
        XCTAssertEqual(groups.map(\.name), ["Мой VPN"])

        await model.add()
        XCTAssertEqual(added, 1, "второе нажатие не считается")
        XCTAssertNotNil(try render(ImportView(model: model, texts: PgTexts(.english), onClose: {})))
    }

    @MainActor
    func testQrFromAPictureIsCheckedLikeText() async throws {
        let link = try XCTUnwrap(Self.links().first)
        let model = ImportModel(container: SharedContainer(directory: directory, vault: MemoryVault()), source: FixedSource(text: ""))
        let png = try XCTUnwrap(QrCode.image(link)?.pngData())
        await model.readQr(png)
        XCTAssertEqual(model.preview?.batch?.found, 1)
        XCTAssertEqual(model.text, "", "в поле код не попадает")

        await model.readQr(Data("not an image".utf8))
        XCTAssertEqual(model.qrProblem, "android.import.qr.failed")
        XCTAssertNotNil(PgTexts(.russian).text(key: try XCTUnwrap(model.qrProblem)))
    }

    @MainActor
    func testSettingsDrawOnEveryPageAndLanguage() throws {
        let about = AboutInfo.of(core: "26.3.27")
        for language in PgTexts.Language.allCases {
            let texts = PgTexts(language)
            for page in [SettingsPage.main, .protection, .licenses] {
                let view = SettingsView(texts: texts, settings: AppSettings(), about: about, page: .constant(page)) { _ in EmptyView() }
                XCTAssertEqual(try render(view).size.width, 393, accuracy: 1)
            }

            let custom = TunnelSettings(dns: .custom, customDns: "192.0.2.53", ipv6: true, allowLan: true)
            let editing = ProtectionView(texts: texts, tunnel: custom, killSwitch: false, editingDns: true, onTunnel: { _ in }, onKillSwitch: { _ in }, onBack: {})
            XCTAssertNotNil(try render(editing))
            for component in about.components {
                XCTAssertNotNil(try render(LicensesView(texts: texts, about: about, open: component.name, onBack: {}), height: 2000), component.name)
            }
        }
    }

    /// Пакет приложения несет список компонентов, тексты всех их лицензий и коммит libXray (ios/project.yml).
    @MainActor
    func testAppCarriesTheLicensesAndVersions() throws {
        let about = AboutInfo.of(core: nil)
        let file = Self.testData.deletingLastPathComponent().appendingPathComponent("licenses/ios.json")
        XCTAssertEqual(about.components, try OpenComponents.parse(String(contentsOf: file, encoding: .utf8)))
        for component in about.components {
            for id in component.licenseIds {
                XCTAssertFalse(about.licenseText(id).isEmpty, "\(component.name): нет текста \(id) в пакете приложения")
            }
        }

        let libXray = try XCTUnwrap(about.components.first { $0.xcframework != nil })
        let commit = try String(contentsOf: Self.testData.deletingLastPathComponent().deletingLastPathComponent().appendingPathComponent("android/libxray.version"), encoding: .utf8)
        XCTAssertEqual(about.version(libXray), commit.trimmingCharacters(in: .whitespacesAndNewlines))
        XCTAssertNotNil(libXray.sourceUrl(version: about.version(libXray)))
        XCTAssertNil(about.version(try XCTUnwrap(about.components.first { $0.name == "Xray-core" })), "версию ядра сообщает расширение")
        XCTAssertEqual(AboutInfo.of(core: "26.3.27").version(try XCTUnwrap(about.components.first { $0.name == "Xray-core" })), "26.3.27")
        XCTAssertFalse(about.app.isEmpty)
        XCTAssertTrue(about.system.hasPrefix("iOS "))
    }

    @MainActor
    func testSubscriptionsDrawInEveryStateAndLanguage() throws {
        let now = Date()
        let groups = [
            GroupItem(id: "s", name: "Работа", maskedUrl: "https://example.com/sub/••••", servers: 12, updatedAt: now, lastAttemptAt: now, failure: nil, status: nil),
            GroupItem(id: "f", name: "Провайдер", maskedUrl: "https://example.org/••••", servers: 3, updatedAt: now.addingTimeInterval(-3 * 86400),
                      lastAttemptAt: now.addingTimeInterval(-300), failure: .status, status: 403),
            GroupItem(id: "p", name: "Профиль", maskedUrl: nil, servers: 1, updatedAt: nil, lastAttemptAt: nil, failure: nil, status: nil),
        ]
        let home = HomeState(status: TunnelStatus(state: .connected, serverId: "a"), servers: servers, selectedId: "a")
        for language in PgTexts.Language.allCases {
            let texts = PgTexts(language)
            for settings in [SubscriptionSettings(), SubscriptionSettings(refresh: .off, onlyViaVpn: false, notifySelected: false)] {
                let view = SubscriptionsView(
                    texts: texts, settings: settings, groups: groups, manualCount: 2, updating: ["s"], home: HomeState(), actions: SubscriptionsActions(), onBack: {}
                )
                XCTAssertEqual(try render(view, height: 1600).size.width, 393, accuracy: 1)
            }

            let editing = SubscriptionsView(
                texts: texts, settings: SubscriptionSettings(), groups: groups, manualCount: 0, home: home, editing: "f", actions: SubscriptionsActions(), onBack: {}
            )
            XCTAssertNotNil(try render(editing, height: 1600))
            let empty = SubscriptionsView(texts: texts, settings: SubscriptionSettings(), groups: [], manualCount: 0, home: HomeState(), actions: SubscriptionsActions(), onBack: {})
            XCTAssertNotNil(try render(empty))
        }
    }

    /// Подписка через модель окна: обновление кнопкой без защиты не грузит и говорит почему, без «только через VPN» —
    /// загружает и говорит итог; удаление группы снимает выбор и говорит «удалена».
    @MainActor
    func testSubscriptionActionsReachTheSharedFolder() async throws {
        let container = SharedContainer(directory: directory, vault: MemoryVault())
        let links = Self.links()
        let model = AppModel(container: container, texts: PgTexts(.russian), source: FixedSource(text: links.joined(separator: "\n")))
        let parsed = links.compactMap { LinkParser.parse($0).server }
        _ = try await container.servers.add(Array(parsed.prefix(2)), group: "Мой VPN", url: SubscriptionAddress.parse("https://example.com/sub/0123456789abcdef"))
        await model.reload()
        let group = try XCTUnwrap(model.groups.first)

        await model.updateSubscription(group)
        XCTAssertEqual(model.message, PgTexts(.russian).subsBlockedOff, "только через VPN, а защита выключена")

        await model.setSubscriptionSettings(SubscriptionSettings(onlyViaVpn: false))
        await model.updateSubscription(group)
        XCTAssertEqual(model.message?.hasPrefix("«Мой VPN» обновлена"), true, model.message ?? "")
        XCTAssertGreaterThan(model.groups.first?.servers ?? 0, 2)
        XCTAssertTrue(model.updating.isEmpty)

        let bad = await model.editSubscription(group, name: "Новое", url: "ftp://example.com/sub")
        XCTAssertFalse(bad)
        let renamed = await model.editSubscription(group, name: "Новое", url: "")
        XCTAssertTrue(renamed)
        XCTAssertEqual(model.groups.first?.name, "Новое")

        let selected = try XCTUnwrap(model.home.servers.first).id
        await model.select(selected)
        await model.removeGroup(group.id, name: "Новое", inUse: false)
        XCTAssertTrue(model.groups.isEmpty)
        XCTAssertNil(model.settings.selectedServerId)
        XCTAssertEqual(model.message, "«Новое» удалена")
    }

    /// Каждое уведомление — на обоих языках, без незаполненных «{N}», и его кнопки зарегистрированы.
    @MainActor
    func testNotificationsSpeakBothLanguagesAndTheirButtonsAreRegistered() throws {
        var notices: [Notice] = []
        let states = TunnelState.allCases.flatMap { state in
            [nil, TunnelErrors.coreFailed, TunnelErrors.serverNotResponding, TunnelErrors.otherVpn].map { TunnelStatus(state: state, serverId: "a", error: $0) }
        }
        for before in states {
            for after in states {
                for killSwitch in [false, true] {
                    if let notice = NotificationPolicy.forChange(before, after, server: "Нидерланды 01", killSwitch: killSwitch) {
                        notices.append(notice)
                    }
                }
            }
        }

        notices += [SubscriptionNotice.gone(server: "Германия 02", subscription: "Основная"), .changed(server: "Германия 02", subscription: "Основная"),
                    .stale(subscription: "Основная", reason: .forbidden(403)), .stale(subscription: "Основная", reason: .timeout)]
            .compactMap(NotificationPolicy.forSubscription)
        XCTAssertEqual(Set(notices.map(\.kind)), Set(NoticeKind.allCases))
        let registered = Set(Notifications.actionSets.map(Notifications.category))
        for notice in notices {
            XCTAssertTrue(registered.contains(Notifications.category(notice.actions)), "\(notice.actions)")
            for language in PgTexts.Language.allCases {
                let words = Notifications.words(notice, PgTexts(language))
                XCTAssertFalse(words.title.isEmpty, "\(notice.kind)")
                XCTAssertFalse(words.body.isEmpty, "\(notice.textKey)")
                XCTAssertFalse(words.body.contains("{"), words.body)
            }
        }

        let failed = try XCTUnwrap(NotificationPolicy.forChange(
            TunnelStatus(state: .connecting), TunnelStatus(state: .error, error: TunnelErrors.coreFailed), server: nil, killSwitch: false
        ))
        XCTAssertTrue(Notifications.words(failed, PgTexts(.russian)).body.contains(TunnelErrors.coreFailed), "код ошибки — в тексте")
    }

    @MainActor
    func testCheckDrawsInEveryStateAndLanguage() {
        let networks = PhoneNetworks.order([
            PhoneNetwork(kind: .vpn, name: "utun4", isDefault: true, expensive: true, constrained: true, ipv6: true),
            PhoneNetwork(kind: .wifi, name: "en0", isDefault: false), PhoneNetwork(kind: .cellular, name: "pdp_ip0", isDefault: false),
        ])
        let known = PublicIp(address: "203.0.113.24", viaServer: true, info: AddressInfo(
            address: "203.0.113.24", asn: 64500, holder: "EXAMPLE-AS", prefix: "203.0.113.0/24", country: "NL", city: "Amsterdam", failure: nil
        ))
        let ips = [PublicIp(), PublicIp(measuring: true), PublicIp(failed: true), known, PublicIp(address: "192.0.2.1", info: .failed("192.0.2.1", .private))]
        for language in PgTexts.Language.allCases {
            for state in TunnelState.allCases {
                for (index, ip) in ips.enumerated() {
                    let status = TunnelStatus(state: state, serverId: "a", error: state == .error ? TunnelErrors.serverNotResponding : nil)
                    let view = CheckView(
                        texts: PgTexts(language), status: status, killSwitch: true, core: "26.3.27", networks: state == .off ? [] : networks, ip: ip,
                        log: state == .connected ? ["2026-10-27 13:05:42 connected", "2026-10-27 13:07:01 connection lost, attempt 1"] : [],
                        probe: .constant(Self.probes[index % Self.probes.count])
                    )
                    XCTAssertNotNil(try? render(view, height: 3200), "\(state)")
                }
            }
        }
    }

    /// Замер в каждом виде: пустой, ошибка ввода, идет, итог DNS, TCP с отказом, TLS в порядке и с ошибками сертификата.
    private static let probes: [ProbeState] = {
        let facts = TlsFacts(
            protocol: "TLS 1.3", cipher: "TLS_AES_128_GCM_SHA256", alpn: "h2", subject: "CN=www.example.com, O=Example, C=NL",
            issuer: "CN=Example Test CA, O=Example", notBefore: Date(timeIntervalSince1970: 1_767_225_600),
            notAfter: Date(timeIntervalSince1970: 1_798_806_605), names: ["www.example.com", "*.example.org"], problems: []
        )
        var broken = facts
        broken.problems = [.expired, .nameMismatch, .untrusted]
        broken.notAfter = Date(timeIntervalSince1970: 1_700_000_000)
        let dns = ProbeResult(kind: .dns, target: "example.com", port: nil, steps: [
            ProbeStep(index: 1, address: "192.0.2.10", timeMs: 12, outcome: .ok), ProbeStep(index: 2, address: "2001:db8::10", timeMs: 12, outcome: .ok),
        ])
        return [
            ProbeState(), ProbeState(kind: .tcp, target: "example", error: .target), ProbeState(kind: .tcp, target: "example.com", running: true),
            ProbeState(target: "example.com", result: dns),
            ProbeState(kind: .tcp, target: "203.0.113.10", port: "8443", result: ProbeResult(
                kind: .tcp, target: "203.0.113.10", port: 8443, steps: [ProbeStep(index: 1, address: "203.0.113.10", timeMs: nil, outcome: .refused)]
            )),
            ProbeState(kind: .tls, target: "www.example.com", result: ProbeResult(
                kind: .tls, target: "www.example.com", port: 443, steps: [ProbeStep(index: 1, address: "192.0.2.10", timeMs: 48, outcome: .ok, tls: facts)]
            )),
            ProbeState(kind: .tls, target: "example.net", result: ProbeResult(
                kind: .tls, target: "example.net", port: 443, steps: [ProbeStep(index: 1, address: "198.51.100.7", timeMs: 51, outcome: .ok, tls: broken)]
            )),
        ]
    }()
}

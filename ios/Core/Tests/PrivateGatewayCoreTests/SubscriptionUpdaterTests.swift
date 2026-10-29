import Foundation
import XCTest
@testable import PrivateGatewayCore

/// Подписка без сети: заданный ответ или неудача; `hold` держит загрузку, пока тест ее не отпустит.
private final class FakeSource: SubscriptionSource, @unchecked Sendable {
    private let lock = NSLock()
    private var content = SubscriptionContent(text: "", title: nil)
    private var failure: SubscriptionDownloadError?
    private var holding = false
    private var waiting: CheckedContinuation<Void, Never>?
    private var count = 0
    private var protectedCount = 0

    var calls: Int { locked { count } }
    var protectedCalls: Int { locked { protectedCount } }
    var waits: Bool { locked { waiting != nil } }

    func serve(_ text: String, failure: SubscriptionDownloadError? = nil) {
        locked {
            content = SubscriptionContent(text: text, title: "Провайдер")
            self.failure = failure
        }
    }

    func hold() {
        locked { holding = true }
    }

    func release() {
        let next = locked { () -> CheckedContinuation<Void, Never>? in
            holding = false
            defer { waiting = nil }
            return waiting
        }
        next?.resume()
    }

    func download(_ url: URLComponents) async throws -> SubscriptionContent {
        let hold = locked { () -> Bool in
            count += 1
            return holding
        }
        if hold {
            await withCheckedContinuation { continuation in
                locked { waiting = continuation }
            }
        }

        let (content, failure) = locked { (self.content, self.failure) }
        if let failure {
            throw failure
        }

        return content
    }

    func downloadProtected(_ url: URLComponents) async throws -> SubscriptionContent {
        locked { protectedCount += 1 }
        return try await download(url)
    }

    private func locked<T>(_ body: () -> T) -> T {
        lock.lock()
        defer { lock.unlock() }
        return body()
    }
}

/// Часы теста: стоят, пока тест их не переведет.
private final class TestClock: @unchecked Sendable {
    private let lock = NSLock()
    private var value = Date(timeIntervalSince1970: 1_793_029_320)

    var now: Date {
        lock.lock()
        defer { lock.unlock() }
        return value
    }

    func advance(_ seconds: TimeInterval) {
        lock.lock()
        value += seconds
        lock.unlock()
    }
}

/// Обновление подписок — те же случаи, что `SubscriptionUpdaterTest` Android: «только через VPN» без защиты ничего не
/// грузит, новый список с пометкой пропавшего выбранного, неудача с причиной и кодом, сообщение только по расписанию, не
/// пора — не грузится, сообщения о выбранном — по настройке, второе нажатие во время загрузки не считается.
final class SubscriptionUpdaterTests: XCTestCase {
    private let source = FakeSource()
    private let clock = TestClock()
    private lazy var servers = ServerStore(file: MemoryFile(), vault: MemoryVault(), clock: { [clock] in clock.now })
    private lazy var settings = SettingsStore(file: MemoryFile())
    private lazy var updater = SubscriptionUpdater(source: source, servers: servers, settings: settings, clock: { [clock] in clock.now })
    private let url = SubscriptionAddress.parse("https://example.com/sub/0123456789abcdef0123456789abcdef")!

    /// Эталонные серверы без дублей.
    private static let catalog: [ServerDefinition] = {
        let folder = LinkParserTests.testData.appendingPathComponent("links")
        let files = (FileManager.default.enumerator(atPath: folder.path)?.compactMap { $0 as? String } ?? []).filter { $0.hasSuffix(".txt") }.sorted()
        var seen = Set<String>()
        return files.compactMap { file in
            (try? String(contentsOf: folder.appendingPathComponent(file), encoding: .utf8)).flatMap {
                LinkParser.parse($0.trimmingCharacters(in: .whitespacesAndNewlines)).server
            }
        }
        .filter { seen.insert(ServerKeys.key($0)).inserted }
    }()

    private func serve(_ list: [ServerDefinition]) {
        source.serve(list.map(LinkWriter.write).joined(separator: "\n"))
    }

    /// Подписка из трех серверов, выбран второй; номер подписки и номера серверов.
    private func subscribe() async throws -> (String, [String]) {
        XCTAssertGreaterThanOrEqual(Self.catalog.count, 4, "эталонов слишком мало")
        _ = try await servers.add(Array(Self.catalog.prefix(3)), group: "Провайдер", url: url)
        let groups = try await servers.groups()
        let group = try XCTUnwrap(groups.first).id
        let ids = try await servers.servers().map(\.id)
        XCTAssertEqual(ids.count, 3)
        try await settings.select(ids[1])
        return (group, ids)
    }

    func testOnlyViaVpnNothingIsDownloadedWhileProtectionIsOff() async throws {
        let (group, _) = try await subscribe()
        serve(Array(Self.catalog.prefix(3)))
        let blocked = await updater.update(group, connected: false)
        XCTAssertEqual(blocked.outcome, .blocked(.protectionOff))
        XCTAssertEqual(source.calls, 0, "адрес подписки не ушел в сеть мимо туннеля")

        try await settings.setSubscriptions(SubscriptionSettings(onlyViaVpn: false))
        let direct = await updater.update(group, connected: false)
        XCTAssertEqual(direct.outcome, .updated(added: 0, removed: 0), "настройку выключили — грузится напрямую")
        XCTAssertEqual(source.calls, 1)
        XCTAssertEqual(source.protectedCalls, 0)
    }

    func testUpdateUnderProtectionReplacesTheListAndKeepsTheSelectedServerMarked() async throws {
        let (group, ids) = try await subscribe()
        // Пришли первый и новый четвертый; третий ушел, второй ушел, но выбран — остается с пометкой.
        serve([Self.catalog[0], Self.catalog[3]])

        let report = await updater.update(group, connected: true)
        XCTAssertEqual(source.protectedCalls, 1, "snapshot connected не дает права грузить обычным download")
        XCTAssertEqual(report.outcome, .updated(added: 1, removed: 1))
        XCTAssertEqual(report.notices, [.gone(server: Self.catalog[1].name, subscription: "Провайдер")])
        let after = try await servers.servers()
        XCTAssertEqual(Set(after.map(\.id).filter { ids.contains($0) }), [ids[0], ids[1]])
        XCTAssertNotNil(after.first { $0.id == ids[1] }?.missingSince)
        let groups = try await servers.groups()
        let subscription = try XCTUnwrap(groups.first)
        XCTAssertNotNil(subscription.updatedAt)
        XCTAssertNil(subscription.failure)
    }

    func testFailureKeepsTheOldListAndRemembersTheReasonWithItsCode() async throws {
        let (group, ids) = try await subscribe()
        source.serve("", failure: SubscriptionDownloadError(.status, status: 403))

        let report = await updater.update(group, connected: true)
        XCTAssertEqual(report.outcome, .failed(.forbidden(403)))
        let after = try await servers.servers()
        XCTAssertEqual(after.map(\.id), ids, "прошлый список оставлен")
        let groups = try await servers.groups()
        let subscription = try XCTUnwrap(groups.first)
        XCTAssertEqual(subscription.failure, .status)
        XCTAssertEqual(subscription.status, 403)
        XCTAssertNotNil(subscription.lastAttemptAt)
    }

    func testCancelledBackgroundUpdateDoesNotApplyALateResultOrMarkFailure() async throws {
        let (group, ids) = try await subscribe()
        let before = try await servers.groups()
        serve([Self.catalog[3]])
        source.hold()
        let updater = self.updater
        let action = Task { await updater.update(group, connected: true, scheduled: true) }
        for _ in 0..<1000 where !source.waits { try await Task.sleep(nanoseconds: 1_000_000) }
        XCTAssertTrue(source.waits)
        action.cancel()
        source.release()
        let report = await action.value
        XCTAssertEqual(report.outcome, .unavailable)
        let afterIds = try await servers.servers().map(\.id)
        let after = try await servers.groups()
        XCTAssertEqual(afterIds, ids)
        XCTAssertEqual(after, before)
    }

    func testScheduledFailureIsReportedAndATapIsNot() async throws {
        let (group, _) = try await subscribe()
        source.serve("", failure: SubscriptionDownloadError(.timeout))

        let tap = await updater.update(group, connected: true)
        XCTAssertEqual(tap.notices, [], "кнопкой — итог на экране, без сообщения «не обновились»")

        // Пора: успех старше 12 часов, неудача — старше часа.
        clock.advance(13 * 3600)
        let notices = await updater.updateDue(connected: true)
        XCTAssertEqual(notices, [.stale(subscription: "Провайдер", reason: .timeout)])
        XCTAssertEqual(source.calls, 2)
    }

    func testNotDueMeansNotDownloaded() async throws {
        _ = try await subscribe()
        serve(Array(Self.catalog.prefix(3)))
        let notices = await updater.updateDue(connected: true)
        XCTAssertEqual(notices, [])
        XCTAssertEqual(source.calls, 0, "подписку только что добавили — обновлять рано")

        clock.advance(13 * 3600)
        let offline = await updater.updateDue(connected: false)
        XCTAssertEqual(offline, [])
        XCTAssertEqual(source.calls, 0, "пора, но защита выключена — по расписанию тоже ничего")
        _ = await updater.updateDue(connected: true)
        XCTAssertEqual(source.calls, 1)
    }

    func testSelectedServerNoticesFollowTheSetting() async throws {
        let (group, _) = try await subscribe()
        try await settings.setSubscriptions(SubscriptionSettings(notifySelected: false))
        serve([Self.catalog[0]])

        let report = await updater.update(group, connected: true)
        XCTAssertEqual(report.outcome, .updated(added: 0, removed: 1))
        XCTAssertEqual(report.notices, [])
    }

    func testASecondTapWhileUpdatingIsNotCounted() async throws {
        let (group, _) = try await subscribe()
        serve(Array(Self.catalog.prefix(3)))
        source.hold()
        let updater = self.updater
        let first = Task { await updater.update(group, connected: true) }
        for _ in 0..<1000 where !source.waits {
            try await Task.sleep(nanoseconds: 5_000_000)
        }

        XCTAssertTrue(source.waits, "первая загрузка не началась")
        let busy = await updater.busy
        XCTAssertEqual(busy, [group])
        let second = await updater.update(group, connected: true)
        XCTAssertEqual(second.outcome, .blocked(.busy))

        source.release()
        let report = await first.value
        XCTAssertEqual(report.outcome, .updated(added: 0, removed: 0))
        let after = await updater.busy
        XCTAssertTrue(after.isEmpty)
        XCTAssertEqual(source.calls, 1)
    }

    func testDeletedWhileWaitingIsGone() async throws {
        let (group, _) = try await subscribe()
        try await servers.remove(group: group)
        let report = await updater.update(group, connected: true)
        XCTAssertEqual(report.outcome, .gone)
        XCTAssertEqual(source.calls, 0)
    }
}

import PrivateGatewayCore
import PrivateGatewayTheme
import XCTest
@testable import PrivateGateway

final class ReadinessTests: XCTestCase {
    @MainActor
    func testAccessLossClearsAutomaticProtectionButKeepsSiteRulesAndPin() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("pg-access-reset-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: directory) }
        let container = SharedContainer(directory: directory, vault: MemoryVault())
        try await container.settings.setKillSwitch(true)
        try await container.settings.setAlwaysOn(true)
        try await container.settings.setConnectOnLaunch(true)
        let rules = RoutingSettings(rules: [SiteRule(pattern: "blocked.example", action: .block)])
        try await container.settings.setRouting(rules)
        let pin = try XCTUnwrap(BlockPin.create("1234"))
        try container.blockPin.save(pin)
        let model = AppModel(container: container)
        await model.accessLost()
        let saved = try await container.settings.load()
        XCTAssertFalse(saved.killSwitch)
        XCTAssertFalse(saved.alwaysOn)
        XCTAssertFalse(saved.connectOnLaunch)
        XCTAssertTrue(saved.manualActivationRequired)
        XCTAssertEqual(saved.routing, rules)
        XCTAssertEqual(container.blockPin.read(), pin)
        let restarted = SharedContainer(directory: directory, vault: MemoryVault())
        let reloaded = try await restarted.settings.load()
        XCTAssertTrue(reloaded.manualActivationRequired, "барьер должен пережить перезапуск приложения")
        XCTAssertFalse(reloaded.alwaysOn)
    }

    @MainActor
    func testLoginWaitsForThePreviousNativeProfileCleanup() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("pg-login-barrier-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: directory) }
        let container = SharedContainer(directory: directory, vault: MemoryVault())
        let source = ReadinessAccountSource()
        let cleaning = AccountCleanupGate()
        let finish = AccountCleanupGate()
        let model = AccountModel(container: container, source: source)
        model.onLost = {
            cleaning.open()
            await finish.wait()
            return true
        }
        model.setEmail("member@example.org")
        model.setPassword("test-password")
        model.submit()
        await cleaning.wait()
        let earlyCalls = await source.loginCalls
        XCTAssertEqual(earlyCalls, 0, "новый аккаунт не авторизуется, пока прежний native save еще завершается")
        XCTAssertFalse(container.account.read().allowed(now: Date()))
        finish.open()
        await source.waitForLogin()
        let calls = await source.loginCalls
        XCTAssertEqual(calls, 1)
    }

    @MainActor
    func testSignOutBlocksNewLoginUntilNativeCleanupHasFinished() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("pg-signout-barrier-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: directory) }
        let source = ReadinessAccountSource()
        let model = AccountModel(container: SharedContainer(directory: directory, vault: MemoryVault()), source: source)
        let cleaning = AccountCleanupGate()
        let finish = AccountCleanupGate()
        model.onLost = {
            cleaning.open()
            await finish.wait()
            return true
        }
        let stop = Task { await model.signOut() }
        await cleaning.wait()
        XCTAssertTrue(model.signingOut)
        model.setEmail("member@example.org")
        model.setPassword("test-password")
        model.submit()
        let earlyCalls = await source.loginCalls
        XCTAssertEqual(earlyCalls, 0)
        XCTAssertFalse(model.form.busy)
        finish.open()
        await stop.value
        XCTAssertFalse(model.signingOut)
        model.submit()
        await source.waitForLogin()
        let calls = await source.loginCalls
        XCTAssertEqual(calls, 1)
    }

    @MainActor
    func testUnconfirmedNativeCleanupCannotAuthorizeAndRecoveryNeedsNoPassword() async throws {
        for saveFails in [true, false] {
            let directory = FileManager.default.temporaryDirectory.appendingPathComponent("pg-unconfirmed-\(UUID().uuidString)")
            defer { try? FileManager.default.removeItem(at: directory) }
            let source = ReadinessAccountSource()
            let model = AccountModel(container: SharedContainer(directory: directory, vault: MemoryVault()), source: source)
            model.onLost = {
                await TunnelCleanupConfirmation.perform(disable: {
                    if saveFails { throw NativePreferenceFailure() }
                    return true
                }, stopped: { saveFails }) // успешный save, но stop истек — тоже не подтверждение
            }
            model.setEmail("member@example.org")
            model.setPassword("test-password")
            model.submit()
            await readinessWait { model.cleanupPending && !model.restoringNetwork }
            XCTAssertFalse(model.allowed)
            let refused = await source.loginCalls
            XCTAssertEqual(refused, 0)
            model.onLost = { true }
            model.retryCleanup()
            await readinessWait { !model.cleanupPending && !model.restoringNetwork }
            let recovered = await source.loginCalls
            XCTAssertEqual(recovered, 0, "повтор очистки не должен сам входить или запрашивать пароль")
            model.setPassword("test-password")
            model.submit()
            await source.waitForLogin()
            let calls = await source.loginCalls
            XCTAssertEqual(calls, 1)
        }
    }

    @MainActor
    private func readinessWait(_ condition: () -> Bool, file: StaticString = #filePath, line: UInt = #line) async {
        for _ in 0..<1_000 {
            if condition() { return }
            await Task.yield()
        }
        XCTFail("очистка не завершилась", file: file, line: line)
    }

    func testOnlyRemoteVpnAllowsVpnOnlySubscriptionRefresh() {
        XCTAssertTrue(AppModel.subscriptionProtected(TunnelStatus(state: .connected)))
        XCTAssertFalse(AppModel.subscriptionProtected(TunnelStatus(state: .connected, localSiteFilter: true)))
        XCTAssertFalse(AppModel.subscriptionProtected(TunnelStatus(state: .connecting)))
        XCTAssertFalse(AppModel.subscriptionProtected(TunnelStatus(state: .off)))
    }

    @MainActor
    func testLocalFilterCannotClaimEncryptedTrafficOrHiddenIp() {
        let texts = PgTexts(.english)
        let look = StateLook.localFilter(texts)
        XCTAssertEqual(look.power, texts.text(key: "power.label.filter"))
        XCTAssertNotEqual(look.title, texts.statusConnectedTitle)
        let rows = CheckRows.of(CheckInput(state: .connected, localSiteFilter: true), texts)
        XCTAssertEqual(rows.first { $0.key == CheckId.dns.rawValue }?.outcome, .warning)
        XCTAssertEqual(rows.first { $0.key == CheckId.exitIp.rawValue }?.outcome, .warning)
        XCTAssertEqual(rows.first { $0.key == CheckId.allTraffic.rawValue }?.outcome, .warning)
        XCTAssertFalse(rows.first { $0.key == CheckId.killSwitch.rawValue }?.outcome == .ok)
    }

    @MainActor
    func testLabTlsSuccessRequiresAValidCertificate() {
        let tls = TlsFacts(protocol: "TLSv1.3", cipher: "test", alpn: nil, subject: "example.org", issuer: "test", notBefore: nil,
                           notAfter: nil, names: ["example.org"], problems: [.nameMismatch])
        let result = ProbeResult(kind: .tls, target: "example.org", port: 443,
                                 steps: [ProbeStep(index: 1, address: "192.0.2.10", timeMs: 12, outcome: .ok, tls: tls)])
        XCTAssertFalse(MobileLabModel.succeeded(result))
        XCTAssertFalse(MobileLabModel.succeeded(ProbeResult(kind: .tls, target: "example.org", port: 443, steps: [])))
    }

    @MainActor
    func testLabSnapshotsStayFixedUntilTheNextExplicitCapture() {
        let model = MobileLabModel()
        XCTAssertNil(model.before)
        XCTAssertNil(model.after)
        let direct = CheckInput(state: .off, ip: PublicIp(address: "192.0.2.10", viaServer: false))
        model.capture(direct, [])
        let before = model.before
        let protected = CheckInput(state: .connected, ip: PublicIp(address: "192.0.2.20", viaServer: true))
        model.cancel() // смена состояния/интерфейса отменяет пробы, но не перезаписывает ручные снимки
        XCTAssertEqual(model.before, before)
        XCTAssertNil(model.after)
        model.capture(protected, [])
        let after = model.after
        model.cancel()
        XCTAssertEqual(model.before, before)
        XCTAssertEqual(model.after, after)
        XCTAssertEqual(model.before?.address, "192.0.2.10")
        XCTAssertEqual(model.after?.address, "192.0.2.20")
        model.capture(CheckInput(state: .connected, ip: PublicIp(address: "192.0.2.30", viaServer: true)), [])
        XCTAssertEqual(model.before, before)
        XCTAssertEqual(model.after?.address, "192.0.2.30")
    }

    @MainActor
    func testChangedSessionCannotPublishThePreviousDelayOrIp() async {
        let model = CheckModel(through: { 22 }, fresh: { 33 })
        await model.measureDelay(connected: true, toServer: {
            model.invalidate()
            return 11
        })
        XCTAssertNil(model.findings.delay)
        XCTAssertFalse(model.findings.measuringDelay)
        let input = CheckInput(state: .connected, ip: PublicIp(address: "192.0.2.10", viaServer: false))
        XCTAssertNil(MobileLabSnapshot(input, []).address, "до первого замера VPN нельзя брать прежний прямой IP")
    }

    @MainActor
    func testAQueuedDelayUsesTheNewestSessionCallback() async throws {
        let gate = DelayGate()
        var calls = 0
        let model = CheckModel(through: {
            calls += 1
            if calls == 1 { return await gate.hold() }
            return 22
        }, fresh: { 33 })
        let first = Task { await model.measureDelay(connected: true, toServer: { 11 }) }
        for _ in 0..<500 where gate.waiting == nil { try await Task.sleep(nanoseconds: 10_000_000) }
        guard gate.waiting != nil else {
            first.cancel()
            XCTFail("первый замер не дошел до ожидающего ответа")
            return
        }
        model.invalidate()
        await model.measureDelay(connected: true, toServer: { 99 })
        gate.release()
        await first.value
        XCTAssertEqual(model.findings.delay?.toServer, 99)
        XCTAssertEqual(calls, 2)
    }
}

@MainActor
private final class DelayGate {
    var waiting: CheckedContinuation<Int?, Never>?
    func hold() async -> Int? { await withCheckedContinuation { waiting = $0 } }
    func release() {
        waiting?.resume(returning: 22)
        waiting = nil
    }
}

@MainActor
private final class AccountCleanupGate {
    private var opened = false
    private var waiting: [CheckedContinuation<Void, Never>] = []
    func wait() async {
        if opened { return }
        await withCheckedContinuation { waiting.append($0) }
    }
    func open() {
        opened = true
        let continuations = waiting
        waiting = []
        continuations.forEach { $0.resume() }
    }
}

private actor ReadinessAccountSource: AccountSource {
    private(set) var loginCalls = 0
    private var waiting: [CheckedContinuation<Void, Never>] = []
    func login(email: String, password: String, code: String?, recovery: String?, device: DeviceInfo) async -> LoginResult {
        loginCalls += 1
        let continuations = waiting
        waiting = []
        continuations.forEach { $0.resume() }
        return .failed(.invalid)
    }
    func status(token: String) async -> StatusResult { .unreachable }
    func logout(token: String) async -> Bool { true }
    func waitForLogin() async {
        if loginCalls > 0 { return }
        await withCheckedContinuation { waiting.append($0) }
    }
}

private struct NativePreferenceFailure: Error {}

import Foundation
import XCTest
@testable import PrivateGatewayCore

/// Переподключение — те же случаи, что `ReconnectTest` Android (и `ReconnectTests` десктопа): чистая логика с явным временем.
final class ReconnectTests: XCTestCase {
    private let second = 1000
    private var minute: Int { 60 * second }
    private var hour: Int { 60 * minute }
    private let timing = ReconnectTiming()
    private let backoff = ReconnectPolicy.backoff60Seconds

    func testPauseDoublesUpToTheCeiling() {
        XCTAssertEqual((1...9).map { timing.delay($0) / second }, [1, 2, 4, 8, 16, 32, 60, 60, 60])
        XCTAssertEqual(timing.delay(Int.max), 60 * second, "и через много попыток — без переполнения")
    }

    func testGivesUpOnlyByThePolicy() {
        XCTAssertTrue(timing.givesUp(.never, sinceLost: 0))
        XCTAssertFalse(timing.givesUp(.upTo5Minutes, sinceLost: 299 * second))
        XCTAssertTrue(timing.givesUp(.upTo5Minutes, sinceLost: 300 * second))
        XCTAssertFalse(timing.givesUp(.backoff60Seconds, sinceLost: 3600 * second))
    }

    func testTimingIsTheDesktopDefault() {
        XCTAssertEqual(timing, ReconnectTiming(every: minute, probeTimeout: 10 * second, firstPause: second, maxPause: minute, giveUpAfter: 5 * minute))
    }

    func testPolicyIsReadFromTheSavedNameAndAnythingElseIsTheDefault() {
        XCTAssertEqual(ReconnectPolicy.read("UpTo5Minutes"), .upTo5Minutes)
        XCTAssertEqual(ReconnectPolicy.read("Never"), .never)
        XCTAssertEqual(ReconnectPolicy.read("Backoff60Seconds"), backoff)
        XCTAssertEqual(ReconnectPolicy.read(nil), backoff)
        XCTAssertEqual(ReconnectPolicy.read("Sometimes"), backoff, "имя из будущей версии")
    }

    func testProbeAsksAnAddressSoNoDnsLeavesTheTunnel() {
        let octets = TunnelProbe.host.split(separator: ".")
        XCTAssertEqual(octets.count, 4)
        XCTAssertTrue(octets.allSatisfy { UInt8($0) != nil }, "адрес, а не имя")
        XCTAssertTrue(TunnelProbe.request.hasPrefix("HEAD / HTTP/1.1\r\n"))
        XCTAssertTrue(TunnelProbe.request.contains("\r\nHost: \(TunnelProbe.host)\r\n"))
        XCTAssertTrue(TunnelProbe.request.hasSuffix("\r\n\r\n"), "конец заголовков, иначе сервер ждет")
    }

    func testAnyHttpStatusLineIsAnAnswer() {
        for head in ["HTTP/1.1 301 Moved Permanently\r\n", "HTTP/1.1 204", "HTTP/1.0 403 Forbidden\r\nServer: x", "HTTP/1.1 500\r\n"] {
            XCTAssertTrue(TunnelProbe.answered(Array(head.utf8)), head)
        }
        for head in ["", "HTTP/1.1", "HTTP/1.1 ", "HTTP/1.1 30", "HTTP/1.1 3O1", "HTTP/1.1 2041", "HTTP/2 200", "SSH-2.0-OpenSSH_9.6\r\n", "\u{16}\u{3}\u{3}"] {
            XCTAssertFalse(TunnelProbe.answered(Array(head.utf8)), head)
        }
        XCTAssertGreaterThanOrEqual(TunnelProbe.headLength, "HTTP/1.1 301".utf8.count)
    }

    func testServerIsProbedOnceAMinuteWhileItAnswers() {
        let watch = HealthWatch(now: 5 * minute)
        XCTAssertEqual(watch.nextProbeAt, 6 * minute)
        watch.begin()
        XCTAssertEqual(watch.probed(answered: true, now: 6 * minute + 300), .alive)
        XCTAssertEqual(watch.nextProbeAt, 7 * minute + 300, "срок — от конца прежней пробы")
    }

    func testOneFailureProbesAgainAtOnceAndTheSecondInARowIsADrop() {
        let watch = HealthWatch(now: 0)
        watch.begin()
        XCTAssertEqual(watch.probed(answered: false, now: minute + 10 * second), .probeAgain)
        XCTAssertEqual(watch.nextProbeAt, minute + 10 * second, "одна неудача бывает и у живого сервера — вторая проба сразу")
        watch.begin()
        XCTAssertEqual(watch.probed(answered: false, now: minute + 20 * second), .lost)
    }

    func testAnAnswerBetweenFailuresStartsTheCountAgain() {
        let watch = HealthWatch(now: 0)
        XCTAssertEqual(watch.probed(answered: false, now: minute), .probeAgain)
        XCTAssertEqual(watch.probed(answered: true, now: minute + second), .alive)
        XCTAssertEqual(watch.nextProbeAt, 2 * minute + second)
        XCTAssertEqual(watch.probed(answered: false, now: 2 * minute + second), .probeAgain)
        XCTAssertEqual(watch.probed(answered: false, now: 2 * minute + 2 * second), .lost)
    }

    func testNetworkChangeOrWakingUpProbesTheServerAtOnce() {
        let watch = HealthWatch(now: 0, timing: ReconnectTiming(every: hour))
        watch.nudge()
        XCTAssertLessThanOrEqual(watch.nextProbeAt, 5 * minute, "сразу, а не через час")
        watch.begin()
        XCTAssertEqual(watch.probed(answered: true, now: 5 * minute), .alive)
        XCTAssertEqual(watch.nextProbeAt, 5 * minute + hour, "сервер отвечает — дальше раз в час")

        watch.nudge()
        watch.begin()
        XCTAssertEqual(watch.probed(answered: false, now: 6 * minute), .probeAgain)
        watch.begin()
        XCTAssertEqual(watch.probed(answered: false, now: 6 * minute + 2 * second), .lost)
    }

    func testNetworkChangeDuringAProbeBringsTheNextProbeAtOnce() {
        let watch = HealthWatch(now: 0)
        watch.begin()
        watch.nudge()
        XCTAssertEqual(watch.probed(answered: true, now: minute), .alive)
        XCTAssertEqual(watch.nextProbeAt, minute, "проба могла уйти еще в прежнюю сеть — новая сразу")
        watch.begin()
        XCTAssertEqual(watch.probed(answered: true, now: minute + second), .alive)
        XCTAssertEqual(watch.nextProbeAt, 2 * minute + second, "подгон забран — дальше раз в минуту")
    }

    func testServerThatDoesNotAnswerAtConnectIsADropAndNeverIsAnErrorAtOnce() {
        let watch = HealthWatch(now: 0)
        watch.nudge()
        XCTAssertEqual(watch.nextProbeAt, 0)
        watch.begin()
        XCTAssertEqual(watch.probed(answered: false, now: 10 * second), .probeAgain)
        watch.begin()
        XCTAssertEqual(watch.probed(answered: false, now: 20 * second), .lost)
        XCTAssertNotNil(Reconnection.after(20 * second, backoff))
        XCTAssertNotNil(Reconnection.after(20 * second, .upTo5Minutes))
        XCTAssertNil(Reconnection.after(20 * second, .never))
    }

    func testServerThatStopsAnsweringIsRetriedWithGrowingPausesAndNeverGivenUpByDefault() throws {
        let lost = 10 * minute
        let reconnection = try XCTUnwrap(Reconnection.after(lost, backoff))
        XCTAssertEqual(reconnection.lostAt, lost)
        XCTAssertEqual(reconnection.attempt, 1)
        var now = lost
        var pauses: [Int] = []
        for index in 0..<9 {
            let at = reconnection.nextAttemptAt
            pauses.append(at - now)
            XCTAssertEqual(reconnection.begin(), index + 1)
            now = at + 3 * second
            XCTAssertEqual(reconnection.failed(now, backoff), .retry)
        }

        XCTAssertEqual(pauses.map { $0 / second }, [1, 2, 4, 8, 16, 32, 60, 60, 60], "пауза — от конца прежней попытки")
        XCTAssertEqual(reconnection.attempt, 10)
        XCTAssertEqual(reconnection.failed(lost + hour, backoff), .retry, "«Экспоненц. до 60 с» не сдается и через час")
        XCTAssertEqual(reconnection.attempt, 11)
    }

    func testUpTo5MinutesGivesUpOnce5MinutesPassedSinceTheLoss() throws {
        let lost = 10 * minute
        let reconnection = try XCTUnwrap(Reconnection.after(lost, .upTo5Minutes))
        XCTAssertEqual(reconnection.failed(lost + 5 * minute - 1, .upTo5Minutes), .retry)
        XCTAssertEqual(reconnection.attempt, 2)
        XCTAssertEqual(reconnection.failed(lost + 5 * minute, .upTo5Minutes), .giveUp)
        XCTAssertEqual(reconnection.attempt, 2, "попытка, на которой сдались, — последняя")
    }

    func testNeverStopsAtOnceAndAPolicyChangedToNeverStopsAtTheNextFailure() throws {
        XCTAssertNil(Reconnection.after(0, .never))
        let reconnection = try XCTUnwrap(Reconnection.after(0, backoff))
        XCTAssertEqual(reconnection.failed(second, backoff), .retry)
        // Политика — на каждой неудаче: ее смена в настройках действует без переподключения.
        XCTAssertEqual(reconnection.failed(3 * second, .never), .giveUp)
    }

    func testNetworkChangeSkipsTheRestOfThePause() throws {
        let slow = ReconnectTiming(firstPause: hour, maxPause: hour)
        let reconnection = try XCTUnwrap(Reconnection.after(0, backoff, timing: slow))
        XCTAssertEqual(reconnection.nextAttemptAt, hour)
        reconnection.nudge()
        XCTAssertEqual(reconnection.nextAttemptAt, 0, "смена сети сразу после обрыва не пропадает")
        XCTAssertEqual(reconnection.begin(), 1)
        XCTAssertEqual(reconnection.failed(5 * second, backoff), .retry)
        XCTAssertEqual(reconnection.nextAttemptAt, 5 * second + hour, "подгон забран — следующая попытка ждет свою паузу")
    }

    func testNetworkChangeDuringAnAttemptBringsTheNextAttemptAtOnce() throws {
        let slow = ReconnectTiming(every: hour, firstPause: hour, maxPause: hour, giveUpAfter: hour)
        let reconnection = try XCTUnwrap(Reconnection.after(0, backoff, timing: slow))
        reconnection.nudge()
        XCTAssertEqual(reconnection.begin(), 1)
        reconnection.nudge()
        XCTAssertEqual(reconnection.failed(2 * second, backoff), .retry)
        XCTAssertEqual(reconnection.nextAttemptAt, 2 * second, "сигнал ждал конца попытки, а не пропал")
        XCTAssertEqual(reconnection.begin(), 2)

        let next = try XCTUnwrap(Reconnection.after(10 * minute, backoff, timing: slow))
        XCTAssertEqual(next.attempt, 1)
        XCTAssertEqual(next.nextAttemptAt, 10 * minute + hour)
    }
}

/// Виртуальные часы наблюдения: ожидание сразу переводит время; подгоны — в заданные моменты; дальше `horizon` — отмена.
private final class VirtualTimer: GuardTimer, @unchecked Sendable {
    private let lock = NSLock()
    private var time = 0
    private var nudges: [Int]
    private let horizon: Int

    init(horizon: Int, nudges: [Int] = []) {
        self.horizon = horizon
        self.nudges = nudges.sorted()
    }

    func now() -> Int {
        locked { time }
    }

    func advance(_ ms: Int) {
        locked { time += ms }
    }

    func wait(until at: Int) async throws -> Bool {
        try locked { () throws -> Bool in
            if let nudge = nudges.first, nudge <= at {
                nudges.removeFirst()
                time = max(time, nudge)
                return true
            }

            guard at <= horizon else {
                time = horizon
                throw CancellationError()
            }

            time = at
            return false
        }
    }

    private func locked<T>(_ body: () throws -> T) rethrows -> T {
        lock.lock()
        defer { lock.unlock() }
        return try body()
    }
}

/// Расширение для проверки: пробы и попытки по очереди из списков, события — строками с временем.
private final class Rig: GuardEvents, @unchecked Sendable {
    private let lock = NSLock()
    let timer: VirtualTimer
    private var probes: [Bool]
    private var restarts: [String?]
    private(set) var log: [String] = []
    private(set) var probesAt: [Int] = []
    private(set) var restartsAt: [Int] = []
    var policy = ReconnectPolicy.backoff60Seconds
    /// Политика после первой попытки — смена в настройках во время переподключения.
    var policyAfterFirstRestart: ReconnectPolicy?

    init(horizon: Int, probes: [Bool], restarts: [String?] = [], nudges: [Int] = []) {
        timer = VirtualTimer(horizon: horizon, nudges: nudges)
        self.probes = probes
        self.restarts = restarts
    }

    func run() async {
        let guarded = TunnelGuard(
            timer: timer, policy: { [self] in locked { policy } }, probe: { [self] in probe() }, restart: { [self] in restart($0) }, events: self
        )
        await guarded.run()
    }

    private func probe() -> Bool {
        let now = timer.now()
        let answer = locked { () -> Bool in
            probesAt.append(now)
            return probes.isEmpty ? true : probes.removeFirst()
        }
        // Проба идет секунду — как настоящая, а не мгновенно.
        timer.advance(1000)
        return answer
    }

    private func restart(_ attempt: Int) -> String? {
        let now = timer.now()
        return locked { () -> String? in
            restartsAt.append(now)
            log.append("restart \(attempt) @\(now)")
            if let next = policyAfterFirstRestart {
                policy = next
            }

            return restarts.isEmpty ? nil : restarts.removeFirst()
        }
    }

    func reconnecting(_ attempt: Int) async {
        record("reconnecting \(attempt)")
    }

    func protectedAgain() async {
        record("protected")
    }

    func gaveUp(_ error: String) async {
        record("gave up \(error)")
    }

    private func record(_ event: String) {
        let now = timer.now()
        locked { log.append("\(event) @\(now)") }
    }

    private func locked<T>(_ body: () -> T) -> T {
        lock.lock()
        defer { lock.unlock() }
        return body()
    }
}

/// Наблюдение и переподключение на виртуальном времени — случаи `TunnelGuardTest` Android.
final class TunnelGuardTests: XCTestCase {
    func testLiveServerIsProbedAtOnceAndThenEveryMinute() async {
        let rig = Rig(horizon: 3 * 61000 + 1, probes: [])
        await rig.run()
        // Проба сразу после подключения, дальше — через минуту после конца прежней (проба идет секунду).
        XCTAssertEqual(rig.probesAt, [0, 61000, 122_000, 183_000])
        XCTAssertEqual(rig.log, [])
    }

    func testOneFailureIsProbedAgainAtOnceAndIsNotADrop() async {
        let rig = Rig(horizon: 2 * 60000, probes: [true, false, true])
        await rig.run()
        XCTAssertEqual(Array(rig.probesAt.prefix(3)), [0, 61000, 62000], "вторая проба — сразу после неудачи")
        XCTAssertEqual(rig.log, [], "одна неудача бывает и у живого сервера")
    }

    func testTwoFailuresReconnectToTheSameServerAfterAPauseAndProbeIt() async {
        let rig = Rig(horizon: 10000, probes: [false, false, true])
        await rig.run()
        // Обрыв — после второй пробы (2 с); первая попытка — через секунду; после нее — проба.
        XCTAssertEqual(Array(rig.log.prefix(3)), ["reconnecting 1 @2000", "restart 1 @3000", "protected @4000"])
    }

    func testPausesDoubleUpToAMinute() async {
        let rig = Rig(horizon: 185_000, probes: [false, false], restarts: Array(repeating: TunnelErrors.coreFailed, count: 8))
        await rig.run()
        XCTAssertEqual(rig.restartsAt, [3000, 5000, 9000, 17000, 33000, 65000, 125_000, 185_000])
        XCTAssertTrue(rig.log.contains("reconnecting 9 @185000"), "номер попытки, которую ждут")
    }

    func testNudgeDuringAPauseTriesAtOnce() async {
        let rig = Rig(horizon: 3600, probes: [false, false], restarts: Array(repeating: TunnelErrors.coreFailed, count: 3), nudges: [3500])
        await rig.run()
        XCTAssertEqual(rig.restartsAt, [3000, 3500], "сеть сменилась — не ждать 2 с")
    }

    func testUpToFiveMinutesGivesUpWithTheServerNotResponding() async throws {
        let rig = Rig(horizon: 10 * 60000, probes: Array(repeating: false, count: 200))
        rig.policy = .upTo5Minutes
        await rig.run()
        let last = try XCTUnwrap(rig.log.last)
        XCTAssertTrue(last.hasPrefix("gave up \(TunnelErrors.serverNotResponding)"), last)
        let at = try XCTUnwrap(Int(last.split(separator: "@").last ?? ""))
        XCTAssertTrue(at - 2000 >= 5 * 60000 && at - 2000 < 6 * 60000, "сдались через 5 минут после обрыва, а не раньше: \(at)")
    }

    func testNeverGivesUpAtOnceAndPolicyChangeAppliesToTheNextFailure() async throws {
        let never = Rig(horizon: 60000, probes: [false, false])
        never.policy = .never
        await never.run()
        XCTAssertEqual(never.log, ["gave up \(TunnelErrors.serverNotResponding) @2000"], "«Не повторять» — сразу «Ошибка»")

        let changed = Rig(horizon: 60000, probes: Array(repeating: false, count: 10))
        changed.policyAfterFirstRestart = .never
        await changed.run()
        XCTAssertTrue(try XCTUnwrap(changed.log.last).hasPrefix("gave up"), "\(changed.log)")
        XCTAssertEqual(changed.restartsAt.count, 1, "смена политики — на ближайшей неудаче, без переподключения")
    }

    func testGivingUpNamesTheLastFailure() async throws {
        let rig = Rig(horizon: 20 * 60000, probes: Array(repeating: false, count: 400), restarts: Array(repeating: TunnelErrors.coreFailed, count: 400))
        rig.policy = .upTo5Minutes
        await rig.run()
        XCTAssertTrue(try XCTUnwrap(rig.log.last).hasPrefix("gave up \(TunnelErrors.coreFailed)"), "«Ошибка» с кодом последней неудачи: \(rig.log.last ?? "")")
    }

    func testSystemTimerWaitsAndANudgeCutsTheWaitShort() async throws {
        let timer = SystemGuardTimer()
        let start = timer.now()
        let nudged = try await timer.wait(until: start + 50)
        XCTAssertFalse(nudged)
        XCTAssertGreaterThanOrEqual(timer.now(), start + 50)

        timer.nudge()
        let before = timer.now()
        let early = try await timer.wait(until: before + 60000)
        XCTAssertTrue(early, "подгон, пришедший до ожидания, не пропадает")
        XCTAssertLessThan(timer.now() - before, 1000)

        let waiting = Task { try await timer.wait(until: timer.now() + 60000) }
        try await Task.sleep(nanoseconds: 50_000_000)
        timer.nudge()
        let woken = try await waiting.value
        XCTAssertTrue(woken, "подгон во время ожидания прерывает его")

        let cancelled = Task { try await timer.wait(until: timer.now() + 60000) }
        cancelled.cancel()
        do {
            _ = try await cancelled.value
            XCTFail("отмена задачи — ошибка, наблюдение кончается")
        } catch {}
    }

    func testErrorsARetryCannotFixGiveUpAtOnce() async {
        let rig = Rig(horizon: 60000, probes: [false, false], restarts: [TunnelErrors.noServer])
        await rig.run()
        XCTAssertEqual(rig.log, ["reconnecting 1 @2000", "restart 1 @3000", "gave up \(TunnelErrors.noServer) @3000"])
    }
}

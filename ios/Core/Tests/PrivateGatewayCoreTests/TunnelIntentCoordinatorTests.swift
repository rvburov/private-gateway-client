import XCTest
@testable import PrivateGatewayCore

final class TunnelIntentCoordinatorTests: XCTestCase {
    @MainActor
    func testPreferenceSaveFailureStillWaitsForStopButDoesNotConfirmCleanup() async {
        var waitedForStop = false
        let confirmed = await TunnelCleanupConfirmation.perform(disable: {
            throw PreferenceFailure()
        }, stopped: {
            waitedForStop = true
            return true
        })
        XCTAssertTrue(waitedForStop)
        XCTAssertFalse(confirmed)
    }

    @MainActor
    func testStopDeadlineCannotConfirmCleanupAfterSuccessfulPreferenceSave() async {
        let confirmed = await TunnelCleanupConfirmation.perform(disable: { true }, stopped: { false })
        XCTAssertFalse(confirmed)
    }

    @MainActor
    func testCleanupNeedsBothSavedDisabledProfileAndConfirmedStop() async {
        let confirmed = await TunnelCleanupConfirmation.perform(disable: { true }, stopped: { true })
        XCTAssertTrue(confirmed)
        let stillEnabled = await TunnelCleanupConfirmation.perform(disable: { false }, stopped: { true })
        XCTAssertFalse(stillEnabled, "успешный callback save без перечитанного выключенного профиля недостаточен")
    }

    /// Нативное saveToPreferences уже нельзя отменить. Его поздний ответ не должен запускать туннель после выхода,
    /// а cleanup должен закончиться до запуска следующего аккаунта.
    @MainActor
    func testSignOutWaitsForSuspendedPreferenceSaveAndCleanupBeforeNewStart() async throws {
        let intents = TunnelIntentCoordinator()
        let saving = IntentGate()
        let finishSave = IntentGate()
        let cleaning = IntentGate()
        let finishCleanup = IntentGate()
        var events: [String] = []
        var enabled = false
        let oldStart = Task {
            try await intents.perform({ ticket in
                saving.open()
                await finishSave.wait()
                enabled = true // поздний ответ сохранения профиля системы
                events.append("saved-old")
                try intents.requireCurrent(ticket)
                events.append("started-old")
            }, cleanup: {
                events.append("cleanup-old")
                cleaning.open()
                await finishCleanup.wait()
                enabled = false
                events.append("cleaned-old")
            })
        }
        await saving.wait()
        let stop = Task {
            await intents.stop {
                enabled = false
                events.append("signed-out")
            }
        }
        await waitUntil { intents.current == 2 }
        let newStart = Task {
            try await intents.perform({ ticket in
                try intents.requireCurrent(ticket)
                XCTAssertFalse(enabled, "старый профиль должен быть выключен перед новым запуском")
                enabled = true
                events.append("started-new")
            }, cleanup: { events.append("cleanup-new"); enabled = false })
        }
        await waitUntil { intents.current == 3 }
        finishSave.open()
        await cleaning.wait()
        XCTAssertEqual(events, ["saved-old", "cleanup-old"])
        XCTAssertTrue(enabled, "очистка еще ждет нативного завершения")
        finishCleanup.open()
        do {
            try await oldStart.value
            XCTFail("отмененный запуск не должен завершаться успехом")
        } catch is CancellationError {} catch { XCTFail("неожиданная ошибка: \(error)") }
        await stop.value
        try await newStart.value
        XCTAssertEqual(events, ["saved-old", "cleanup-old", "cleaned-old", "signed-out", "started-new"])
        XCTAssertTrue(enabled)
    }

    @MainActor
    func testAnObsoleteQueuedStartCannotCleanOrStartANewerSession() async throws {
        let intents = TunnelIntentCoordinator()
        let entered = IntentGate()
        let release = IntentGate()
        var middleStarts = 0
        var middleCleanups = 0
        var active = false
        let first = Task {
            try await intents.perform({ ticket in
                entered.open()
                await release.wait()
                try intents.requireCurrent(ticket)
            }, cleanup: { active = false })
        }
        await entered.wait()
        let middle = Task {
            try await intents.perform({ _ in middleStarts += 1 }, cleanup: { middleCleanups += 1; active = false })
        }
        await waitUntil { intents.current == 2 }
        let latest = Task {
            try await intents.perform({ _ in active = true }, cleanup: { active = false })
        }
        await waitUntil { intents.current == 3 }
        release.open()
        do { try await first.value; XCTFail("прежнее намерение отменено") } catch is CancellationError {} catch { XCTFail("\(error)") }
        do { try await middle.value; XCTFail("очередь содержит устаревшее намерение") } catch is CancellationError {} catch { XCTFail("\(error)") }
        try await latest.value
        XCTAssertEqual(middleStarts, 0)
        XCTAssertEqual(middleCleanups, 0, "не начавшееся намерение не владеет системным профилем")
        XCTAssertTrue(active)
    }

    @MainActor
    private func waitUntil(_ condition: () -> Bool, file: StaticString = #filePath, line: UInt = #line) async {
        for _ in 0..<1_000 {
            if condition() { return }
            await Task.yield()
        }
        XCTFail("намерение не вошло в очередь", file: file, line: line)
    }
}

private struct PreferenceFailure: Error {}

@MainActor
private final class IntentGate {
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

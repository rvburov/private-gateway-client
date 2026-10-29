import Foundation
import XCTest
@testable import PrivateGatewayCore

private actor Gate {
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

private actor Switch {
    var value = false
    func set() { value = true }
}

final class SerialWorkTests: XCTestCase {
    private struct Failed: Error {}

    func testTheNextStartCannotRunUntilFailureCleanupHasFinished() async throws {
        let work = SerialWork()
        let cleanupStarted = Gate()
        let finishCleanup = Gate()
        let nextStarted = Switch()
        let first = Task {
            try await work.run({ throw Failed() }, onFailure: { _ in
                await cleanupStarted.open()
                await finishCleanup.wait()
            })
        }
        await cleanupStarted.wait()
        let second = Task {
            await work.run { await nextStarted.set() }
        }
        try await Task.sleep(nanoseconds: 50_000_000)
        let premature = await nextStarted.value
        XCTAssertFalse(premature, "cleanup прежнего ядра еще идет: новое запускать нельзя")
        await finishCleanup.open()
        do { _ = try await first.value; XCTFail("первая операция должна отказать") } catch {}
        await second.value
        let started = await nextStarted.value
        XCTAssertTrue(started)
    }

    func testACancelledWatchDoesNotReportTheResultOfItsOldProbe() async throws {
        let entered = Gate()
        let finish = Gate()
        let events = RecordedEvents()
        let watch = TunnelGuard(
            timer: SystemGuardTimer(), policy: { .never },
            probe: { await entered.open(); await finish.wait(); return false },
            restart: { _ in await events.mark(); return nil }, events: events
        )
        let task = Task { await watch.run() }
        await entered.wait()
        task.cancel()
        await finish.open()
        await task.value
        let count = await events.count
        XCTAssertEqual(count, 0, "запоздалая проба прежнего поколения не меняет состояние нового туннеля")
    }
}

private actor RecordedEvents: GuardEvents {
    var count = 0
    func mark() { count += 1 }
    func reconnecting(_ attempt: Int) { count += 1 }
    func protectedAgain() { count += 1 }
    func gaveUp(_ error: String) { count += 1 }
}

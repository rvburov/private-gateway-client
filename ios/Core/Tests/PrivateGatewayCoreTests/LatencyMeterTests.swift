import Foundation
import XCTest
@testable import PrivateGatewayCore

/// libXray без libXray: на каждый конфиг порции — задержка по порядку (10, 20, …), вызовы записываются; `during` — что
/// сделать во время вызова, `hold` — держать ответ, пока тест не отпустит.
private final class FakeCore: @unchecked Sendable {
    private let lock = NSLock()
    private var counts: [Int] = []
    private var answer: String?
    private var holding = false
    private var waiting: CheckedContinuation<Void, Never>?
    var during: (@Sendable () -> Void)?

    var calls: [Int] { locked { counts } }
    var waits: Bool { locked { waiting != nil } }

    /// Ответ вместо задержек — отказ вызова целиком.
    func fail(_ response: String) {
        locked { answer = response }
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

    func ping(_ request: String) async -> String? {
        let count = request.components(separatedBy: "xrayJson").count - 1
        let (hold, fixed) = locked { () -> (Bool, String?) in
            counts.append(count)
            return (holding, answer)
        }
        if hold {
            await withCheckedContinuation { continuation in
                locked { waiting = continuation }
            }
        }

        during?()
        if let fixed {
            return fixed
        }

        let results = (0..<count).map { #"{"success":true,"delay":\#(($0 + 1) * 10)}"# }.joined(separator: ",")
        return #"{"success":true,"data":{"results":[\#(results)]}}"#
    }

    private func locked<T>(_ body: () -> T) -> T {
        lock.lock()
        defer { lock.unlock() }
        return body()
    }
}

/// Защита: выключена, пока тест не включит.
private final class Protection: @unchecked Sendable {
    private let lock = NSLock()
    private var on = false

    var off: Bool {
        lock.lock()
        defer { lock.unlock() }
        return !on
    }

    func connect() {
        lock.lock()
        on = true
        lock.unlock()
    }
}

/// Замер задержки — те же случаи, что `LatencyMeterTest` Android: порции по пять через ядро, Hysteria2 в ядро не уходит,
/// под защитой не меряется, порция, во время которой началось подключение, отбрасывается, второе нажатие не считается,
/// отказ ядра записывается как отказ.
final class LatencyMeterTests: XCTestCase {
    private let core = FakeCore()
    private let protection = Protection()
    private lazy var servers = ServerStore(file: MemoryFile(), vault: MemoryVault())
    private lazy var settings = SettingsStore(file: MemoryFile())
    private lazy var meter = LatencyMeter(
        servers: servers, settings: settings, off: { [protection] in protection.off }, ping: { [core] in await core.ping($0) }
    )

    /// Семь серверов, что ядро умеет, и один Hysteria2 — его ядро не меряет.
    private func fill() async throws -> (supported: [String], hysteria: String) {
        let folder = LinkParserTests.testData.appendingPathComponent("links")
        let files = (FileManager.default.enumerator(atPath: folder.path)?.compactMap { $0 as? String } ?? []).filter { $0.hasSuffix(".txt") }.sorted()
        var seen = Set<String>()
        let all = files.compactMap { file in
            (try? String(contentsOf: folder.appendingPathComponent(file), encoding: .utf8)).flatMap {
                LinkParser.parse($0.trimmingCharacters(in: .whitespacesAndNewlines)).server
            }
        }
        .filter { seen.insert(ServerKeys.key($0)).inserted }
        let supported = all.filter { ($0.protocol == .vless || $0.protocol == .trojan) && PingBatch.outbounds($0, CoreSettings()).json != nil }.prefix(7)
        let hysteria = try XCTUnwrap(all.first { $0.protocol == .hysteria2 })
        XCTAssertEqual(supported.count, 7, "эталонов слишком мало")
        _ = try await servers.add(Array(supported) + [hysteria])
        let items = try await servers.servers()
        XCTAssertEqual(items.count, 8)
        let other = try XCTUnwrap(items.first { $0.protocol == .hysteria2 }).id
        return (items.map(\.id).filter { $0 != other }, other)
    }

    private func latencies() async throws -> [String: Latency?] {
        Dictionary(uniqueKeysWithValues: try await servers.servers().map { ($0.id, $0.latency) })
    }

    func testServersAreMeasuredFiveAtATimeThroughTheCoreAndHysteria2IsNotSent() async throws {
        let (supported, hysteria) = try await fill()
        let result = await meter.measure()
        XCTAssertEqual(result, .done)

        XCTAssertEqual(core.calls, [5, 2], "порции по 5, в libXray — только то, что ядро умеет")
        let items = try await latencies()
        XCTAssertEqual(supported.map { items[$0]??.ms }, [10, 20, 30, 40, 50, 10, 20])
        XCTAssertEqual(items[hysteria] ?? nil, Latency.failed(.unsupported))
        let measuring = await meter.measuring
        XCTAssertFalse(measuring)
    }

    func testNothingIsMeasuredUnderProtection() async throws {
        _ = try await fill()
        protection.connect()
        let result = await meter.measure()
        XCTAssertEqual(result, .busy)
        XCTAssertEqual(core.calls, [])
        let items = try await latencies()
        XCTAssertTrue(items.values.allSatisfy { $0 == nil })
    }

    func testAChunkMeasuredWhileProtectionCameUpIsDropped() async throws {
        let (supported, _) = try await fill()
        core.during = { [protection] in protection.connect() }
        let result = await meter.measure()
        XCTAssertEqual(result, .busy)

        XCTAssertEqual(core.calls, [5], "дальше порции не идут")
        let items = try await latencies()
        XCTAssertTrue(supported.allSatisfy { items[$0] ?? nil == nil }, "итогам порции рядом с ядром туннеля не верим")
    }

    func testASecondTapWhileMeasuringIsNotCounted() async throws {
        _ = try await fill()
        core.hold()
        let shared = self.meter
        let first = Task { await shared.measure() }
        for _ in 0..<500 where !core.waits {
            try await Task.sleep(nanoseconds: 10_000_000)
        }

        XCTAssertTrue(core.waits, "замер дошел до ядра")
        let measuring = await meter.measuring
        XCTAssertTrue(measuring)
        let second = await meter.measure()
        XCTAssertEqual(second, .running)
        core.release()
        let done = await first.value
        XCTAssertEqual(done, .done)
        let after = await meter.measuring
        XCTAssertFalse(after)
    }

    func testCoreFailureForAChunkIsRecordedAsSuch() async throws {
        let (supported, _) = try await fill()
        core.fail(#"{"success":false,"error":"isolated process"}"#)
        let result = await meter.measure()
        XCTAssertEqual(result, .done)
        let items = try await latencies()
        XCTAssertTrue(supported.allSatisfy { items[$0] ?? nil == Latency.failed(.coreFailed) })
        XCTAssertNil(items.values.compactMap { $0?.ms }.first)
    }

    func testEachChunkIsReportedAfterItIsWritten() async throws {
        let (supported, _) = try await fill()
        let seen = Seen()
        let result = await meter.measure { [servers] in
            let measured = ((try? await servers.servers()) ?? []).filter { $0.latency?.ms != nil }.count
            seen.add(measured)
        }

        XCTAssertEqual(result, .done)
        XCTAssertEqual(seen.values, [5, 7], "окно перечитывает список после каждой порции")
        XCTAssertEqual(supported.count, 7)
    }
}

/// Что видело окно после порций.
private final class Seen: @unchecked Sendable {
    private let lock = NSLock()
    private var list: [Int] = []

    var values: [Int] {
        lock.lock()
        defer { lock.unlock() }
        return list
    }

    func add(_ value: Int) {
        lock.lock()
        list.append(value)
        lock.unlock()
    }
}

import Foundation
import XCTest
@testable import PrivateGatewayCore

/// Журнал расширения: время по UTC, без секретов, одной строкой, не больше последних 200 строк.
final class TunnelLogTests: XCTestCase {
    func testLinesAreStampedMaskedAndKeptShort() {
        let file = MemoryFile()
        let log = TunnelLog(file: file, clock: { Date(timeIntervalSince1970: 1_793_029_320) })
        XCTAssertEqual(log.lines(), [])

        log.add("start: vless://11111111-2222-3333-4444-555555555555@203.0.113.10:443?security=reality#NL")
        log.add("reload\nfailed")
        let lines = log.lines()
        XCTAssertEqual(lines.count, 2)
        XCTAssertTrue(lines[0].hasPrefix("2026-10-26 "), lines[0])
        XCTAssertFalse(lines[0].contains("11111111-2222-3333-4444-555555555555"), "ключ сервера в журнал не попадает")
        XCTAssertFalse(lines[0].contains("203.0.113.10"), "и адрес сервера тоже")
        XCTAssertTrue(lines[1].hasSuffix("reload failed"), "перевод строки не разрывает запись")

        for index in 0..<(TunnelLog.limit + 50) {
            log.add("event \(index)")
        }

        let kept = log.lines()
        XCTAssertEqual(kept.count, TunnelLog.limit)
        XCTAssertTrue(kept.last?.hasSuffix("event \(TunnelLog.limit + 49)") ?? false)
        XCTAssertTrue(kept.first?.hasSuffix("event 50") ?? false, "старые строки уходят")
    }
}

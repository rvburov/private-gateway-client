import Foundation
import XCTest
@testable import PrivateGatewayCore

/// Части отчета «Проверки» — как `DnsReport` и `ProblemLines` десктопа: вид своего DNS без адреса и ошибки журнала с повторами.
final class ReportPartsTests: XCTestCase {
    func testCustomDnsKindNeverShowsTheAddress() {
        let cases: [(String?, String)] = [
            (nil, "missing"), ("  ", "missing"), ("192.168.1.53", "private"), ("198.51.100.53", "address"), ("2001:db8::53", "address"),
            ("https://198.51.100.53/dns-query?token=secret", "doh"), ("dns.example.com", "invalid"), ("https://dns.example.com/q", "invalid"),
        ]
        for (custom, kind) in cases {
            XCTAssertEqual(ReportParts.customKind(TunnelSettings(dns: .custom, customDns: custom)), kind, custom ?? "nil")
        }
    }

    func testProblemsAreGroupedWithRepeatsInFirstSeenOrder() {
        let start = Date(timeIntervalSince1970: 1_700_000_000)
        let entries = [
            TunnelLogEntry(time: start, level: .error, message: "start: PGC-NET-001 no answer"),
            TunnelLogEntry(time: start.addingTimeInterval(1), level: .info, message: "connected"),
            TunnelLogEntry(time: start.addingTimeInterval(2), level: .warning, message: "reconnect 1: PGC-SVC-002"),
            TunnelLogEntry(time: start.addingTimeInterval(3), level: .error, message: "start: PGC-NET-001 no answer"),
            TunnelLogEntry(time: start.addingTimeInterval(4), level: .debug, message: "probe"),
            TunnelLogEntry(time: start.addingTimeInterval(5), level: .error, message: "start: PGC-NET-001 no answer"),
        ]
        let problems = ReportParts.problems(entries)
        XCTAssertEqual(problems.map(\.message), ["start: PGC-NET-001 no answer", "reconnect 1: PGC-SVC-002"])
        XCTAssertEqual(problems.map(\.count), [3, 1])
        XCTAssertEqual(problems[0].first.time, start)
        XCTAssertEqual(problems[0].last.time, start.addingTimeInterval(5))
        XCTAssertTrue(ReportParts.problems([TunnelLogEntry(time: nil, level: .info, message: "ok")]).isEmpty)
        let secret = ReportParts.problems([TunnelLogEntry(time: nil, level: .error, message: "bad 11111111-2222-3333-4444-555555555555")])
        XCTAssertFalse(secret[0].message.contains("11111111-2222"), "текст проходит через SecretMasker")
    }
}

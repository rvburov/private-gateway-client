import Foundation
import XCTest
@testable import PrivateGatewayCore

/// Состояние туннеля в общей папке: расширение пишет, приложение читает; непонятное — nil, а не выдуманное состояние.
final class TunnelStatusFileTests: XCTestCase {
    private let file = MemoryFile()
    private lazy var status = TunnelStatusFile(file: file)

    func testNoFileIsNil() {
        XCTAssertNil(status.read())
    }

    func testStatusComesBackAsWritten() throws {
        let since = Date(timeIntervalSince1970: 1_793_029_320.5)
        let connected = TunnelStatus(state: .connected, serverId: "server-1", since: since)
        try status.write(connected)
        XCTAssertEqual(status.read(), connected)

        let failed = TunnelStatus(state: .error, error: TunnelErrors.coreFailed)
        try status.write(failed)
        XCTAssertEqual(TunnelStatusFile(file: file).read(), failed)
    }

    func testEveryStateSurvivesTheFile() throws {
        for state in TunnelState.allCases {
            try status.write(TunnelStatus(state: state, attempt: state == .reconnecting ? 2 : 0))
            XCTAssertEqual(status.read()?.state, state)
        }
    }

    func testUnknownStateOrBrokenFileIsNil() throws {
        try file.write(Data(#"{"state": "blocking", "attempt": 0}"#.utf8))
        XCTAssertNil(status.read(), "состояние из будущей версии")

        try file.write(Data("не JSON".utf8))
        XCTAssertNil(status.read())
    }
}

import Foundation
import XCTest
@testable import PrivateGatewayCore

final class ServerNameDisplayTests: XCTestCase {
    private struct Fixture: Decodable {
        let input: String
        let expected: String
    }

    func testDisplaySpacingPreservesGraphemesAndOriginalNamesOnSharedFixtures() throws {
        let data = try Data(contentsOf: LinkParserTests.testData.appendingPathComponent("server-name-display.json"))
        let fixtures = try JSONDecoder().decode([Fixture].self, from: data)
        for fixture in fixtures {
            XCTAssertEqual(ServerNames.forDisplay(fixture.input), fixture.expected, fixture.input)
            XCTAssertEqual(ServerNames.forDisplay(fixture.expected), fixture.expected, "повтор не добавляет пробелы")
            let server = ServerItem(
                id: "id", linkName: fixture.input, countryCode: "", protocol: .vless, security: .reality, transport: .tcp, port: 443
            )
            XCTAssertEqual(server.title, fixture.expected)
            XCTAssertEqual(server.displayName, fixture.input, "редактор и экспорт видят исходное имя")
        }
    }
}

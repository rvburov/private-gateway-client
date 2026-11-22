import Foundation
import XCTest
@testable import PrivateGatewayCore

final class TransportCompatibilityTests: XCTestCase {
    func testSensitiveHeadersIncludeNestedDownloadWithoutTreatingOtherFieldsAsCredentials() throws {
        var server = try fixture("xhttp-extra")
        server.transport.extraJson = #"{"headers":{"X-Private-Key":"demo-secret","Authorization":"Bearer demo-bearer"},"xPaddingKey":"public-padding","downloadSettings":{"network":"xhttp","xhttpSettings":{"extra":{"headers":{"Cookie":"demo-cookie"}}}}}"#
        XCTAssertEqual(Set(TransportParameterRules.sensitiveExtraValues(server)), Set(["demo-secret", "Bearer demo-bearer", "demo-cookie"]))
        server.transport.extraJson = #"{"headers":{"X-Key":"untrusted"},"sockopt":{}}"#
        XCTAssertTrue(TransportParameterRules.sensitiveExtraValues(server).isEmpty)
    }

    func testExtraAndAlpnSurviveStoredLinksAndReachOnlyXhttpSettings() throws {
        let original = try fixture("xhttp-extra")
        let server = try XCTUnwrap(LinkParser.parse(LinkWriter.write(original)).server)
        XCTAssertEqual(original, server)
        XCTAssertEqual(server.security.alpn, ["h3"])
        let proxy = try outbound(server)
        let stream = try XCTUnwrap(proxy["streamSettings"] as? [String: Any])
        let xhttp = try XCTUnwrap(stream["xhttpSettings"] as? [String: Any])
        let extra = try XCTUnwrap(xhttp["extra"] as? [String: Any])
        XCTAssertEqual(extra["noGRPCHeader"] as? Bool, true)
        XCTAssertEqual((extra["xmux"] as? [String: Any])?["maxConcurrency"] as? String, "4-8")
        XCTAssertEqual((extra["downloadSettings"] as? [String: Any])?["address"] as? String, "download.example.com")
    }

    func testVlessEncryptionIsNotDowngradedToNoneAndWorksWithoutTls() throws {
        let server = try fixture("encryption-mlkem")
        XCTAssertFalse(XrayConfig.unencrypted(server))
        let proxy = try outbound(server)
        let settings = try XCTUnwrap(proxy["settings"] as? [String: Any])
        let vnext = try XCTUnwrap(settings["vnext"] as? [[String: Any]])
        let user = try XCTUnwrap((vnext[0]["users"] as? [[String: Any]])?.first)
        XCTAssertEqual(user["encryption"] as? String, server.options.encryption)
    }

    func testUnsafeExtrasAndUnsupportedEncryptionAreRejectedBeforeCore() throws {
        var server = try fixture("xhttp-extra")
        let unsafe = "{\"downloadSettings\":{\"sockopt\":{\"dialerProxy\":\"direct\"}}}"
        XCTAssertNil(TransportParameterRules.normalizeExtra(unsafe))
        server.transport.extraJson = unsafe
        XCTAssertEqual(XrayConfig.build(server, CoreSettings(), tunFd: 42).rejection, .unsupportedTransport)
        server = try fixture("xhttp-extra")
        server.options.encryption = "unknown"
        XCTAssertEqual(XrayConfig.build(server, CoreSettings(), tunFd: 42).rejection, .unsupportedTransport)
        XCTAssertEqual(LinkParser.parse(LinkWriter.write(server)).error, ImportErrors.unsupportedParameter)
        XCTAssertFalse(TransportParameterRules.validEncryption("mlkem768x25519plus.native.1rtt.bad"))
    }

    private func fixture(_ name: String) throws -> ServerDefinition {
        let link = try String(contentsOf: LinkParserTests.testData.appendingPathComponent("links/vless/\(name).txt"), encoding: .utf8)
        return try XCTUnwrap(LinkParser.parse(link).server)
    }

    private func outbound(_ server: ServerDefinition) throws -> [String: Any] {
        let text = try XCTUnwrap(XrayConfig.build(server, CoreSettings(), tunFd: 42).json)
        let config = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(text.utf8)) as? [String: Any])
        let outbounds = try XCTUnwrap(config["outbounds"] as? [[String: Any]])
        return try XCTUnwrap(outbounds.first { $0["tag"] as? String == "proxy" })
    }
}

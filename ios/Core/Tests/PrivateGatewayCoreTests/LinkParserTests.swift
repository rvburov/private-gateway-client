import Foundation
import XCTest
@testable import PrivateGatewayCore

/// Разбор ссылок и подписок на эталонах shared/testdata — тех же, что у десктопа (`LinkParserTests`) и Android
/// (`LinkParserTest`). Результат переводится в тот же JSON-вид, что в .expected.json: пустые и ложные поля не пишутся;
/// сравниваются тексты JSON с упорядоченными ключами.
final class LinkParserTests: XCTestCase {
    /// ios/Core/Tests/PrivateGatewayCoreTests/этот файл → корень репозитория → shared/testdata.
    static let testData: URL = {
        var url = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 {
            url.deleteLastPathComponent()
        }

        return url.appendingPathComponent("shared/testdata")
    }()

    func testLinkParsesAsInTheSharedFixture() throws {
        let fixtures = try cases("links")
        XCTAssertGreaterThan(fixtures.count, 40, "эталоны найдены")
        for fixture in fixtures {
            let (input, expected) = try load(fixture)
            XCTAssertEqual(try canonical(toJson(LinkParser.parse(input))), expected, fixture)
        }
    }

    func testWrittenLinkParsesBackToTheSameServer() throws {
        for fixture in try cases("links") {
            let parsed = LinkParser.parse(try load(fixture).0)
            guard let server = parsed.server else {
                continue
            }

            let written = LinkWriter.write(server)
            XCTAssertEqual(try canonical(toJson(LinkParser.parse(written))), try canonical(toJson(parsed)), "\(fixture): \(written)")
        }
    }

    func testWrittenLinkHasTheCommonForm() {
        // Shadowsocks 2022 — открытым текстом (SIP022), обычные шифры — base64url без «=» (SIP002); флаг страны — в названии.
        let cases = [
            (
                "ss://2022-blake3-aes-128-gcm:AAAAAAAAAAAAAAAAAAAAAA%3D%3D@192.0.2.1:8388#SS",
                "ss://2022-blake3-aes-128-gcm:AAAAAAAAAAAAAAAAAAAAAA%3D%3D@192.0.2.1:8388#SS"
            ),
            ("ss://YWVzLTI1Ni1nY206cHctMQ@192.0.2.1:8388#SS", "ss://YWVzLTI1Ni1nY206cHctMQ@192.0.2.1:8388#SS"),
            (
                "vless://11111111-2222-3333-4444-555555555555@[2001:db8::1]:443?security=tls&sni=example.com#%F0%9F%87%B3%F0%9F%87%B1%20NL",
                "vless://11111111-2222-3333-4444-555555555555@[2001:db8::1]:443?encryption=none&type=tcp&security=tls&sni=example.com#%F0%9F%87%B3%F0%9F%87%B1%20NL"
            ),
        ]
        for (link, written) in cases {
            XCTAssertEqual(LinkWriter.write(LinkParser.parse(link).server!), written)
        }
    }

    func testSubscriptionParsesAsInTheSharedFixture() throws {
        let fixtures = try cases("subscriptions")
        XCTAssertGreaterThan(fixtures.count, 5, "эталоны найдены")
        for fixture in fixtures {
            let (input, expected) = try load(fixture)
            let batch = ImportParser.parse(input)

            // У конфигов sing-box и Clash сверяется весь сервер: поля в них названы иначе, чем в ссылке.
            let whole = batch.format == ImportParser.formatSingBox || batch.format == ImportParser.formatClash
            let lines: [[String: Any]] = batch.lines.map { line in
                var json: [String: Any] = ["line": line.number]
                if let server = line.result.server {
                    if whole {
                        json["server"] = toJson(line.result)
                    } else {
                        json["protocol"] = name(server.protocol)
                        json["name"] = server.name
                    }
                } else {
                    json["error"] = line.result.error
                }

                return json
            }
            let actual: [String: Any] = ["format": batch.format, "lines": lines]
            XCTAssertEqual(try canonical(actual), expected, fixture)
        }
    }

    func testEveryProtocolAndErrorCodeHasAFixture() throws {
        var protocols = Set<String>()
        var errors = Set<String>()
        for fixture in try cases("links") {
            let url = Self.testData.appendingPathComponent(fixture + ".expected.json")
            let json = try JSONSerialization.jsonObject(with: Data(contentsOf: url)) as? [String: Any]
            if let proto = json?["protocol"] as? String {
                protocols.insert(proto)
            }

            if let error = json?["error"] as? String {
                errors.insert(error)
            }
        }

        XCTAssertEqual(protocols, Set(ProxyProtocol.allCases.map(name)))
        XCTAssertEqual(errors, Set(ImportErrors.all))
    }

    func testPrivateGatewayFormatsGiveOneAndTheSameServer() throws {
        // Название в sing-box — тег «gateway», spx есть только в ссылке; остальное обязано совпасть.
        let servers = try ["base64", "uri", "sing-box", "mihomo"].map { format -> ServerDefinition in
            let text = try String(contentsOf: Self.testData.appendingPathComponent("subscriptions/private-gateway/\(format).txt"), encoding: .utf8)
            let batch = ImportParser.parse(text)
            XCTAssertEqual(batch.lines.count, 1, format)
            var server = try XCTUnwrap(batch.lines.first?.result.server, format)
            server.name = ""
            server.security.realitySpiderX = nil
            return server
        }

        XCTAssertEqual(Set(servers).count, 1, "\(servers)")
    }

    func testCredentialsNeverShowUpInText() {
        let server = LinkParser.parse("trojan://pw-very-secret@example.com:443#X").server!

        XCTAssertFalse(String(describing: server).contains("pw-very-secret"))
        XCTAssertFalse(String(reflecting: server).contains("pw-very-secret"))
        XCTAssertEqual(String(describing: server.credentials), "ServerCredentials { *** }")
    }

    func testLeadingFlagBecomesCountryCode() {
        let cases = [("🇳🇱 Нидерланды 01", "NL", "Нидерланды 01"), ("🇺🇸", "US", ""), ("Нидерланды 🇳🇱", "", "Нидерланды 🇳🇱"), ("  Без флага ", "", "Без флага")]
        for (name, country, rest) in cases {
            let (code, clean) = CountryFlag.split(name)
            XCTAssertEqual(code, country, name)
            XCTAssertEqual(clean, rest, name)
        }
    }

    func testPercentDecodingKeepsPlusAndBrokenSequences() {
        XCTAssertEqual(LinkParser.unescape("a+b"), "a+b")
        XCTAssertEqual(LinkParser.unescape("%D0%9D%D0%B8%D0%B4%D0%B5%D1%80%D0%BB%D0%B0%D0%BD%D0%B4%D1%8B"), "Нидерланды")
        XCTAssertEqual(LinkParser.unescape("%zz%D0"), "%zz%D0", "неверная последовательность остается как есть")
        XCTAssertEqual(LinkParser.escape("a b/+="), "a%20b%2F%2B%3D")
    }

    // MARK: Помощники

    private func cases(_ folder: String) throws -> [String] {
        let root = Self.testData.standardizedFileURL.path
        guard let walker = FileManager.default.enumerator(atPath: Self.testData.appendingPathComponent(folder).path) else {
            XCTFail("Нет папки эталонов \(root)/\(folder)")
            return []
        }

        var result: [String] = []
        while let relative = walker.nextObject() as? String {
            if relative.hasSuffix(".txt") {
                result.append(folder + "/" + String(relative.dropLast(4)))
            }
        }

        return result.sorted()
    }

    private func load(_ fixture: String) throws -> (String, String) {
        let base = Self.testData.appendingPathComponent(fixture)
        let input = try String(contentsOf: URL(fileURLWithPath: base.path + ".txt"), encoding: .utf8)
        let expected = try JSONSerialization.jsonObject(with: Data(contentsOf: URL(fileURLWithPath: base.path + ".expected.json")))
        return (input, try canonical(expected))
    }

    /// JSON с упорядоченными ключами — для сравнения текстов.
    private func canonical(_ value: Any) throws -> String {
        let data = try JSONSerialization.data(withJSONObject: value, options: [.sortedKeys])
        return String(decoding: data, as: UTF8.self)
    }

    private func name(_ proto: ProxyProtocol) -> String {
        proto.rawValue.lowercased()
    }

    private func toJson(_ result: LinkResult) -> [String: Any] {
        guard let s = result.server else {
            return ["error": result.error ?? ""]
        }

        var json: [String: Any] = ["name": s.name, "protocol": name(s.protocol), "host": s.host, "port": s.port]
        put(&json, "country", s.countryCode)

        var transport: [String: Any] = ["kind": s.transport.kind == .webSocket ? "ws" : s.transport.kind.rawValue.lowercased()]
        put(&transport, "path", s.transport.path)
        put(&transport, "host", s.transport.host)
        put(&transport, "serviceName", s.transport.serviceName)
        json["transport"] = transport

        var security: [String: Any] = ["kind": s.security.kind.rawValue]
        put(&security, "sni", s.security.sni)
        put(&security, "fingerprint", s.security.fingerprint)
        put(&security, "alpn", s.security.alpn)
        if s.security.allowInsecure {
            security["allowInsecure"] = true
        }

        put(&security, "realityPublicKey", s.security.realityPublicKey)
        put(&security, "realityShortId", s.security.realityShortId)
        put(&security, "realitySpiderX", s.security.realitySpiderX)
        json["security"] = security

        var options: [String: Any] = [:]
        put(&options, "flow", s.options.flow)
        put(&options, "cipher", s.options.cipher)
        put(&options, "obfs", s.options.obfs)
        put(&options, "congestionControl", s.options.congestionControl)
        put(&options, "peerPublicKey", s.options.peerPublicKey)
        put(&options, "addresses", s.options.addresses)
        if let mtu = s.options.mtu {
            options["mtu"] = mtu
        }

        if !options.isEmpty {
            json["options"] = options
        }

        var credentials: [String: Any] = [:]
        put(&credentials, "uuid", s.credentials.uuid)
        put(&credentials, "password", s.credentials.password)
        put(&credentials, "obfsPassword", s.credentials.obfsPassword)
        put(&credentials, "privateKey", s.credentials.privateKey)
        put(&credentials, "presharedKey", s.credentials.presharedKey)
        json["credentials"] = credentials
        return json
    }

    private func put(_ json: inout [String: Any], _ key: String, _ value: String?) {
        if let value, !value.isEmpty {
            json[key] = value
        }
    }

    private func put(_ json: inout [String: Any], _ key: String, _ values: [String]?) {
        if let values, !values.isEmpty {
            json[key] = values
        }
    }
}

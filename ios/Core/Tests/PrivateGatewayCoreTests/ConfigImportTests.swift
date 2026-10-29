import XCTest
@testable import PrivateGatewayCore

/// Конфиги sing-box и Clash сверх эталонов — как `ConfigImportTest` Android: ошибки с номером строки, порядок строк, BOM.
/// Подписка приходит из сети — ни вложенность, ни ссылки-«бомбы» YAML не должны уронить программу или занять память.
final class ConfigImportTests: XCTestCase {
    private let vless = #"{ "type": "vless", "tag": "A", "server": "192.0.2.1", "server_port": 443, "uuid": "11111111-2222-3333-4444-555555555555" }"#

    func testBrokenJsonIsOneErrorOnItsLine() {
        let batch = ImportParser.parse("{\n  \"outbounds\": [\n    { \"type\": \"vless\" \n    \"tag\": \"A\" }\n  ]\n}")

        XCTAssertEqual(batch.format, ImportParser.formatSingBox)
        XCTAssertEqual(batch.lines, [ImportLine(number: 4, result: LinkParser.fail(ImportErrors.badEncoding))])
    }

    func testBrokenYamlIsOneErrorOnItsLine() {
        let cases = [
            ("proxies:\n  - name: A\n    type: vless\n   bad: x\n", 4),
            ("proxies:\n  - name: A\n    port: *nope\n", 3),
            ("proxies:\n  - {name: A, type: vless\n  - name: B\n", 2),
        ]
        for (text, line) in cases {
            let batch = ImportParser.parse(text)

            XCTAssertEqual(batch.format, ImportParser.formatClash, text)
            XCTAssertEqual(batch.lines, [ImportLine(number: line, result: LinkParser.fail(ImportErrors.badEncoding))], text)
        }
    }

    func testPlainArrayOfOutboundsAndBomAreUnderstood() {
        let batch = ImportParser.parse("\u{FEFF}[\n\(vless)\n]")

        XCTAssertEqual(batch.format, ImportParser.formatSingBox)
        XCTAssertEqual(batch.lines.map(\.number), [2])
        XCTAssertEqual(batch.lines.map { $0.result.server?.protocol }, [.vless])
    }

    func testLinesGoInTextOrderEvenWhenEndpointsComeFirst() {
        let batch = ImportParser.parse("{\n\"endpoints\": [{ \"type\": \"socks\" }],\n\"outbounds\": [\n\(vless)\n]}")

        XCTAssertEqual(batch.lines.map(\.number), [2, 4])
    }

    func testCrlfLinesAreCountedLikeLf() {
        let batch = ImportParser.parse("trojan://x@192.0.2.1:443#A\r\n\r\ntrojan://y@192.0.2.2:443#B\r\n")

        XCTAssertEqual(batch.lines.map(\.number), [1, 3])
        XCTAssertEqual(batch.lines.map { $0.result.server?.name }, ["A", "B"])
    }

    func testHysteria2AndTuicKeepTlsFieldsWithoutEnabledFlag() {
        let batch = ImportParser.parse(
            """
            [
              { "type": "hysteria2", "server": "192.0.2.1", "server_port": 443, "password": "x", "tls": { "server_name": "hy.example.com" } },
              { "type": "tuic", "server": "192.0.2.1", "server_port": 443, "uuid": "11111111-2222-3333-4444-555555555555", "password": "x",
                "tls": { "server_name": "tuic.example.com" } }
            ]
            """
        )

        XCTAssertEqual(batch.lines.map { $0.result.server?.security.sni }, ["hy.example.com", "tuic.example.com"])
    }

    func testDeepJsonIsRefusedNotOverflowed() {
        let batch = ImportParser.parse("{\"outbounds\": " + String(repeating: "[", count: 10_000) + String(repeating: "]", count: 10_000) + "}")

        XCTAssertEqual(batch.lines.map { $0.result.error }, [ImportErrors.badEncoding])
    }

    func testDeepYamlIsRefusedNotOverflowed() {
        let flow = ImportParser.parse("proxies: " + String(repeating: "[", count: 10_000) + String(repeating: "]", count: 10_000) + "\n")
        XCTAssertEqual(flow.lines.map { $0.result.error }, [ImportErrors.badEncoding])

        // Вложенность отступами: «- » на каждом уровне.
        var block = "proxies:\n"
        for level in 0..<2_000 {
            block += String(repeating: " ", count: level) + "-\n"
        }

        XCTAssertEqual(ImportParser.parse(block).lines.map { $0.result.error }, [ImportErrors.badEncoding])
    }

    func testYamlAliasBombIsRefusedBeforeItTakesMemory() {
        // Каждый уровень — десять ссылок на предыдущий: 10⁹ узлов, если копировать без счета.
        var text = "a0: &a0 [x, x, x, x, x, x, x, x, x, x]\n"
        for level in 1...9 {
            text += "a\(level): &a\(level) [" + Array(repeating: "*a\(level - 1)", count: 10).joined(separator: ", ") + "]\n"
        }

        text += "proxies: *a9\n"

        let batch = ImportParser.parse(text)

        XCTAssertEqual(batch.lines.map { $0.result.error }, [ImportErrors.badEncoding])
    }

    func testEscapedBackslashBeforeAQuoteCannotHideDeepYaml() {
        // Валидная первая строка: две косые черты в YAML — одна черта в значении, кавычка закрывает строку. Раньше
        // сканер считал ее экранированной и не видел следующие 100 уровней, до рекурсивного Parser.nextRoot.
        for slashes in [2, 4, 6] {
            let prefix = "note: \"" + String(repeating: "\\", count: slashes) + "\"\n"
            let text = prefix + "proxies: " + String(repeating: "[", count: 100) + "0" + String(repeating: "]", count: 100)
            XCTAssertEqual(YamlTree.tooDeep(text), 2)
            XCTAssertEqual(ImportParser.parse(text).lines.map { $0.result.error }, [ImportErrors.badEncoding])
        }
        // Нечетное число черт действительно экранирует кавычку: скобки внутри строки не считаются.
        XCTAssertNil(YamlTree.tooDeep("note: \"a\\\"" + String(repeating: "[", count: 100) + "\"\nproxies: []"))
    }

    func testYamlMergeKeepsExplicitKeys() throws {
        let batch = ImportParser.parse(
            """
            base: &base {type: trojan, port: 1, sni: from-base.example.com}
            proxies:
              - {<<: *base, name: A, server: 192.0.2.1, port: 443, password: x}
            """
        )

        let server = try XCTUnwrap(batch.lines.first?.result.server)
        XCTAssertEqual(batch.lines.count, 1)
        XCTAssertEqual(server.protocol, .trojan)
        XCTAssertEqual(server.port, 443)
        XCTAssertEqual(server.security.sni, "from-base.example.com")
    }

    func testYamlTildeAndNullAreMissingValuesButQuotedOnesAreText() throws {
        let batch = ImportParser.parse(
            """
            proxies:
              - {name: A, type: trojan, server: 192.0.2.1, port: 443, password: x, sni: ~, client-fingerprint: null}
              - {name: B, type: trojan, server: 192.0.2.1, port: 443, password: "~"}
            """
        )

        let first = try XCTUnwrap(batch.lines.first?.result.server)
        XCTAssertNil(first.security.sni)
        XCTAssertNil(first.security.fingerprint)
        XCTAssertEqual(batch.lines[1].result.server?.credentials.password, "~")
    }

    func testLinksWithAWordProxiesStayLinks() {
        XCTAssertEqual(ImportParser.parse("trojan://x@192.0.2.1:443#proxies: test").format, ImportParser.formatLinks)
    }

    func testConfigWithoutServersFindsNothing() {
        XCTAssertTrue(ImportParser.parse(#"{ "outbounds": [{ "type": "direct" }] }"#).lines.isEmpty)
        XCTAssertTrue(ImportParser.parse("proxies:\nrules: []\n").lines.isEmpty)
        XCTAssertTrue(ImportParser.parse("[]").lines.isEmpty)
    }
}

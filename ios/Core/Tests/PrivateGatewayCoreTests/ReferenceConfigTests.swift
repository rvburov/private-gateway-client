import Foundation
import XCTest
@testable import PrivateGatewayCore

/// Регрессия этапа 11: конфиг каждой эталонной ссылки shared/testdata/links читает само ядро телефона — Xray-core той
/// версии, что внутри libXray (`scripts/get-xray-mobile.sh`; коммит libXray у iOS тот же, что у Android): `xray run -test`
/// собирает обработчики, как `TestXray` libXray, без запуска. Туннель, замер задержки, настройки по умолчанию и все
/// включенное. Тот же тест у Android — `ReferenceConfigTest`, у десктопа — `ReferenceConfigTests`.
final class ReferenceConfigTests: XCTestCase {
    /// Все, что по умолчанию выключено и меняет конфиг: свой DNS в сети, IPv6, локальная сеть, Mux, отпечаток, журнал.
    private let everything = CoreSettings(
        dns: "https://dns.quad9.net/dns-query", allowLan: true, ipv6: true, mux: true, fingerprint: "random", sniffing: false, mtu: 1400,
        debugLog: true, lanDns: "192.168.1.53"
    )

    func testXrayOfLibXrayAcceptsTheConfigOfEachReferenceLink() throws {
        let xray = try self.xray()
        let fixtures = try self.fixtures()
        XCTAssertGreaterThanOrEqual(fixtures.count, 30, "эталоны найдены")
        for (fixture, link) in fixtures {
            guard let server = LinkParser.parse(link).server else {
                continue
            }

            let tunnel = XrayConfig.build(server, CoreSettings(), tunFd: 42)
            // Протокол или транспорт, которых у ядра телефона нет, — отказ до ядра, и он должен быть единственной причиной.
            if let rejection = tunnel.rejection {
                XCTAssertNotEqual(rejection, .missingCredentials, "\(fixture): эталон с данными доступа отвергнут")
                XCTAssertEqual(PingBatch.outbounds(server, CoreSettings()).rejection, rejection, fixture)
                continue
            }

            try accepted(xray, try XCTUnwrap(tunnel.json), "\(fixture), туннель")
            try accepted(xray, try XCTUnwrap(XrayConfig.build(server, everything, tunFd: 42).json), "\(fixture), туннель, все включено")
            try accepted(xray, try XCTUnwrap(PingBatch.outbounds(server, CoreSettings()).json), "\(fixture), замер задержки")
            try accepted(xray, try XCTUnwrap(PingBatch.outbounds(server, everything).json), "\(fixture), замер задержки, все включено")
        }
    }

    /// Эталонные ссылки — файлы .txt в shared/testdata/links, текст без пробелов по краям, как у `PingBatchTests`.
    private func fixtures() throws -> [(String, String)] {
        let links = LinkParserTests.testData.appendingPathComponent("links")
        let enumerator = try XCTUnwrap(FileManager.default.enumerator(at: links, includingPropertiesForKeys: nil))
        var found: [(String, String)] = []
        for case let url as URL in enumerator where url.pathExtension == "txt" {
            let name = url.deletingPathExtension().lastPathComponent
            let folder = url.deletingLastPathComponent().lastPathComponent
            let text = try String(contentsOf: url, encoding: .utf8).trimmingCharacters(in: .whitespacesAndNewlines)
            found.append(("links/\(folder)/\(name)", text))
        }

        return found.sorted { $0.0 < $1.0 }
    }

    /// Xray libXray: artifacts/cores/xray-mobile. Нет файла — тест пропускается с причиной, а в CI (`PG_REQUIRE_XRAY_MOBILE=1`)
    /// падает: проверка не может тихо перестать идти.
    private func xray() throws -> URL {
        #if os(macOS) || os(Linux)
            let root = LinkParserTests.testData.deletingLastPathComponent().deletingLastPathComponent()
            let file = root.appendingPathComponent("artifacts/cores/xray-mobile/xray")
            let exists = FileManager.default.isExecutableFile(atPath: file.path)
            if ProcessInfo.processInfo.environment["PG_REQUIRE_XRAY_MOBILE"] == "1", !exists {
                XCTFail("Нет Xray libXray: \(file.path) — scripts/get-xray-mobile.sh")
            }

            try XCTSkipUnless(exists, "нет Xray libXray — scripts/get-xray-mobile.sh")
            return file
        #else
            throw XCTSkip("программы запускаются только на macOS и Linux")
        #endif
    }

    private func accepted(_ xray: URL, _ config: String, _ what: String) throws {
        #if os(macOS) || os(Linux)
            let file = FileManager.default.temporaryDirectory.appendingPathComponent("xray-\(UUID().uuidString).json")
            try Data(config.utf8).write(to: file)
            defer { try? FileManager.default.removeItem(at: file) }
            let process = Process()
            process.executableURL = xray
            process.arguments = ["run", "-test", "-c", file.path]
            let output = Pipe()
            process.standardOutput = output
            process.standardError = output
            try process.run()
            let text = String(decoding: output.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
            process.waitUntilExit()
            XCTAssertEqual(process.terminationStatus, 0, "\(what): ядро не приняло конфиг\n\(text)\n\(config)")
        #endif
    }
}

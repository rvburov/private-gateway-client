import Foundation
import XCTest
@testable import PrivateGatewayCore

/// Шаги расширения туннеля до маршрутов и ядра — те же коды, что у `start` Android: нет сервера — PGC-SVC-003, ядро не
/// берется — PGC-SVC-005, файлы не прочитать или ядро не запустилось — PGC-SVC-002.
final class TunnelLaunchTests: XCTestCase {
    private let vault = MemoryVault()
    private lazy var servers = ServerStore(file: MemoryFile(), vault: vault)
    private lazy var settings = SettingsStore(file: MemoryFile())

    static func fixture(_ path: String) throws -> ServerDefinition {
        let text = try String(contentsOf: LinkParserTests.testData.appendingPathComponent("links/\(path).txt"), encoding: .utf8)
        return try XCTUnwrap(LinkParser.parse(text.trimmingCharacters(in: .whitespacesAndNewlines)).server, path)
    }

    /// Добавить сервер и выбрать его; номер — из хранилища.
    private func select(_ server: ServerDefinition) async throws -> String {
        try await servers.add([server])
        let items = try await servers.servers()
        let id = try XCTUnwrap(items.last?.id)
        try await settings.select(id)
        return id
    }

    private func failure(_ body: () async throws -> Void) async -> TunnelFailure? {
        do {
            try await body()
            return nil
        } catch {
            return error as? TunnelFailure
        }
    }

    func testSelectedServerIsPreparedWithItsSettings() async throws {
        let server = try Self.fixture("vless/reality-vision")
        let id = try await select(server)
        let tunnel = TunnelSettings(dns: .quad9Doh, ipv6: true, allowLan: true)
        try await settings.setTunnel(tunnel)

        let launch = try await TunnelLaunch.prepare(settings: settings, servers: servers)
        XCTAssertEqual(launch.serverId, id)
        XCTAssertEqual(launch.server, server)
        XCTAssertEqual(launch.core, tunnel.core())
        XCTAssertEqual(launch.plan, TunPlan.of(tunnel.core()))

        let config = try launch.config(tunFd: 7)
        XCTAssertTrue(config.contains(#""xray.tun.fd":"7""#), config)
        XCTAssertTrue(config.contains(TunnelSettings.quad9Doh))
    }

    func testNoSelectedServer() async throws {
        try await servers.add([Self.fixture("vless/reality-vision")])
        let error = await failure { _ = try await TunnelLaunch.prepare(settings: self.settings, servers: self.servers) }
        XCTAssertEqual(error, TunnelFailure(TunnelErrors.noServer))
    }

    func testSelectedServerWasDeleted() async throws {
        let id = try await select(Self.fixture("vless/reality-vision"))
        try await servers.delete(id)
        let error = await failure { _ = try await TunnelLaunch.prepare(settings: self.settings, servers: self.servers) }
        XCTAssertEqual(error?.code, TunnelErrors.noServer)
    }

    func testSelectedServerWithoutItsKeychainRecord() async throws {
        // Восстановление на другом iPhone: подпись в списке есть, ссылки нет — подключаться не к чему.
        let id = try await select(Self.fixture("vless/reality-vision"))
        try vault.remove(ServerStore.serverKey(id))
        let error = await failure { _ = try await TunnelLaunch.prepare(settings: self.settings, servers: self.servers) }
        XCTAssertEqual(error?.code, TunnelErrors.noServer)
    }

    func testServerTheCoreDoesNotTakeIsRejectedBeforeRoutes() async throws {
        _ = try await select(Self.fixture("hysteria2/hy2-short-scheme"))
        let error = await failure { _ = try await TunnelLaunch.prepare(settings: self.settings, servers: self.servers) }
        XCTAssertEqual(error?.code, TunnelErrors.unsupported)
    }

    func testUnreadableFilesAreACoreFailure() async throws {
        struct Locked: Error {}
        struct LockedFile: StateFile {
            func read() throws -> Data? { throw Locked() }
            func write(_ data: Data) throws { throw Locked() }
        }

        let locked = SettingsStore(file: LockedFile())
        let error = await failure { _ = try await TunnelLaunch.prepare(settings: locked, servers: self.servers) }
        XCTAssertEqual(error?.code, TunnelErrors.coreFailed)

        try await settings.select("server-1")
        let lockedServers = ServerStore(file: LockedFile(), vault: vault)
        let second = await failure { _ = try await TunnelLaunch.prepare(settings: self.settings, servers: lockedServers) }
        XCTAssertEqual(second?.code, TunnelErrors.coreFailed)
    }

    func testCoreResponse() throws {
        XCTAssertNoThrow(try TunnelLaunch.started(#"{"success": true, "data": null}"#))
        for response in [#"{"success": false, "error": "port in use"}"#, "не JSON", nil] {
            XCTAssertThrowsError(try TunnelLaunch.started(response)) { error in
                XCTAssertEqual((error as? TunnelFailure)?.code, TunnelErrors.coreFailed)
            }
        }
    }

    func testCoreErrorIsMaskedForTheLog() {
        let uuid = "11111111-2222-3333-4444-555555555555"
        let response = #"{"success": false, "error": "failed: vless://\#(uuid)@203.0.113.24:443"}"#
        XCTAssertThrowsError(try TunnelLaunch.started(response)) { error in
            let detail = (error as? TunnelFailure)?.detail ?? ""
            XCTAssertFalse(detail.contains(uuid), detail)
            XCTAssertFalse(detail.contains("203.0.113.24"), detail)
            XCTAssertTrue(detail.contains(SecretMasker.hidden), detail)
        }
    }

    func testFailureDescriptionIsTheCode() {
        XCTAssertEqual(TunnelFailure(TunnelErrors.otherVpn, detail: "x").errorDescription, TunnelErrors.otherVpn)
    }
}

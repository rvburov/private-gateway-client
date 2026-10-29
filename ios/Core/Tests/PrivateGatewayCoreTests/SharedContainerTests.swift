import Foundation
import XCTest
@testable import PrivateGatewayCore

/// Общая папка приложения и расширения: что записало приложение, расширение (второй экземпляр на той же папке) видит и
/// поднимает; адрес и ключи сервера в файлы папки не попадают.
final class SharedContainerTests: XCTestCase {
    private var directory: URL!

    override func setUpWithError() throws {
        directory = FileManager.default.temporaryDirectory.appendingPathComponent("pg-shared-\(UUID().uuidString)", isDirectory: true)
    }

    override func tearDownWithError() throws {
        if FileManager.default.fileExists(atPath: directory.path) {
            try FileManager.default.removeItem(at: directory)
        }
    }

    #if canImport(Security)
    func testCurrentSharesRepositoriesBetweenTheWindowAndBackgroundTask() {
        let app = SharedContainer.current()
        let background = SharedContainer.current()
        XCTAssertTrue(app.servers === background.servers)
        XCTAssertTrue(app.settings === background.settings)
    }
    #endif

    func testExtensionSeesWhatTheAppSaved() async throws {
        let vault = MemoryVault()
        let app = SharedContainer(directory: directory, vault: vault)
        let server = try TunnelLaunchTests.fixture("vless/reality-vision")
        try await app.servers.add([server])
        let items = try await app.servers.servers()
        let id = try XCTUnwrap(items.first?.id)
        try await app.settings.select(id)

        // Расширение — свой процесс и свой экземпляр, та же папка и та же Связка ключей.
        let tunnel = SharedContainer(directory: directory, vault: vault)
        let launch = try await TunnelLaunch.prepare(settings: tunnel.settings, servers: tunnel.servers)
        XCTAssertEqual(launch.server, server)

        try tunnel.status.write(TunnelStatus(state: .connected, serverId: id, since: Date(timeIntervalSince1970: 1_793_029_320)))
        XCTAssertEqual(app.status.read()?.serverId, id)

        XCTAssertNil(app.coreVersion.read(), "ядро еще не запускалось")
        try tunnel.coreVersion.write("26.10.21\n")
        XCTAssertEqual(app.coreVersion.read(), "26.10.21")

        try app.language.write("ru")
        XCTAssertEqual(tunnel.language.read(), "ru")

        let files = try FileManager.default.contentsOfDirectory(atPath: directory.path).sorted()
        XCTAssertEqual(
            files,
            [SharedContainer.serversFile, SharedContainer.settingsFile, SharedContainer.statusFile, SharedContainer.coreVersionFile, SharedContainer.languageFile]
                .sorted()
        )
        let uuid = try XCTUnwrap(server.credentials.uuid)
        for file in files {
            let text = try String(contentsOf: directory.appendingPathComponent(file), encoding: .utf8)
            XCTAssertFalse(text.contains(uuid), file)
            XCTAssertFalse(text.contains(server.host), file)
        }
    }
}

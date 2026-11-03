import Foundation
import PrivateGatewayCore
import PrivateGatewayTheme
import XCTest
@testable import PrivateGateway

/// Файл .pgbackup на iPhone — тот же, что у десктопа и Android (решение 559): общий эталон, пароль, ключ, подмена обертки.
final class BackupTests: XCTestCase {
    private static let fixtures: URL = {
        var url = URL(fileURLWithPath: #filePath)
        url.deleteLastPathComponent()
        url.deleteLastPathComponent()
        url.deleteLastPathComponent()
        return url.appendingPathComponent("shared/testdata/backup")
    }()

    private let password = "correct horse battery staple"

    func testSharedFixtureMadeLikeTheDesktopOpensWithItsPassword() throws {
        let text = try String(contentsOf: Self.fixtures.appendingPathComponent("envelope-v1.pgbackup"), encoding: .utf8)
        let plain = try Data(contentsOf: Self.fixtures.appendingPathComponent("envelope-v1.plain.json"))
        XCTAssertEqual(try BackupEnvelope.open(text, password: password), plain)
        XCTAssertEqual(BackupEnvelope.created(text), Date(timeIntervalSince1970: 1_793_620_800))
        XCTAssertThrowsError(try BackupEnvelope.open(text, password: "wrong horse battery staple")) { XCTAssertEqual($0 as? BackupError, .wrongPassword) }
    }

    func testPhoneCopyOpensWithThePasswordAndTheStoredKeyAndKeepsItsEnvelope() throws {
        let created = Date(timeIntervalSince1970: 1_793_634_120)
        let content = MobileBackup(createdAt: created, subscriptions: [BackupSubscription(url: "https://example.com/s", name: "VPN")]).write()
        let sealed = try BackupEnvelope.seal(content, password: password, created: created)
        XCTAssertEqual(try BackupEnvelope.open(sealed, password: password), content)
        XCTAssertEqual(BackupEnvelope.created(sealed), created)

        let key = Data((0..<32).map { UInt8($0) })
        let auto = try BackupEnvelope.seal(content, key: key, created: created)
        XCTAssertEqual(try BackupEnvelope.open(auto, key: key), content)
        XCTAssertThrowsError(try BackupEnvelope.open(auto, password: password)) { XCTAssertEqual($0 as? BackupError, .notBackup) }

        let moved = sealed.replacingOccurrences(of: "2026-11-02T15:42:00Z", with: "2026-11-03T15:42:00Z")
        XCTAssertThrowsError(try BackupEnvelope.open(moved, password: password)) { XCTAssertEqual($0 as? BackupError, .wrongPassword) }
    }

    /// Подписи экрана копии: имя файла, итог восстановления формами чисел, ошибки словами.
    func testBackupTextsOnTheScreen() {
        let texts = PgTexts(.russian)
        XCTAssertEqual(BackupLook.fileName(Date(timeIntervalSince1970: 1_793_634_120)), "private-gateway-2026-11-02.pgbackup")
        XCTAssertEqual(BackupLook.restored(BackupRestored(servers: 3, subscriptions: 1, settings: true), texts), "Восстановлено: 3 сервера, 1 список серверов, настройки")
        XCTAssertEqual(BackupLook.error(.fromComputer, texts), texts.backupErrorFromComputer)
        let summary = BackupLook.summary(MobileBackup(createdAt: Date(), servers: [BackupServer(link: "vless://example")], settings: [:]), texts)
        XCTAssertTrue(summary.contains(texts.backupPartServersKeys(1)), summary)
    }
}

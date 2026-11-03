import Foundation
import XCTest
@testable import PrivateGatewayCore

/// Содержимое копии телефона (решение 559) — те же случаи, что `MobileBackupTest` Android.
final class MobileBackupTests: XCTestCase {
    private static let fixtures: URL = {
        var url = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 {
            url.deleteLastPathComponent()
        }
        return url.appendingPathComponent("shared/testdata/backup")
    }()

    private let created = Date(timeIntervalSince1970: 1_793_634_120)

    func testCopyWithKeysGoesThereAndBack() throws {
        let copy = MobileBackup(
            createdAt: created,
            servers: [
                BackupServer(link: "vless://11111111-2222-3333-4444-555555555555@198.51.100.20:443?security=tls#NL", name: "Нидерланды «01»", favorite: true),
                BackupServer(link: "trojan://password@203.0.113.7:443#DE", group: "Работа"),
            ],
            subscriptions: [BackupSubscription(url: "https://example.com/sub/token", name: "Мой VPN")],
            settings: ["tunnel_dns": "Cloudflare", "connect_on_launch": "true"]
        )
        let back = try MobileBackup.read(copy.write())
        XCTAssertEqual(back, copy)
        XCTAssertTrue(back.keys)
    }

    func testAndroidTimeAndDesktopTimeAreRead() {
        XCTAssertEqual(MobileBackup.parseTime("2026-11-02T12:00:00Z"), Date(timeIntervalSince1970: 1_793_620_800))
        XCTAssertEqual(MobileBackup.parseTime("2026-11-02T12:00:00.0000000+00:00"), Date(timeIntervalSince1970: 1_793_620_800))
    }

    func testDesktopCopyAndOtherFilesAreNamedHonestly() throws {
        let desktop = try Data(contentsOf: Self.fixtures.appendingPathComponent("envelope-v1.plain.json"))
        XCTAssertThrowsError(try MobileBackup.read(desktop)) { XCTAssertEqual($0 as? BackupError, .fromComputer) }
        XCTAssertThrowsError(try MobileBackup.read(Data("[1,2]".utf8))) { XCTAssertEqual($0 as? BackupError, .notBackup) }
        let newer = String(decoding: MobileBackup(createdAt: created).write(), as: UTF8.self).replacingOccurrences(of: "\"version\":1", with: "\"version\":2")
        XCTAssertThrowsError(try MobileBackup.read(Data(newer.utf8))) { XCTAssertEqual($0 as? BackupError, .newerVersion) }
    }

    /// Копия Android на iPhone (DECISIONS.md, п. 3): общий эталон, значения списков — без учета регистра.
    func testAndroidCopyRestoresEveryChoiceOnTheIphone() throws {
        let android = try MobileBackup.read(Data(contentsOf: Self.fixtures.appendingPathComponent("mobile-android.plain.json")))
        let restored = AppSettings().restoring(try XCTUnwrap(android.settings))
        XCTAssertEqual(restored.tunnel.dns, .custom)
        XCTAssertEqual(restored.subscriptions.refresh, .every6Hours)
        XCTAssertEqual(restored.notifications, .all)
        XCTAssertEqual(restored.reconnect, .upTo5Minutes)
        XCTAssertEqual(restored.routing.mode, .split)

        let iphone = try MobileBackup.read(Data(contentsOf: Self.fixtures.appendingPathComponent("mobile-ios.plain.json")))
        XCTAssertEqual(AppSettings().restoring(try XCTUnwrap(iphone.settings)).tunnel.dns, .custom, "своя копия — как раньше")
        XCTAssertEqual(AppSettings().restoring(["tunnel_dns": "Другой"]).tunnel.dns, AppSettings().tunnel.dns, "неизвестное — значение по умолчанию")
    }
}

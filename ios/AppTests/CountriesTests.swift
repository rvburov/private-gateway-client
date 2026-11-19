import PrivateGatewayCore
import SwiftUI
import UIKit
import XCTest
@testable import PrivateGateway

/// Флаги стран (решение 733): пакет приложения несет геобазу и картинки флагов, база распаковывается один раз и находит
/// страну адреса, как у десктопа и Android; без кода — Земля.
final class CountriesTests: XCTestCase {
    private var directory: URL!

    override func setUpWithError() throws {
        directory = FileManager.default.temporaryDirectory.appendingPathComponent("pg-countries-\(UUID().uuidString)", isDirectory: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: directory)
    }

    func testDatabaseIsUnpackedOnceAndFindsTheCountryOfAnAddress() throws {
        let first = try XCTUnwrap(Countries.open(folder: directory), "геобаза в пакете приложения открывается")
        XCTAssertTrue(first.renewed, "первый запуск распаковывает базу")
        XCTAssertGreaterThan(first.database.count, 600_000)
        // Адреса серверов из отчета владельца 2026-11-19: десктоп показывает флаги DE и FI.
        XCTAssertEqual(first.database.lookup(host: "187.124.2.129"), "DE")
        XCTAssertEqual(first.database.lookup(host: "80.47.227.44"), "FI")
        XCTAssertNil(first.database.lookup(host: "example.com"), "адрес-имя: DNS не спрашивается")
        XCTAssertNil(first.database.lookup(host: "192.0.2.1"), "адрес документации — без страны")

        let second = try XCTUnwrap(Countries.open(folder: directory))
        XCTAssertFalse(second.renewed, "та же сборка — та же распакованная база")

        let files = try FileManager.default.contentsOfDirectory(atPath: directory.path)
        XCTAssertEqual(files.count, 1)
        let file = directory.appendingPathComponent(try XCTUnwrap(files.first))
        XCTAssertEqual(try file.resourceValues(forKeys: [.isExcludedFromBackupKey]).isExcludedFromBackup, true, "не в резервной копии")

        // Испорченный файл не мешает: флага нет, а следующий запуск распаковывает заново.
        try Data("PGCTRY1\0".utf8).write(to: file)
        XCTAssertNil(Countries.open(folder: directory))
        XCTAssertEqual(Countries.open(folder: directory)?.renewed, true)
    }

    @MainActor
    func testEveryCodeHasAFlagAndUnknownIsTheEarth() throws {
        XCTAssertEqual(CountryFlags.key("de"), "DE")
        XCTAssertEqual(CountryFlags.key(""), CountryFlags.unknown)
        XCTAssertEqual(CountryFlags.key("NLD"), CountryFlags.unknown)
        XCTAssertEqual(CountryFlags.key("Д1"), CountryFlags.unknown)
        for code in ["DE", "FI", "NL", "US", CountryFlags.unknown] {
            XCTAssertNotNil(CountryFlags.image(code), code)
        }

        let earth = try XCTUnwrap(CountryFlags.image(CountryFlags.unknown)).pngData()
        XCTAssertEqual(try XCTUnwrap(CountryFlags.image("XX")).pngData(), earth, "кода нет в наборе — Земля")

        let renderer = ImageRenderer(content: CountryGlyph(code: "DE"))
        renderer.scale = 2
        let image = try XCTUnwrap(renderer.uiImage, "флаг рисуется")
        XCTAssertEqual(image.size, CGSize(width: 28, height: 28))
    }
}

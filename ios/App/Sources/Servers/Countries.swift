import Foundation
import PrivateGatewayCore
import PrivateGatewayTheme
import SwiftUI
import UIKit

/// Геобаза приложения (решение 733) — `country.bin.gz` из пакета приложения (DB-IP Country Lite, shared/countries, общая с
/// десктопом и Android). Распаковывается один раз в Application Support под номером сборки (без резервной копии iCloud) и
/// читается отображением в память: 25 МБ записей не занимают память приложения. Нет базы или она повреждена — флаг из
/// названия ссылки, как у десктопа; DNS для адреса-имени не спрашивается.
enum Countries {
    struct Opened: Sendable {
        let database: CountryDatabase
        /// База распакована заново в этом запуске (поставили или обновили приложение): страны сохраненных серверов устарели.
        let renewed: Bool
    }

    static func open(bundle: Bundle = .main, folder: URL? = nil) -> Opened? {
        guard let source = bundle.url(forResource: "country.bin", withExtension: "gz"),
              let base = folder ?? (try? FileManager.default.url(for: .applicationSupportDirectory, in: .userDomainMask, appropriateFor: nil, create: true))?
                  .appendingPathComponent("countries", isDirectory: true)
        else {
            return nil
        }

        do {
            let build = try revision(source, bundle)
            var file = base.appendingPathComponent("country-\(build).bin")
            var renewed = false
            if !FileManager.default.fileExists(atPath: file.path) {
                try? FileManager.default.removeItem(at: base)
                try FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
                let member = try Gzip.member(Data(contentsOf: source))
                let plain = try (member.deflate as NSData).decompressed(using: .zlib) as Data
                try Gzip.check(plain, member)
                try plain.write(to: file, options: .atomic)
                var values = URLResourceValues()
                values.isExcludedFromBackup = true
                try? file.setResourceValues(values)
                renewed = true
            }

            return Opened(database: try CountryDatabase(data: Data(contentsOf: file, options: .alwaysMapped)), renewed: renewed)
        } catch {
            // Испорченный файл распакуется заново при следующем запуске.
            try? FileManager.default.removeItem(at: base)
            return nil
        }
    }

    /// Номер сборки базы: версия приложения, размер и время файла в пакете — новая сборка распаковывает заново.
    private static func revision(_ source: URL, _ bundle: Bundle) throws -> String {
        let values = try source.resourceValues(forKeys: [.fileSizeKey, .contentModificationDateKey])
        let version = (bundle.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0").filter { $0.isLetter || $0.isNumber || $0 == "." }
        return "\(version)-\(values.fileSize ?? 0)-\(Int(values.contentModificationDate?.timeIntervalSince1970 ?? 0))"
    }
}

/// Квадратный флаг страны с рамкой — как CountryFlag десктопа и .country-glyph макета: layout.countryFlag.size,
/// radius.countryFlag, рамка space.hairline цвета line.strong (видна и у белого флага). Без кода или картинки — Земля
/// (UNKNOWN.png). Картинки — папка flags пакета приложения (shared/countries/flags), без сети и эмодзи шрифта.
@MainActor
struct CountryGlyph: View {
    let code: String

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: PgRadius.countryFlag, style: .continuous)
        let key = CountryFlags.key(code)
        Group {
            if let image = CountryFlags.image(key) {
                Image(uiImage: image).resizable().scaledToFill()
            } else {
                Color(PgColor.Accent.base)
            }
        }
        .frame(width: PgLayout.CountryFlag.size, height: PgLayout.CountryFlag.size)
        .clipShape(shape)
        .overlay(shape.strokeBorder(Color(PgColor.Line.strong), lineWidth: PgSpace.hairline))
        .accessibilityHidden(true)
        .accessibilityIdentifier("flag.\(key)")
    }
}

/// Картинки флагов: код → «flags/XX.png» в пакете приложения, неизвестный — «UNKNOWN»; открытые держатся в памяти.
@MainActor
enum CountryFlags {
    nonisolated static let unknown = "UNKNOWN"
    private static var cache: [String: UIImage] = [:]

    /// Ключ картинки: две латинские буквы — код, иначе Земля.
    nonisolated static func key(_ code: String) -> String {
        code.count == 2 && code.unicodeScalars.allSatisfy({ ("A" ... "Z").contains($0) || ("a" ... "z").contains($0) }) ? code.uppercased() : unknown
    }

    static func image(_ key: String, bundle: Bundle = .main) -> UIImage? {
        if let image = cache[key] {
            return image
        }

        let image = load(key, bundle) ?? (key == unknown ? nil : load(unknown, bundle))
        if let image {
            cache[key] = image
        }

        return image
    }

    private static func load(_ key: String, _ bundle: Bundle) -> UIImage? {
        bundle.path(forResource: key, ofType: "png", inDirectory: "flags").flatMap(UIImage.init(contentsOfFile:))
    }
}

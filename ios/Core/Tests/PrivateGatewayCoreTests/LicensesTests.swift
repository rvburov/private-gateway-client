import Foundation
import XCTest
@testable import PrivateGatewayCore

/// Открытые компоненты приложения iOS (shared/licenses/ios.json) — как сверка android.json сборкой Android: у каждого есть
/// автор, ссылка и полный текст лицензии, и он действительно в сборке — пакет в Package.swift, .xcframework в проекте; пакет без
/// строки в списке тоже ошибка.
final class LicensesTests: XCTestCase {
    private static let root = LinkParserTests.testData.deletingLastPathComponent().deletingLastPathComponent()

    private struct Component {
        let fields: [String: String]

        var name: String { fields["name"] ?? "" }
    }

    private func components() throws -> [Component] {
        let text = try String(contentsOf: Self.root.appendingPathComponent("shared/licenses/ios.json"), encoding: .utf8)
        guard case let .array(items, _, _) = try JsonTree.parse(text).root else {
            XCTFail("ios.json — не список")
            return []
        }

        return items.compactMap { item in
            guard case let .object(values, _) = item else {
                XCTFail("элемент ios.json — не словарь")
                return nil
            }

            return Component(fields: values.compactMapValues { value in
                if case let .string(text) = value {
                    return text
                }

                return nil
            })
        }
    }

    func testEveryComponentHasAuthorLinkAndLicenseText() throws {
        let list = try components()
        XCTAssertFalse(list.isEmpty)
        let names = Set(list.map(\.name))
        XCTAssertEqual(names.count, list.count, "два компонента с одним именем")
        for component in list {
            for field in ["name", "license", "copyright", "url"] {
                XCTAssertFalse(component.fields[field, default: ""].isEmpty, "\(component.name): нет \(field)")
            }

            let sources = ["swiftpm", "xcframework", "inside"].filter { component.fields[$0] != nil }
            XCTAssertEqual(sources.count, 1, "\(component.name): как компонент приходит в приложение — одно из swiftpm, xcframework, inside")
            if let parent = component.fields["inside"] {
                XCTAssertTrue(names.contains(parent), "\(component.name): внутри неизвестного \(parent)")
            }

            let license = try XCTUnwrap(component.fields["license"])
            for id in license.components(separatedBy: CharacterSet(charactersIn: "() ")).filter({ !$0.isEmpty && $0 != "OR" && $0 != "AND" }) {
                let path = Self.root.appendingPathComponent("shared/licenses/texts/\(id).txt").path
                XCTAssertTrue(FileManager.default.fileExists(atPath: path), "\(component.name): нет текста лицензии \(id)")
            }
        }
    }

    func testListParsesForTheLicensesScreen() throws {
        let text = try String(contentsOf: Self.root.appendingPathComponent("shared/licenses/ios.json"), encoding: .utf8)
        let list = try OpenComponents.parse(text)
        XCTAssertEqual(list.map(\.name), try components().map(\.name))
        let libXray = try XCTUnwrap(list.first { $0.name == "libXray" })
        XCTAssertEqual(libXray.sourceUrl(version: "abc123"), "https://github.com/XTLS/libXray/tree/abc123")
        XCTAssertNil(libXray.sourceUrl(version: nil), "без версии — без ссылки на исходники")
        XCTAssertEqual(libXray.xcframework, "LibXray")
        XCTAssertEqual(list.first { $0.name == "Xray-core" }?.inside, "libXray")
        XCTAssertEqual(OpenComponent(name: "x", license: "MIT OR (Apache-2.0 AND BSD-3-Clause)", shown: nil, copyright: "", url: "", source: nil, notice: nil, inside: nil,
                                     xcframework: nil).licenseIds,
                       ["MIT", "Apache-2.0", "BSD-3-Clause"])
        XCTAssertThrowsError(try OpenComponents.parse(#"[{"name": "x"}]"#), "без лицензии — ошибка, а не пропуск")
    }

    func testListMatchesTheBuild() throws {
        let list = try components()
        let package = try String(contentsOf: Self.root.appendingPathComponent("ios/Core/Package.swift"), encoding: .utf8)
        let project = try String(contentsOf: Self.root.appendingPathComponent("ios/project.yml"), encoding: .utf8)

        // Пакеты Package.swift — по имени репозитория в адресе.
        let regex = try NSRegularExpression(pattern: #"\.package\(url: "[^"]*/([^"/]+?)(?:\.git)?""#)
        let range = NSRange(location: 0, length: (package as NSString).length)
        let packages = Set(regex.matches(in: package, range: range).map { (package as NSString).substring(with: $0.range(at: 1)) })
        XCTAssertFalse(packages.isEmpty)
        XCTAssertEqual(Set(list.compactMap { $0.fields["swiftpm"] }), packages, "пакеты Package.swift и строки swiftpm в ios.json")

        for framework in list.compactMap({ $0.fields["xcframework"] }) {
            XCTAssertTrue(project.contains("Frameworks/\(framework).xcframework"), "\(framework) нет в ios/project.yml")
        }

        let frameworks = project.components(separatedBy: .newlines).filter { $0.contains(".xcframework") && $0.contains("framework:") }
        XCTAssertEqual(frameworks.count, list.compactMap { $0.fields["xcframework"] }.count, "каждый .xcframework проекта — строкой в ios.json")
    }
}

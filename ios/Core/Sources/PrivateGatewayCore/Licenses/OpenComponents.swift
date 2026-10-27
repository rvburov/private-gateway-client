import Foundation

/// Открытый компонент приложения — строка shared/licenses/ios.json, как `OpenComponents` Android: имя, лицензия (выражение
/// SPDX) и ее подпись для людей, авторство, ссылка, исходники этой версии, обязательная строка авторства и чем компонент
/// приходит в приложение.
public struct OpenComponent: Equatable, Sendable {
    public let name: String
    public let license: String
    /// Подпись лицензии на экране, если SPDX людям непонятен; nil — сам SPDX.
    public let shown: String?
    public let copyright: String
    public let url: String
    /// Исходники этой версии; «{version}» — версия компонента.
    public let source: String?
    public let notice: String?
    /// Входит в другой компонент списка (Xray-core — внутри libXray).
    public let inside: String?
    /// Свой .xcframework из ios/Frameworks: его версия — коммит libXray (android/libxray.version).
    public let xcframework: String?

    /// Подпись лицензии.
    public var label: String { shown ?? license }

    /// Тексты лицензий для выражения SPDX: «MIT», «MIT OR Apache-2.0» — по файлу на каждый id.
    public var licenseIds: [String] {
        license.components(separatedBy: CharacterSet(charactersIn: "() ")).filter { !$0.isEmpty && $0 != "OR" && $0 != "AND" && $0 != "WITH" }
    }

    /// Исходники с подставленной версией; версии нет — без ссылки на исходники.
    public func sourceUrl(version: String?) -> String? {
        guard let source else {
            return nil
        }

        guard source.contains("{version}") else {
            return source
        }

        return version.map { source.replacingOccurrences(of: "{version}", with: $0) }
    }
}

/// Список ios.json: JSON с комментариями «//», как у Android и десктопа; строка без имени или лицензии — ошибка разбора, а не
/// пропуск: экран «Лицензии» не должен молча терять компонент.
public enum OpenComponents {
    public struct ParseError: Error, Equatable {
        public let message: String
    }

    public static func parse(_ text: String) throws -> [OpenComponent] {
        guard case let .array(items, _, _) = try JsonTree.parse(text).root else {
            throw ParseError(message: "ios.json — не список")
        }

        return try items.map { item in
            guard case let .object(values, _) = item else {
                throw ParseError(message: "элемент ios.json — не словарь")
            }

            func field(_ key: String) -> String? {
                if case let .string(text)? = values[key], !text.isEmpty {
                    return text
                }

                return nil
            }

            guard let name = field("name"), let license = field("license") else {
                throw ParseError(message: "у компонента нет name или license")
            }

            return OpenComponent(
                name: name, license: license, shown: field("shown"), copyright: field("copyright") ?? "", url: field("url") ?? "",
                source: field("source"), notice: field("notice"), inside: field("inside"), xcframework: field("xcframework")
            )
        }
    }
}

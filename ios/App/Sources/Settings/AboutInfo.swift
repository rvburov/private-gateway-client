import Foundation
import PrivateGatewayCore
import UIKit

/// «О программе» и «Лицензии»: версия приложения, iOS и архитектура, версии ядра и libXray, открытые компоненты и тексты их
/// лицензий — из пакета приложения (shared/licenses и android/libxray.version кладет туда проект, `ios/project.yml`).
struct AboutInfo {
    var app = ""
    var system = ""
    var arch = ""
    /// Версия Xray-core — ее пишет расширение туннеля после запуска ядра; до первого подключения — nil.
    var core: String?
    /// Коммит libXray, с которым собран .xcframework.
    var libXray: String?
    var components: [OpenComponent] = []
    var licenseText: (String) -> String = { _ in "" }

    /// Версия компонента: свой .xcframework — коммит libXray, ядро внутри него — версия, которую сообщило ядро; пакеты Swift —
    /// без версии на экране, она в ссылке на исходники.
    func version(_ component: OpenComponent) -> String? {
        if component.xcframework != nil {
            return libXray
        }

        return component.name == "Xray-core" ? core : nil
    }

    @MainActor
    static func of(core: String?, bundle: Bundle = .main) -> AboutInfo {
        AboutInfo(
            app: bundle.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "",
            system: "iOS " + UIDevice.current.systemVersion,
            arch: arch,
            core: core,
            libXray: read(bundle.url(forResource: "libxray", withExtension: "version")),
            components: (read(bundle.url(forResource: "ios", withExtension: "json")).flatMap { try? OpenComponents.parse($0) }) ?? [],
            licenseText: { id in read(bundle.url(forResource: id, withExtension: "txt", subdirectory: "texts")) ?? "" }
        )
    }

    private static var arch: String {
        #if arch(arm64)
        return "arm64"
        #elseif arch(x86_64)
        return "x86_64"
        #else
        return ""
        #endif
    }

    private static func read(_ url: URL?) -> String? {
        guard let url, let text = try? String(contentsOf: url, encoding: .utf8) else {
            return nil
        }

        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty ? nil : trimmed
    }
}

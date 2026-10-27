import Foundation
import PackagePlugin

/// Тема, тексты и иконки iOS из общих файлов при каждой сборке PrivateGatewayTheme — как плагин
/// privategateway.shared-resources Android: правка токенов или текстов попадает в приложение без ручного шага, а
/// сгенерированное лежит в папке сборки, не в git.
@main
struct SharedResources: BuildToolPlugin {
    func createBuildCommands(context: PluginContext, target: Target) throws -> [Command] {
        // ios/Core → корень репозитория.
        let root = context.package.directoryURL.deletingLastPathComponent().deletingLastPathComponent()
        let output = context.pluginWorkDirectoryURL.appendingPathComponent("Generated", isDirectory: true)
        // Те же файлы, что читает SharedWriter: правка любого запускает генератор снова.
        let inputs = [
            "shared/specifications/design-tokens.json",
            "shared/specifications/localization/ru.json",
            "shared/specifications/localization/en.json",
            "desktop/src/Client.UI/Themes/Icons.axaml",
        ]
        return [
            .buildCommand(
                displayName: "Тема, тексты и иконки из shared/specifications",
                executable: try context.tool(named: "SharedGen").url,
                arguments: [root.path, output.path],
                inputFiles: inputs.map { root.appendingPathComponent($0) },
                outputFiles: ["Tokens.swift", "Texts.swift", "Icons.swift"].map { output.appendingPathComponent($0) }
            ),
        ]
    }
}

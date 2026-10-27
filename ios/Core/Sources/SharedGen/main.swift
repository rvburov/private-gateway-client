// Генератор темы, текстов и иконок iOS: SharedGen <корень репозитория> <папка вывода>. Запускает его плагин SharedResources
// при сборке PrivateGatewayTheme; ошибка в общих файлах — текст в stderr и код 1, сборка падает.
import Foundation
import SharedGenKit

let arguments = ProcessInfo.processInfo.arguments
guard arguments.count == 3 else {
    FileHandle.standardError.write(Data("Использование: SharedGen <корень репозитория> <папка вывода>\n".utf8))
    exit(2)
}

do {
    let output = URL(fileURLWithPath: arguments[2], isDirectory: true)
    try FileManager.default.createDirectory(at: output, withIntermediateDirectories: true)
    let files = try SharedWriter.write(root: URL(fileURLWithPath: arguments[1], isDirectory: true))
    for (name, text) in files {
        try Data(text.utf8).write(to: output.appendingPathComponent(name))
    }
} catch {
    FileHandle.standardError.write(Data("error: SharedGen: \(error)\n".utf8))
    exit(1)
}

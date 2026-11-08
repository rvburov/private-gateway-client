// Проверка оформления фоновой записи: штатная регистрация пакета и получение иконки через AppKit.
// --register применяется только на одноразовой машине CI до установки тестовой службы.
import AppKit
import CoreServices
import Foundation
import UniformTypeIdentifiers

func fail(_ message: String) -> Never {
    fputs("Иконка фоновой записи: \(message)\n", stderr)
    exit(1)
}

func plist(_ url: URL) throws -> [String: Any] {
    let data = try Data(contentsOf: url)
    guard let value = try PropertyListSerialization.propertyList(from: data, format: nil) as? [String: Any] else {
        fail("не словарь plist")
    }
    return value
}

func pixels(_ image: NSImage) -> [UInt8] {
    let size = 32
    guard let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: size, pixelsHigh: size,
        bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
        colorSpaceName: .deviceRGB, bytesPerRow: size * 4, bitsPerPixel: 32),
        let context = NSGraphicsContext(bitmapImageRep: bitmap), let data = bitmap.bitmapData else {
        fail("не удалось создать изображение")
    }
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = context
    context.imageInterpolation = .high
    image.draw(in: NSRect(x: 0, y: 0, width: size, height: size))
    NSGraphicsContext.restoreGraphicsState()
    return Array(UnsafeBufferPointer(start: data, count: size * size * 4))
}

let args = Array(CommandLine.arguments.dropFirst())
guard args.count == 2 else { fail("ожидаются путь .app и --register или путь launchd plist") }
let app = URL(fileURLWithPath: args[0]).resolvingSymlinksInPath().standardizedFileURL
let info = try plist(app.appendingPathComponent("Contents/Info.plist"))
guard let identifier = info["CFBundleIdentifier"] as? String,
      let resource = info["CFBundleIconFile"] as? String else { fail("нет идентификатора или иконки пакета") }
if args[1] == "--register" {
    guard LSRegisterURL(app as CFURL, true) == noErr else { fail("Launch Services не зарегистрировала пакет") }
} else {
    let job = try plist(URL(fileURLWithPath: args[1]))
    let associations = (job["AssociatedBundleIdentifiers"] as? [String])
        ?? (job["AssociatedBundleIdentifiers"] as? String).map { [$0] } ?? []
    guard associations == [identifier] else { fail("запись не связана с этим пакетом") }
}
// На сборочной машине остаются staging и копия из DMG с одним ID. Первый результат — эвристика ОС,
// а не обещание вернуть последнюю регистрацию. Ищем именно установленную копию среди всех результатов.
func registeredApps() -> [URL] {
    NSWorkspace.shared.urlsForApplications(withBundleIdentifier: identifier)
        .map { $0.resolvingSymlinksInPath().standardizedFileURL }
}
var registered = registeredApps()
// Регистрация возвращается раньше, чем отдельный процесс Launch Services обновит индекс.
if args[1] == "--register" {
    for _ in 0..<50 where !registered.contains(where: { $0.path == app.path }) {
        Thread.sleep(forTimeInterval: 0.1)
        registered = registeredApps()
    }
}
guard let resolved = registered.first(where: { $0.path == app.path }) else {
    fail("Launch Services не нашла \(app.path); зарегистрированы: \(registered.map(\.path).joined(separator: "; "))")
}
let iconName = resource.hasSuffix(".icns") ? resource : resource + ".icns"
guard NSImage(contentsOf: app.appendingPathComponent("Contents/Resources/" + iconName)) != nil else {
    fail("ресурс иконки не читается")
}
let actualPixels = pixels(NSWorkspace.shared.icon(forFile: resolved.path))
// Системная подложка и отступы зависят от macOS. Требование здесь — получить собственную иконку,
// а не стандартный exec или пустое приложение. Пиксели самого ICNS отдельно проверяет macos-icon-smoke.sh.
for kind in [UTType.unixExecutable, UTType.applicationBundle] {
    let genericPixels = pixels(NSWorkspace.shared.icon(for: kind))
    let error = zip(actualPixels, genericPixels).reduce(0) { $0 + abs(Int($1.0) - Int($1.1)) }
    guard Double(error) / Double(actualPixels.count) > 8 else { fail("получена стандартная системная иконка") }
}
print("Фоновая запись → пакет \(identifier) → иконка щита: OK")

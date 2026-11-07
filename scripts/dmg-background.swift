// Фон окна образа .dmg macOS — инструкция, которую человек видит раньше предупреждения Gatekeeper: программа без подписи
// Apple сама ее показать не может, macOS не дает ей запуститься до «Все равно открыть». Подход перенесен из SmartBase
// (Scripts/github-release/dmg-background.swift, docs/sources/REUSE_REGISTER.md): картинка рисуется кодом, текст правится как
// исходник, бинарных картинок в репозитории нет. Тексты — по-русски и строкой по-английски (решение пользователя
// 2026-11-05): Finder не показывает фон на языке системы. Цвета — из shared/specifications/design-tokens.json.
//
//   swift scripts/dmg-background.swift <папка вывода> <design-tokens.json>
//
// Пишет фон 4096×4096 точек в плотностях 1x и 2x; scripts/package-macos.sh склеивает их в один TIFF, иначе
// на Retina текст выходит мылом. Значки ставит Finder (там же): программа {200, 120}, «Программы» {520, 120}, размер 112.
import AppKit
import Foundation

// Finder не растягивает фон. Запас покрывает окно до 4096 точек по каждой стороне (8K Retina).
// Инструкция остается в левом верхнем углу, остальное поле — тот же цвет.
let W: CGFloat = 4096, H: CGFloat = 4096
let layoutWidth: CGFloat = 720, layoutHeight: CGFloat = 600

let args = CommandLine.arguments
guard args.count == 3 else {
    FileHandle.standardError.write("swift scripts/dmg-background.swift <папка вывода> <design-tokens.json>\n".data(using: .utf8)!)
    exit(2)
}

// Токены: только нужные цвета; формат — "#RRGGBB" или "rgba(r, g, b, a)".
let tokens = try! JSONSerialization.jsonObject(with: Data(contentsOf: URL(fileURLWithPath: args[2]))) as! [String: Any]
func token(_ path: String) -> NSColor {
    var node: Any? = tokens["color"]
    for key in path.split(separator: ".") { node = (node as? [String: Any])?[String(key)] }
    guard let value = node as? String else { fatalError("нет токена color.\(path)") }
    if value.hasPrefix("#"), value.count == 7, let rgb = Int(value.dropFirst(), radix: 16) {
        return NSColor(srgbRed: CGFloat((rgb >> 16) & 0xFF) / 255, green: CGFloat((rgb >> 8) & 0xFF) / 255,
                       blue: CGFloat(rgb & 0xFF) / 255, alpha: 1)
    }
    let parts = value.replacingOccurrences(of: "rgba(", with: "").replacingOccurrences(of: ")", with: "")
        .split(separator: ",").compactMap { Double($0.trimmingCharacters(in: .whitespaces)) }
    guard parts.count == 4 else { fatalError("цвет color.\(path) не разобран: \(value)") }
    return NSColor(srgbRed: parts[0] / 255, green: parts[1] / 255, blue: parts[2] / 255, alpha: parts[3])
}

let backdrop = token("bg.window")
let ink = token("text.primary")
let secondary = token("text.secondary")
let muted = token("text.muted")
let divider = token("line.strong")
let accent = token("accent.base")

// Координаты — от верха окна, как у значков в Finder; NSGraphicsContext считает от низа.
func fromTop(_ y: CGFloat) -> CGFloat { H - y }

@discardableResult
func text(_ s: String, _ font: NSFont, _ color: NSColor, x: CGFloat, top: CGFloat, width: CGFloat) -> CGFloat {
    let p = NSMutableParagraphStyle()
    p.lineSpacing = 2
    let a = NSAttributedString(string: s, attributes: [.font: font, .foregroundColor: color, .paragraphStyle: p])
    let h = a.boundingRect(with: NSSize(width: width, height: 400), options: [.usesLineFragmentOrigin]).height
    a.draw(with: NSRect(x: x, y: fromTop(top) - h, width: width, height: h), options: [.usesLineFragmentOrigin])
    return h
}

let side: CGFloat = 56
let column = layoutWidth - 2 * side

// Шаги — дословно названия кнопок macOS 15–26 (без буквы «е с точками», как во всех текстах проекта).
let steps = [
    "Перетащите Private Gateway в «Программы».",
    "Откройте ее из «Программ». Если появится «Файл не был открыт», нажмите «Готово».",
    "Системные настройки → Конфиденциальность и безопасность.",
    "В разделе «Безопасность» нажмите «Все равно открыть».",
    "В новом окне снова нажмите «Все равно открыть» и подтвердите запуск.",
]
let english = "Drag Private Gateway to Applications and open it. In the warning click Done, then System Settings → "
    + "Privacy & Security → Open Anyway. In the next dialog click Open Anyway again and confirm."

func render(scale: CGFloat) -> Data {
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(W * scale), pixelsHigh: Int(H * scale),
                               bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                               colorSpaceName: .calibratedRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    rep.size = NSSize(width: W, height: H)
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)

    backdrop.setFill()
    NSRect(x: 0, y: 0, width: W, height: H).fill()

    // Стрелка между значками программы и «Программ»: куда тянуть.
    let arrowY = fromTop(120)
    let arrow = NSBezierPath()
    arrow.lineWidth = 3
    arrow.lineCapStyle = .round
    arrow.lineJoinStyle = .round
    arrow.move(to: NSPoint(x: 292, y: arrowY))
    arrow.line(to: NSPoint(x: 428, y: arrowY))
    arrow.move(to: NSPoint(x: 414, y: arrowY + 12))
    arrow.line(to: NSPoint(x: 428, y: arrowY))
    arrow.line(to: NSPoint(x: 414, y: arrowY - 12))
    accent.setStroke()
    arrow.stroke()

    // Подписи значков Finder заканчиваются около 200 от верха; ниже — полоса и инструкция.
    divider.setFill()
    NSRect(x: side, y: fromTop(222), width: column, height: 1).fill()

    var top: CGFloat = 240
    top += text("Как установить и запустить", .systemFont(ofSize: 17, weight: .semibold), ink, x: side, top: top, width: column) + 10
    let font = NSFont.systemFont(ofSize: 13.5, weight: .regular)
    for (index, step) in steps.enumerated() {
        text("\(index + 1).", font, ink, x: side, top: top, width: 22)
        top += text(step, font, ink, x: side + 22, top: top, width: column - 22) + 7
    }

    top += 6
    text(english, .systemFont(ofSize: 11.5, weight: .regular), secondary, x: side, top: top, width: column)
    text("Программа пока без подписи Apple · Not yet signed by Apple", .systemFont(ofSize: 11, weight: .regular), muted,
         x: side, top: layoutHeight - 30, width: column)

    NSGraphicsContext.restoreGraphicsState()
    return rep.representation(using: .png, properties: [:])!
}

for (scale, name) in [(CGFloat(1), "background.png"), (CGFloat(2), "background@2x.png")] {
    let url = URL(fileURLWithPath: args[1]).appendingPathComponent(name)
    try! render(scale: scale).write(to: url)
    print("нарисовано: \(url.path)")
}

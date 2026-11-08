// Сравнивает пиксели после декодирования ICNS с исходными PNG. Заголовки и размеры сами по себе не ловят полосы в Finder.
import Foundation
import CoreGraphics
import ImageIO

struct Raster {
    let width: Int
    let height: Int
    let pixels: [UInt8]
}

func readRaster(_ url: URL) throws -> Raster {
    guard let source = CGImageSourceCreateWithURL(url as CFURL, nil),
          let image = CGImageSourceCreateImageAtIndex(source, 0, nil),
          let space = CGColorSpace(name: CGColorSpace.sRGB) else {
        throw NSError(domain: "IconCheck", code: 1, userInfo: [NSLocalizedDescriptionKey: "Не читается \(url.path)"])
    }
    var pixels = [UInt8](repeating: 0, count: image.width * image.height * 4)
    let rendered = pixels.withUnsafeMutableBytes { bytes in
        guard let context = CGContext(data: bytes.baseAddress, width: image.width, height: image.height,
                                      bitsPerComponent: 8, bytesPerRow: image.width * 4, space: space,
                                      bitmapInfo: CGBitmapInfo.byteOrder32Big.rawValue | CGImageAlphaInfo.premultipliedLast.rawValue) else { return false }
        context.draw(image, in: CGRect(x: 0, y: 0, width: image.width, height: image.height))
        return true
    }
    guard rendered else { throw NSError(domain: "IconCheck", code: 2) }
    return Raster(width: image.width, height: image.height, pixels: pixels)
}

guard CommandLine.arguments.count == 3 else { fatalError("Нужны исходная и декодированная папки iconset") }
let expected = URL(fileURLWithPath: CommandLine.arguments[1])
let actual = URL(fileURLWithPath: CommandLine.arguments[2])
var failures = 0
for size in [16, 32, 128, 256, 512] {
    for scale in [1, 2] {
        let suffix = scale == 2 ? "@2x" : ""
        let name = "icon_\(size)x\(size)\(suffix).png"
        let reference = try readRaster(expected.appendingPathComponent(name))
        let decoded = try readRaster(actual.appendingPathComponent(name))
        let dimensionsMatch = reference.width == size * scale && reference.height == size * scale
            && decoded.width == reference.width && decoded.height == reference.height
        // Маленькие кадры iconutil переводит через RGB + маску: RGB полупрозрачного края при обратной записи
        // меняется. Проверяем всю маску и каждый непрозрачный пиксель, включая белую галочку и зеленый щит.
        var alphaDifference = 0
        var opaqueDifference = 0
        var opaquePixels = 0
        if dimensionsMatch {
            for offset in stride(from: 0, to: reference.pixels.count, by: 4) {
                alphaDifference = max(alphaDifference, abs(Int(reference.pixels[offset + 3]) - Int(decoded.pixels[offset + 3])))
                if reference.pixels[offset + 3] == 255 {
                    opaquePixels += 1
                    for channel in 0..<3 {
                        opaqueDifference = max(opaqueDifference, abs(Int(reference.pixels[offset + channel]) - Int(decoded.pixels[offset + channel])))
                    }
                }
            }
        }
        let passed = dimensionsMatch && opaquePixels > 0 && alphaDifference <= 1 && opaqueDifference <= 1
        print("\(passed ? "OK" : "FAIL") \(name): \(decoded.width)x\(decoded.height), alpha=\(alphaDifference), opaque RGB=\(opaqueDifference)")
        if !passed { failures += 1 }
    }
}
if failures != 0 { exit(1) }
print("Иконка macOS: 10/10 кадров прошли проверку прозрачности и цвета")

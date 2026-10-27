import CoreImage
import CoreImage.CIFilterBuiltins
import UIKit
import Vision
import VisionKit
import SwiftUI

/// QR-код на устройстве, без сети: картинка из ссылки — CoreImage (как `QrCode` Android на ZXing: уровень коррекции M, поле
/// в четыре модуля), текст с картинки — Vision, а если он кода не нашел — детектор CoreImage (решение 461).
enum QrCode {
    /// Поле вокруг кода в модулях — по стандарту QR, иначе камеры и Vision находят код хуже.
    static let quietZone: CGFloat = 4

    /// Картинка кода: модуль — `scale` точек без сглаживания, белое поле вокруг; текст не влез в QR — nil.
    static func image(_ text: String, scale: CGFloat = 8) -> UIImage? {
        let filter = CIFilter.qrCodeGenerator()
        filter.message = Data(text.utf8)
        filter.correctionLevel = "M"
        guard let output = filter.outputImage, let code = CIContext().createCGImage(output, from: output.extent) else {
            return nil
        }

        let modules = CGSize(width: CGFloat(code.width) + quietZone * 2, height: CGFloat(code.height) + quietZone * 2)
        let format = UIGraphicsImageRendererFormat()
        format.scale = 1
        return UIGraphicsImageRenderer(size: CGSize(width: modules.width * scale, height: modules.height * scale), format: format).image { context in
            UIColor.white.setFill()
            context.fill(CGRect(origin: .zero, size: context.format.bounds.size))
            context.cgContext.interpolationQuality = .none
            // CoreGraphics рисует снизу вверх: код переворачивается, чтобы остаться читаемым.
            context.cgContext.translateBy(x: 0, y: context.format.bounds.height)
            context.cgContext.scaleBy(x: 1, y: -1)
            context.cgContext.draw(
                code, in: CGRect(x: quietZone * scale, y: quietZone * scale, width: CGFloat(code.width) * scale, height: CGFloat(code.height) * scale)
            )
        }
    }

    /// Текст первого QR-кода на картинке; кода нет — nil. Vision лучше находит код на фото (наклон, мелкий код), но без
    /// нейронного ускорителя (симулятор) ничего не находит — тогда детектор CoreImage, ему ускоритель не нужен.
    static func read(_ image: CGImage) -> String? {
        vision(image) ?? coreImage(image)
    }

    private static func vision(_ image: CGImage) -> String? {
        let request = VNDetectBarcodesRequest()
        request.symbologies = [.qr]
        try? VNImageRequestHandler(cgImage: image).perform([request])
        return request.results?.compactMap(\.payloadStringValue).first
    }

    private static func coreImage(_ image: CGImage) -> String? {
        let detector = CIDetector(ofType: CIDetectorTypeQRCode, context: nil, options: [CIDetectorAccuracy: CIDetectorAccuracyHigh])
        let features = detector?.features(in: CIImage(cgImage: image)) ?? []
        return features.compactMap { ($0 as? CIQRCodeFeature)?.messageString }.first
    }

    /// Текст QR-кода с картинки из Фото; картинку не открыть — ошибка, кода нет — nil.
    static func read(_ data: Data) throws -> String? {
        guard let image = UIImage(data: data)?.cgImage else {
            throw QrFailure.unreadable
        }

        return read(image)
    }

    /// Умеет ли iPhone сканировать камерой: VisionKit на этом устройстве и камера доступна (разрешение не отозвано).
    @MainActor
    static var cameraAvailable: Bool {
        DataScannerViewController.isSupported && DataScannerViewController.isAvailable
    }
}

enum QrFailure: Error {
    case unreadable
}

/// Сканер QR камерой (VisionKit): код читается сам, как только целиком попадет в кадр; первый найденный текст — `onFound`.
struct QrScanner: UIViewControllerRepresentable {
    let onFound: (String) -> Void
    let onFailed: () -> Void

    func makeUIViewController(context: Context) -> DataScannerViewController {
        let scanner = DataScannerViewController(
            recognizedDataTypes: [.barcode(symbologies: [.qr])], qualityLevel: .balanced, recognizesMultipleItems: false, isHighFrameRateTrackingEnabled: false,
            isHighlightingEnabled: true
        )
        scanner.delegate = context.coordinator
        do {
            try scanner.startScanning()
        } catch {
            // Не посреди построения экрана: окно закроет сканер следующим шагом.
            let failed = onFailed
            Task { @MainActor in failed() }
        }

        return scanner
    }

    func updateUIViewController(_ scanner: DataScannerViewController, context: Context) {}

    static func dismantleUIViewController(_ scanner: DataScannerViewController, coordinator: Coordinator) {
        scanner.stopScanning()
    }

    func makeCoordinator() -> Coordinator {
        Coordinator(onFound: onFound, onFailed: onFailed)
    }

    @MainActor
    final class Coordinator: NSObject, DataScannerViewControllerDelegate {
        private let onFound: (String) -> Void
        private let onFailed: () -> Void
        private var done = false

        init(onFound: @escaping (String) -> Void, onFailed: @escaping () -> Void) {
            self.onFound = onFound
            self.onFailed = onFailed
        }

        func dataScanner(_ dataScanner: DataScannerViewController, didAdd addedItems: [RecognizedItem], allItems: [RecognizedItem]) {
            for item in addedItems {
                if case let .barcode(code) = item, let text = code.payloadStringValue, !done {
                    done = true
                    onFound(text)
                    return
                }
            }
        }

        func dataScanner(_ dataScanner: DataScannerViewController, becameUnavailableWithError error: DataScannerViewController.ScanningUnavailable) {
            if !done {
                done = true
                onFailed()
            }
        }
    }
}

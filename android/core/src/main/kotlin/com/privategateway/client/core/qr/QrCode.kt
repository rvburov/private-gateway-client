package com.privategateway.client.core.qr

import com.google.zxing.BarcodeFormat
import com.google.zxing.BinaryBitmap
import com.google.zxing.DecodeHintType
import com.google.zxing.EncodeHintType
import com.google.zxing.LuminanceSource
import com.google.zxing.PlanarYUVLuminanceSource
import com.google.zxing.ReaderException
import com.google.zxing.RGBLuminanceSource
import com.google.zxing.common.HybridBinarizer
import com.google.zxing.qrcode.QRCodeReader
import com.google.zxing.qrcode.QRCodeWriter
import com.google.zxing.qrcode.decoder.ErrorCorrectionLevel

/**
 * QR-код: ссылка сервера в модули для «Поделиться» и текст с картинки или кадра камеры для импорта — как у десктопа
 * (`QrCode`, ZXing.Net). ZXing (Apache-2.0), только вычисления — ни сети, ни файлов, ни Android API.
 */
object QrCode {
    /** Больше — не снимок экрана и не кадр камеры: картинку не разбираем, чтобы не занять память. */
    const val MAX_PIXELS = 40_000_000

    /** Модули QR-кода без белого поля: [строка][столбец], true — темный. */
    fun encode(text: String): Array<BooleanArray> {
        require(text.isNotEmpty()) { "Пустой текст" }
        val hints = mapOf(
            EncodeHintType.CHARACTER_SET to "UTF-8",
            EncodeHintType.ERROR_CORRECTION to ErrorCorrectionLevel.M,
            EncodeHintType.MARGIN to 0,
        )
        val matrix = QRCodeWriter().encode(text, BarcodeFormat.QR_CODE, 0, 0, hints)
        return Array(matrix.height) { y -> BooleanArray(matrix.width) { x -> matrix.get(x, y) } }
    }

    /** Текст первого QR-кода на картинке (пиксели ARGB по строкам, как Bitmap.getPixels); null — кода нет или картинка не годится. */
    fun decode(width: Int, height: Int, argb: IntArray): String? {
        if (!fits(width, height) || argb.size != width * height) {
            return null
        }

        return read(RGBLuminanceSource(width, height, argb))
    }

    /**
     * Текст QR-кода в кадре камеры: яркость (плоскость Y кадра YUV) по строкам с шагом [rowStride]; null — кода нет.
     */
    fun decodeLuminance(width: Int, height: Int, luminance: ByteArray, rowStride: Int = width): String? {
        if (!fits(width, height) || rowStride < width || luminance.size < rowStride * (height - 1) + width) {
            return null
        }

        // Шаг строки шире кадра — данные читаются как картинка шириной rowStride, из нее вырезается сам кадр.
        val rows = height
        val data = if (luminance.size >= rowStride * rows) luminance else luminance.copyOf(rowStride * rows)
        return read(PlanarYUVLuminanceSource(data, rowStride, rows, 0, 0, width, height, false))
    }

    private fun fits(width: Int, height: Int) = width > 0 && height > 0 && width.toLong() * height <= MAX_PIXELS

    private fun read(source: LuminanceSource): String? {
        val hints = mapOf(DecodeHintType.TRY_HARDER to true, DecodeHintType.CHARACTER_SET to "UTF-8")
        val reader = QRCodeReader()
        // Сначала как есть, потом инвертированно: QR в темной теме бывает светлым на темном.
        val text = attempt { reader.decode(BinaryBitmap(HybridBinarizer(source)), hints).text }
            ?: attempt { reader.decode(BinaryBitmap(HybridBinarizer(source.invert())), hints).text }
        return text?.takeIf { it.isNotEmpty() }
    }

    private inline fun attempt(block: () -> String?): String? = try {
        block()
    } catch (_: ReaderException) {
        null
    }
}

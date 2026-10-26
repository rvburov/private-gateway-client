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
import com.google.zxing.multi.qrcode.QRCodeMultiReader
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
        if (!fitsFrame(width, height, luminance.size, rowStride)) {
            return null
        }

        // Шаг строки шире кадра — данные читаются как картинка шириной rowStride, из нее вырезается сам кадр. ZXing
        // берет из буфера только строки кадра, поэтому последней строке выравнивание не нужно и копия буфера тоже.
        return read(PlanarYUVLuminanceSource(luminance, rowStride, height, 0, 0, width, height, false))
    }

    /**
     * Кадр [width]×[height] с шагом строки [rowStride] помещается в буфер длиной [size]: последняя строка — без
     * выравнивания, как камера отдает плоскости. Счет в Long: огромный шаг не переполняет проверку.
     */
    internal fun fitsFrame(width: Int, height: Int, size: Int, rowStride: Int) =
        fits(width, height) && rowStride >= width && size >= rowStride.toLong() * (height - 1) + width

    /** Один проход ZXing по яркости — общий для картинок и кадров камеры ([QrScanner]); null — кода не нашлось. */
    internal fun pass(source: LuminanceSource, way: QrPass, tryHarder: Boolean): String? {
        val bitmap = BinaryBitmap(HybridBinarizer(if (way.inverted) source.invert() else source))
        // ZXing проверяет только наличие подсказки TRY_HARDER, не ее значение: у быстрого прохода такого ключа нет вовсе.
        val hints = if (tryHarder) thoroughHints else quickHints
        return try {
            if (way.allFinders) {
                QRCodeMultiReader().decodeMultiple(bitmap, hints).firstOrNull()?.text
            } else {
                QRCodeReader().decode(bitmap, hints).text
            }
        } catch (_: ReaderException) {
            null
        }
    }

    private val quickHints: Map<DecodeHintType, Any> = mapOf(DecodeHintType.CHARACTER_SET to "UTF-8")
    private val thoroughHints = quickHints + (DecodeHintType.TRY_HARDER to true)

    private fun fits(width: Int, height: Int) = width > 0 && height > 0 && width.toLong() * height <= MAX_PIXELS

    private fun read(source: LuminanceSource): String? {
        val text = QrPass.entries.firstNotNullOfOrNull { pass(source, it, tryHarder = true) }
        return text?.takeIf { it.isNotEmpty() }
    }
}

/**
 * Проходы по картинке — в этом порядке. Сначала как есть, потом инвертированно: QR в темной теме бывает светлым на
 * темном. Потом то же с перебором всех угловых узоров ([allFinders]): обычный поиск ZXing перестает искать третий
 * узор, как только нашел два и похожий на угловой рисунок в данных, и такой код не читается почти при любом масштабе
 * (в эталонах — hy2-short-scheme и vmess/empty-alter-id). Перебор строже к перекосу кадра, поэтому идет вторым.
 */
internal enum class QrPass(val inverted: Boolean, val allFinders: Boolean) {
    PLAIN(false, false),
    INVERTED(true, false),
    PLAIN_ALL_FINDERS(false, true),
    INVERTED_ALL_FINDERS(true, true),
}

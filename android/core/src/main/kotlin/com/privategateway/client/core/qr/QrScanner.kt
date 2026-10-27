package com.privategateway.client.core.qr

import com.google.zxing.PlanarYUVLuminanceSource

/**
 * QR-код с камеры кадр за кадром. Кадры идут подряд, поэтому каждый разбирается быстро, а не тщательно, как картинка в
 * [QrCode]:
 * - только квадрат в центре кадра со стороной [cropShare] от меньшей стороны — там рамка на экране; квадрат в центре
 *   одинаков при любом повороте кадра, поэтому кадр с датчика не нужно поворачивать;
 * - без TRY_HARDER;
 * - один проход на кадр, проходы [QrPass] по кругу: как есть и инвертированно чередуются — светлый код на темном
 *   экране читается через кадр, код с узором-обманкой в данных — через два.
 *
 * Код читается один раз: после него [offer] дает null до [reset] — экран успевает закрыться, повторов нет. Кадры подаются
 * из одного потока (анализатор камеры); [reset] можно звать из любого.
 */
class QrScanner(val cropShare: Double = FRAME_SHARE) {
    init {
        require(cropShare > 0 && cropShare <= 1) { "Доля кадра — больше 0 и не больше 1" }
    }

    @Volatile
    private var read = false

    @Volatile
    private var next = 0

    /**
     * Текст кода в кадре: яркость (плоскость Y кадра YUV) по строкам с шагом [rowStride]. null — кода нет, кадр не
     * годится (шаг уже кадра, буфер короче) или код уже прочитан. Не бросает исключений.
     */
    fun offer(width: Int, height: Int, luminance: ByteArray, rowStride: Int = width): String? {
        if (read || !QrCode.fitsFrame(width, height, luminance.size, rowStride)) {
            return null
        }

        val side = (minOf(width, height) * cropShare).toInt().coerceAtLeast(1)
        // ZXing читает из буфера только строки квадрата, поэтому короткая последняя строка кадра ему не мешает.
        val crop = PlanarYUVLuminanceSource(luminance, rowStride, height, (width - side) / 2, (height - side) / 2, side, side, false)
        val way = QrPass.entries[next]
        next = (next + 1) % QrPass.entries.size
        val text = QrCode.pass(crop, way, tryHarder = false)?.takeIf { it.isNotEmpty() }
        read = text != null
        return text
    }

    /** Читать снова: например, прочитанный код не подошел, а экран камеры остался открытым. */
    fun reset() {
        next = 0
        read = false
    }

    companion object {
        /** Доля меньшей стороны кадра, где ищется код, — та же рамка на экране камеры. */
        const val FRAME_SHARE = 0.75
    }
}

package com.privategateway.client.core.qr

import kotlin.math.sqrt

/**
 * Размер, до которого уменьшать картинку из галереи перед поиском QR-кода. Снимок камеры телефона — 12–200 Мп, а
 * [QrCode.decode] берет пиксели массивом: 200 Мп — это 800 МБ, приложение упадет. Поэтому картинка декодируется сразу
 * уменьшенной, а не отвергается, как на десктопе: для кода хватает и 8 Мп.
 */
object QrImageSize {
    /** 8 Мп: около 32 МБ на Bitmap и столько же на массив пикселей. */
    const val MAX_PIXELS = 8_000_000

    /**
     * Ширина и высота не больше [maxPixels] пикселей вместе: пропорции те же (с точностью до пикселя), картинка не
     * увеличивается, сторона не меньше 1. Полоска шириной в пиксель пропорций не сохранит — тогда длинная сторона
     * просто обрезается до [maxPixels].
     */
    fun target(width: Int, height: Int, maxPixels: Int = MAX_PIXELS): Pair<Int, Int> {
        require(width > 0 && height > 0 && maxPixels > 0) { "Размеры — больше нуля" }
        val pixels = width.toLong() * height
        if (pixels <= maxPixels) {
            return width to height
        }

        val scale = sqrt(maxPixels.toDouble() / pixels)
        // Округление вниз держит произведение в пределе.
        val w = (width * scale).toInt().coerceAtLeast(1)
        val h = (height * scale).toInt().coerceAtLeast(1)
        return when {
            w.toLong() * h <= maxPixels -> w to h
            // Сторона уперлась в 1 пиксель (или подвела плавающая точка) — подрезается другая.
            w <= h -> w to maxPixels / w
            else -> maxPixels / h to h
        }
    }
}

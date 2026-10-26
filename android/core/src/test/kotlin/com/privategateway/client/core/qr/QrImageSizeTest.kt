package com.privategateway.client.core.qr

import com.privategateway.client.core.qr.QrPicture.Companion.link
import org.junit.jupiter.api.Test
import org.junit.jupiter.params.ParameterizedTest
import org.junit.jupiter.params.provider.CsvSource
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertTrue

/** Снимки из галереи уменьшаются до 8 Мп перед поиском кода: пропорции те же, маленькие не растут. */
class QrImageSizeTest {
    @Test
    fun `photo from a 200 MP camera is shrunk and its code is still read`() {
        // Снимок 16320×12240 целиком массивом — 800 МБ, и QrCode его не возьмет: больше 40 Мп.
        val (width, height) = 16_320 to 12_240
        assertTrue(width.toLong() * height > QrCode.MAX_PIXELS)
        val (w, h) = QrImageSize.target(width, height)
        assertEquals(3265 to 2449, w to h)
        assertProportional(width, height, w, h)

        // Код в углу снимка, 23 пикселя на модуль; после уменьшения в 5 раз — неровные 4–5 пикселей. Ближайший
        // пиксель, как самый грубый способ уменьшения.
        val photo = QrPicture(QrCode.encode(link), scale = 23, left = 11_000, top = 8_000)
        val argb = IntArray(w * h) {
            QrPicture.argb(photo.gray(((it % w).toLong() * width / w).toInt(), ((it / w).toLong() * height / h).toInt()))
        }
        assertEquals(link, QrCode.decode(w, h, argb))
    }

    @ParameterizedTest
    @CsvSource(
        "4000, 3000, 3265, 2449", // 12 Мп
        "3000, 4000, 2449, 3265", // тот же снимок вертикально
        "17280, 11520, 3464, 2309", // 200 Мп, 3:2
        "100000, 1000, 28284, 282", // панорама 100:1
    )
    fun `large picture keeps its proportions under the cap`(width: Int, height: Int, w: Int, h: Int) {
        val target = QrImageSize.target(width, height)
        assertEquals(w to h, target)
        assertTrue(w.toLong() * h <= QrImageSize.MAX_PIXELS)
        assertProportional(width, height, w, h)
    }

    @ParameterizedTest
    @CsvSource("640, 480", "1, 1", "4000, 2000", "8000000, 1", "1, 8000000")
    fun `picture within the cap is never enlarged`(width: Int, height: Int) {
        assertEquals(width to height, QrImageSize.target(width, height))
    }

    @Test
    fun `strip one pixel wide is cut to the cap`() {
        // Пропорции полоски не сохранить — сторона не меньше пикселя, длинная обрезается до предела.
        assertEquals(1 to 8_000_000, QrImageSize.target(1, 100_000_000))
        assertEquals(8_000_000 to 1, QrImageSize.target(100_000_000, 1))
        assertEquals(1 to 8_000_000, QrImageSize.target(3, 50_000_000))
    }

    @Test
    fun `cap can be set`() {
        assertEquals(100 to 100, QrImageSize.target(1000, 1000, maxPixels = 10_000))
        assertEquals(1000 to 10, QrImageSize.target(1000, 10, maxPixels = 10_000))
    }

    @Test
    fun `empty picture is refused`() {
        assertFailsWith<IllegalArgumentException> { QrImageSize.target(0, 100) }
        assertFailsWith<IllegalArgumentException> { QrImageSize.target(100, -1) }
        assertFailsWith<IllegalArgumentException> { QrImageSize.target(100, 100, maxPixels = 0) }
    }

    /** Один масштаб на обе стороны с округлением вниз: отрезки возможных масштабов у сторон пересекаются. */
    private fun assertProportional(width: Int, height: Int, w: Int, h: Int) {
        val low = maxOf(w.toDouble() / width, h.toDouble() / height)
        val high = minOf((w + 1.0) / width, (h + 1.0) / height)
        assertTrue(low < high, "$width×$height → $w×$h: пропорции не те")
    }
}

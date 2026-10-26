package com.privategateway.client.core.qr

import org.junit.jupiter.api.Test
import java.io.File
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

/** QR-код: ссылка сервера → модули → картинка → тот же текст, как при «Поделиться» и импорте; случаи — как у десктопа. */
class QrCodeTest {
    private val link = File(System.getProperty("testdata")).resolve("links/vless/private-gateway.txt").readText().trim()

    @Test
    fun `link survives encoding and reading back from a screenshot`() {
        val modules = QrCode.encode(link)
        assertTrue(modules.all { it.size == modules.size }, "код квадратный")
        // Как снимок экрана: код в углу большой картинки, по 4 пикселя на модуль.
        val image = render(modules, scale = 4, canvas = 900, offset = 37)
        assertEquals(link, QrCode.decode(image.size, image.size, image.argb))
    }

    @Test
    fun `modules are rows then columns not a mirror`() {
        val modules = QrCode.encode(link)
        val size = modules.size
        // ISO/IEC 18004: «темный модуль» всегда в столбце 8 над нижним левым искателем; в зеркальном коде он был бы в
        // строке 8, и часть читателей такой код не примет.
        assertTrue(modules[size - 8][8])
        assertFalse(modules[8][size - 8], "для этой ссылки бит формата здесь светлый — зеркало заметно")
    }

    @Test
    fun `light code on dark background is read too`() {
        val image = render(QrCode.encode(link), scale = 3, canvas = 400, offset = 20, inverted = true)
        assertEquals(link, QrCode.decode(image.size, image.size, image.argb))
    }

    @Test
    fun `camera frame with a wider row stride is read`() {
        // Кадр камеры: плоскость Y, строка в памяти шире кадра (выравнивание), справа — мусор.
        val image = render(QrCode.encode(link), scale = 3, canvas = 360, offset = 12)
        val stride = image.size + 64
        val luminance = ByteArray(stride * image.size) { 0x55 }
        for (y in 0 until image.size) {
            for (x in 0 until image.size) {
                luminance[y * stride + x] = (image.argb[y * image.size + x] and 0xFF).toByte()
            }
        }

        assertEquals(link, QrCode.decodeLuminance(image.size, image.size, luminance, stride))
        // Последняя строка без выравнивания (так отдает плоскости камера) — тоже кадр; хоть на байт короче — уже нет.
        val exact = stride * (image.size - 1) + image.size
        assertEquals(link, QrCode.decodeLuminance(image.size, image.size, luminance.copyOf(exact), stride))
        assertNull(QrCode.decodeLuminance(image.size, image.size, luminance.copyOf(exact - 1), stride), "буфер короче кадра")
        assertNull(QrCode.decodeLuminance(image.size, image.size, luminance, image.size - 1), "строка уже кадра")
    }

    @Test
    fun `picture without a code gives nothing`() {
        assertNull(QrCode.decode(300, 200, IntArray(300 * 200) { 0xFFEEEEEE.toInt() }))
        assertNull(QrCode.decode(10, 10, IntArray(12)), "размер буфера не сходится с картинкой")
        assertNull(QrCode.decodeLuminance(300, 200, ByteArray(300 * 200) { 0x77 }))
    }

    @Test
    fun `huge picture is not read at all`() {
        // Больше 40 млн пикселей — даже с кодом картинка не разбирается: память дороже.
        val image = render(QrCode.encode(link), scale = 4, canvas = 6400, offset = 10)
        assertNull(QrCode.decode(image.size, image.size, image.argb))
    }

    private class Image(val size: Int, val argb: IntArray)

    private fun render(modules: Array<BooleanArray>, scale: Int, canvas: Int, offset: Int, inverted: Boolean = false): Image {
        val (dark, light) = if (inverted) 0xF0 to 0x10 else 0x10 to 0xF0
        fun gray(value: Int) = (0xFF shl 24) or (value shl 16) or (value shl 8) or value
        val pixels = IntArray(canvas * canvas) { gray(light) }
        for (y in modules.indices) {
            for (x in modules[y].indices) {
                if (!modules[y][x]) {
                    continue
                }

                // Белое поле вокруг кода — 4 модуля.
                for (dy in 0 until scale) {
                    for (dx in 0 until scale) {
                        pixels[(offset + (y + 4) * scale + dy) * canvas + offset + (x + 4) * scale + dx] = gray(dark)
                    }
                }
            }
        }

        return Image(canvas, pixels)
    }
}

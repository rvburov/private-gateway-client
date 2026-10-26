package com.privategateway.client.core.qr

import com.privategateway.client.core.qr.QrPicture.Companion.link
import org.junit.jupiter.api.DynamicTest
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.TestFactory
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

/** QR-код: ссылка сервера → модули → картинка → тот же текст, как при «Поделиться» и импорте; случаи — как у десктопа. */
class QrCodeTest {
    @Test
    fun `link survives encoding and reading back from a screenshot`() {
        val modules = QrCode.encode(link)
        assertTrue(modules.all { it.size == modules.size }, "код квадратный")
        // Как снимок экрана: код в углу большой картинки, по 4 пикселя на модуль.
        val image = QrPicture(modules, scale = 4, left = 37, top = 37).argb(900, 900)
        assertEquals(link, QrCode.decode(900, 900, image))
    }

    @Test
    fun `small code in a big screenshot is found`() {
        // 2 пикселя на модуль в углу снимка 1600×1600: быстрый поиск перескакивает строки и угловые узоры не видит,
        // картинку разбирает тщательный (TRY_HARDER), как у десктопа.
        val image = QrPicture(QrCode.encode(link), scale = 2).argb(1600, 1600)
        assertEquals(link, QrCode.decode(1600, 1600, image))
    }

    @TestFactory
    fun `every shared link survives encoding and reading back`() = QrPicture.links().map { (fixture, text) ->
        DynamicTest.dynamicTest(fixture) {
            // Длина, эмодзи флага и кириллица в имени — все доходит до импорта как было.
            val picture = QrPicture(QrCode.encode(text), scale = 3, left = 11, top = 11)
            val size = picture.side + 22
            assertEquals(text, QrCode.decode(size, size, picture.argb(size, size)), fixture)
        }
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
        val image = QrPicture(QrCode.encode(link), scale = 3, left = 20, top = 20, inverted = true).argb(400, 400)
        assertEquals(link, QrCode.decode(400, 400, image))
    }

    @Test
    fun `camera frame with a wider row stride is read`() {
        // Кадр камеры: плоскость Y, строка в памяти шире кадра (выравнивание), справа — мусор.
        val stride = 360 + 64
        val luminance = QrPicture(QrCode.encode(link), scale = 3, left = 12, top = 12).luminance(360, 360, stride)

        assertEquals(link, QrCode.decodeLuminance(360, 360, luminance, stride))
        // Последняя строка без выравнивания (так отдает плоскости камера) — тоже кадр; хоть на байт короче — уже нет.
        val exact = stride * (360 - 1) + 360
        assertEquals(link, QrCode.decodeLuminance(360, 360, luminance.copyOf(exact), stride))
        assertNull(QrCode.decodeLuminance(360, 360, luminance.copyOf(exact - 1), stride), "буфер короче кадра")
        assertNull(QrCode.decodeLuminance(360, 360, luminance, 360 - 1), "строка уже кадра")
        // Огромный шаг строки не переполняет проверку длины, а кадр в одну строку не просит памяти под копию буфера.
        assertNull(QrCode.decodeLuminance(360, 360, luminance, 1_000_000_000), "огромный шаг строки")
        assertNull(QrCode.decodeLuminance(360, 1, ByteArray(360), Int.MAX_VALUE), "одна строка с огромным шагом")
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
        val image = QrPicture(QrCode.encode(link), scale = 4, left = 10, top = 10).argb(6400, 6400)
        assertNull(QrCode.decode(6400, 6400, image))
    }
}

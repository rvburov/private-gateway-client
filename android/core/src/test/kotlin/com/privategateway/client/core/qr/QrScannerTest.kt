package com.privategateway.client.core.qr

import com.privategateway.client.core.qr.QrPicture.Companion.link
import com.privategateway.client.core.qr.QrPicture.Companion.testData
import org.junit.jupiter.api.DynamicTest
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.TestFactory
import kotlin.random.Random
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNull

/** Кадры камеры: плоскость Y 1280×720 с датчика, код в рамке по центру; рамка — квадрат 540 (75 % от 720). */
class QrScannerTest {
    private val modules = QrCode.encode(link)

    @Test
    fun `centred code is read on the first frame`() {
        assertEquals(link, QrScanner().offer(WIDTH, HEIGHT, centred(scale = 5).luminance(WIDTH, HEIGHT)))
    }

    @Test
    fun `light code on dark screen is read on the next frame`() {
        val frame = centred(scale = 5, inverted = true).luminance(WIDTH, HEIGHT)
        val scanner = QrScanner()
        // Один проход на кадр: первый кадр — как есть, инвертированный код в нем не виден.
        assertNull(scanner.offer(WIDTH, HEIGHT, frame))
        assertEquals(link, scanner.offer(WIDTH, HEIGHT, frame))
    }

    @Test
    fun `code with a finder lookalike in its data is read by the full search`() {
        // У этой ссылки в данных есть рисунок, похожий на угловой узор: обычный поиск ZXing его принимает за угол и
        // код не читает ни как есть, ни инвертированно — читает перебор всех узоров, третьим кадром.
        val text = testData.resolve("links/hysteria2/hy2-short-scheme.txt").readText().trim()
        val frame = centred(scale = 8, code = QrCode.encode(text)).luminance(WIDTH, HEIGHT)
        val scanner = QrScanner()
        assertEquals(listOf(null, null, text), List(3) { scanner.offer(WIDTH, HEIGHT, frame) })

        val inverted = centred(scale = 8, code = QrCode.encode(text), inverted = true).luminance(WIDTH, HEIGHT)
        scanner.reset()
        assertEquals(listOf(null, null, null, text), List(4) { scanner.offer(WIDTH, HEIGHT, inverted) })
    }

    @TestFactory
    fun `every shared link is read within a full round of frames`() = QrPicture.links().map { (fixture, text) ->
        DynamicTest.dynamicTest(fixture) {
            // 4 пикселя на модуль: самая длинная ссылка (vmess, 77 модулей) с полем — 340 пикселей, в рамку 540 входит.
            val frame = centred(scale = 4, code = QrCode.encode(text)).luminance(WIDTH, HEIGHT)
            val scanner = QrScanner()
            assertEquals(text, List(QrPass.entries.size) { scanner.offer(WIDTH, HEIGHT, frame) }.firstNotNullOfOrNull { it }, fixture)
        }
    }

    @Test
    fun `frame from a sensor turned for portrait is read`() {
        // Телефон вертикально, датчик лежит боком: кадр 1280×720 — это повернутая на 90° картинка 720×1280.
        val side = QrPicture(modules, scale = 5).side
        val portrait = QrPicture(modules, scale = 5, left = (HEIGHT - side) / 2, top = (WIDTH - side) / 2)
        val frame = ByteArray(WIDTH * HEIGHT) { portrait.gray(HEIGHT - 1 - it / WIDTH, it % WIDTH).toByte() }
        assertEquals(link, QrScanner().offer(WIDTH, HEIGHT, frame))
    }

    @Test
    fun `code outside the frame square is not read`() {
        // Код у левого края кадра: целиком в кадре, но вне квадрата 370–910 по горизонтали.
        val side = QrPicture(modules, scale = 4).side
        val frame = QrPicture(modules, scale = 4, left = 20, top = (HEIGHT - side) / 2).luminance(WIDTH, HEIGHT)
        val scanner = QrScanner()
        assertEquals(List(8) { null }, List(8) { scanner.offer(WIDTH, HEIGHT, frame) })
        assertEquals(link, QrCode.decodeLuminance(WIDTH, HEIGHT, frame), "весь кадр код читает — не читает только рамка")
    }

    @Test
    fun `square is the given share of the shorter side`() {
        // Код сдвинут влево: в квадрат 720 (вся высота) входит, из квадрата 540 вылезает на 80 пикселей.
        val side = QrPicture(modules, scale = 4).side
        val left = (WIDTH - HEIGHT) / 2 + 10
        val frame = QrPicture(modules, scale = 4, left = left, top = (HEIGHT - side) / 2).luminance(WIDTH, HEIGHT)
        val scanner = QrScanner()
        assertEquals(List(4) { null }, List(4) { scanner.offer(WIDTH, HEIGHT, frame) })
        assertEquals(link, QrScanner(cropShare = 1.0).offer(WIDTH, HEIGHT, frame))
    }

    @Test
    fun `code is read once until reset`() {
        val frame = centred(scale = 5).luminance(WIDTH, HEIGHT)
        val scanner = QrScanner()
        assertEquals(link, scanner.offer(WIDTH, HEIGHT, frame))
        assertEquals(List(5) { null }, List(5) { scanner.offer(WIDTH, HEIGHT, frame) }, "экран еще закрывается — повторов нет")
        scanner.reset()
        assertEquals(link, scanner.offer(WIDTH, HEIGHT, frame), "после reset — снова с первого прохода")
    }

    @Test
    fun `row stride wider than the frame and a short last row are read`() {
        val stride = WIDTH + 64
        val frame = centred(scale = 5).luminance(WIDTH, HEIGHT, stride)
        assertEquals(link, QrScanner().offer(WIDTH, HEIGHT, frame, stride))
        // Камера отдает плоскость без выравнивания после последней строки.
        val exact = stride * (HEIGHT - 1) + WIDTH
        assertEquals(link, QrScanner().offer(WIDTH, HEIGHT, frame.copyOf(exact), stride))
    }

    @Test
    fun `broken frames give nothing and never throw`() {
        val stride = WIDTH + 64
        val frame = centred(scale = 5).luminance(WIDTH, HEIGHT, stride)
        val exact = stride * (HEIGHT - 1) + WIDTH
        val scanner = QrScanner()
        assertNull(scanner.offer(WIDTH, HEIGHT, frame.copyOf(exact - 1), stride), "буфер короче кадра")
        assertNull(scanner.offer(WIDTH, HEIGHT, frame, WIDTH - 1), "строка уже кадра")
        assertNull(scanner.offer(WIDTH, HEIGHT, frame, 1_000_000_000), "огромный шаг строки не переполняет проверку")
        assertNull(scanner.offer(0, HEIGHT, frame), "пустой кадр")
        assertNull(scanner.offer(WIDTH, -1, frame), "отрицательная высота")
        assertNull(scanner.offer(8000, 8000, ByteArray(0)), "кадр больше предела пикселей")
        assertNull(scanner.offer(1, 1, byteArrayOf(0)), "кадр в пиксель")
        val noise = Random(7)
        repeat(8) { assertNull(scanner.offer(WIDTH, HEIGHT, noise.nextBytes(WIDTH * HEIGHT)), "шум") }
        // Плохие кадры не занимают сканер: хороший кадр после них читается.
        assertEquals(link, List(QrPass.entries.size) { scanner.offer(WIDTH, HEIGHT, frame, stride) }.firstNotNullOfOrNull { it })
    }

    @Test
    fun `frame over the pixel cap is not read`() {
        // Предел тот же, что у картинок, — 40 Мп: ровно на пределе кадр читается, строкой больше — уже нет.
        val code = QrPicture(modules, scale = 30)
        fun frame(height: Int) = QrPicture(modules, 30, (8000 - code.side) / 2, (height - code.side) / 2).luminance(8000, height)
        assertEquals(link, QrScanner().offer(8000, 5000, frame(5000)))
        assertNull(QrScanner().offer(8000, 5001, frame(5001)))
    }

    @Test
    fun `square share must be within the frame`() {
        assertFailsWith<IllegalArgumentException> { QrScanner(0.0) }
        assertFailsWith<IllegalArgumentException> { QrScanner(1.01) }
    }

    private fun centred(scale: Int, code: Array<BooleanArray> = modules, inverted: Boolean = false): QrPicture {
        val side = QrPicture(code, scale).side
        return QrPicture(code, scale, left = (WIDTH - side) / 2, top = (HEIGHT - side) / 2, inverted = inverted)
    }

    private companion object {
        const val WIDTH = 1280
        const val HEIGHT = 720
    }
}

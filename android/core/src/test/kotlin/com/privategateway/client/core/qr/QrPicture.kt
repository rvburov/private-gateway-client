package com.privategateway.client.core.qr

import java.io.File

/**
 * Картинка с QR-кодом для тестов. Яркость любого пикселя считается по модулям, а не хранится массивом, — так можно
 * описать и снимок в 200 Мп. Модуль — [scale] пикселей, вокруг кода белое поле в 4 модуля, левый верхний угол поля —
 * ([left], [top]). Темный модуль — 0x10, фон — 0xF0; у [inverted] наоборот, как светлый код в темной теме.
 */
internal class QrPicture(
    private val modules: Array<BooleanArray>,
    private val scale: Int,
    private val left: Int = 0,
    private val top: Int = 0,
    private val inverted: Boolean = false,
) {
    /** Сторона кода вместе с белым полем, в пикселях. */
    val side get() = (modules.size + 8) * scale

    fun gray(x: Int, y: Int): Int {
        val row = Math.floorDiv(y - top, scale) - 4
        val column = Math.floorDiv(x - left, scale) - 4
        val dark = row in modules.indices && column in modules[row].indices && modules[row][column]
        return if (dark != inverted) 0x10 else 0xF0
    }

    /** Пиксели ARGB по строкам, как Bitmap.getPixels. */
    fun argb(width: Int, height: Int) = IntArray(width * height) { argb(gray(it % width, it / width)) }

    /** Плоскость Y кадра камеры: строка в памяти шириной [rowStride], выравнивание справа — мусор. */
    fun luminance(width: Int, height: Int, rowStride: Int = width) = ByteArray(rowStride * height) {
        val x = it % rowStride
        if (x < width) gray(x, it / rowStride).toByte() else 0x55
    }

    companion object {
        val testData = File(System.getProperty("testdata") ?: error("Нет пути к shared/testdata"))

        /** Эталонная ссылка VLESS с Reality — как на десктопе. */
        val link = testData.resolve("links/vless/private-gateway.txt").readText().trim()

        /** Все строки эталонов shared/testdata/links — и ссылки, и то, что разбор отвергает: QR несет любой текст. */
        fun links(): List<Pair<String, String>> =
            testData.resolve("links").walkTopDown().filter { it.isFile && it.name.endsWith(".txt") }
                .map { it.relativeTo(testData).invariantSeparatorsPath to it.readText().trim() }
                .sortedBy { it.first }.toList()

        fun argb(gray: Int) = (0xFF shl 24) or (gray shl 16) or (gray shl 8) or gray
    }
}

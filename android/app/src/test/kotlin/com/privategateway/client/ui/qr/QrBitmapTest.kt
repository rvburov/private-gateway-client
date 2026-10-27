package com.privategateway.client.ui.qr

import androidx.test.ext.junit.runners.AndroidJUnit4
import com.privategateway.client.Fixtures
import com.privategateway.client.core.qr.QrCode
import org.junit.Test
import org.junit.runner.RunWith
import kotlin.test.assertEquals

@RunWith(AndroidJUnit4::class)
class QrBitmapTest {
    @Test
    fun `the shared code reads back as the same link for every fixture`() {
        val links = Fixtures.testdata.resolve("links").walk().filter { it.isFile && it.name.endsWith(".txt") }.sortedBy { it.path }
            .map { it.readText().trim() }.filter { it.isNotEmpty() }.toList()
        for (link in links) {
            val bitmap = qrBitmap(QrCode.encode(link), scale = 3)
            val pixels = IntArray(bitmap.width * bitmap.height)
            bitmap.getPixels(pixels, 0, bitmap.width, 0, 0, bitmap.width, bitmap.height)
            assertEquals(link, QrCode.decode(bitmap.width, bitmap.height, pixels), link.take(24))
        }
    }

    @Test
    fun `the code has a quiet zone of four modules`() {
        val modules = QrCode.encode("vless://11111111-2222-3333-4444-555555555555@192.0.2.1:443")
        val bitmap = qrBitmap(modules, scale = 2)
        assertEquals((modules.size + 8) * 2, bitmap.width)
        assertEquals(bitmap.getPixel(0, 0), bitmap.getPixel(7, 7), "поле вокруг кода — светлое")
        assertEquals(true, bitmap.getPixel(8, 8) != bitmap.getPixel(0, 0), "угловая метка начинается сразу за полем")
    }
}

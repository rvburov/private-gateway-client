package com.privategateway.client.ui.qr

import android.content.Context
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.privategateway.client.R
import com.privategateway.client.core.qr.QrCode
import com.privategateway.client.ui.theme.PgTheme
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.annotation.Config
import java.nio.ByteBuffer
import kotlin.test.assertEquals
import kotlin.test.assertNull

/** Сканер QR камерой: кадры с датчика (плоскость Y с запасом в строке) и экран без камеры — в Robolectric ее нет. */
@RunWith(AndroidJUnit4::class)
@Config(qualifiers = "w384dp-h832dp")
class QrCameraTest {
    @get:Rule
    val compose = createComposeRule()

    private val context = ApplicationProvider.getApplicationContext<Context>()
    private val link = "vless://11111111-2222-3333-4444-555555555555@192.0.2.1:443?security=reality&type=tcp#Example"

    /** Плоскость Y кадра 1280×720 с шагом строки [stride] (у датчиков строка бывает длиннее кадра), код — по центру. */
    private fun plane(text: String?, stride: Int, offset: Int): ByteBuffer {
        val bytes = ByteArray(offset + stride * HEIGHT) { 0xFF.toByte() }
        if (text != null) {
            val modules = QrCode.encode(text)
            val scale = 5
            val left = (WIDTH - modules.size * scale) / 2
            val top = (HEIGHT - modules.size * scale) / 2
            for (y in 0 until modules.size * scale) {
                for (x in 0 until modules.size * scale) {
                    if (modules[y / scale][x / scale]) {
                        bytes[offset + (top + y) * stride + left + x] = 0
                    }
                }
            }
        }

        return ByteBuffer.wrap(bytes).apply { position(offset) }
    }

    @Test
    fun `frame with row padding is read once and the plane is left as it was`() {
        val frames = QrFrames()
        val plane = plane(link, stride = 1344, offset = 16)
        assertEquals(link, frames.read(WIDTH, HEIGHT, plane, 1344))
        assertEquals(16, plane.position(), "позиция плоскости не сдвинута — CameraX читает ее и дальше")
        assertNull(frames.read(WIDTH, HEIGHT, plane, 1344), "код уже прочитан — экран закрывается, повторов нет")
    }

    @Test
    fun `frame without a code gives nothing and the next frame with one is read`() {
        val frames = QrFrames()
        assertNull(frames.read(WIDTH, HEIGHT, plane(null, stride = WIDTH, offset = 0), WIDTH))
        // Другой размер буфера — массив кадра пересоздается; проходы идут по кругу, код читается за круг кадров.
        val frame = plane(link, stride = 1408, offset = 0)
        assertEquals(link, List(4) { frames.read(WIDTH, HEIGHT, frame, 1408) }.firstNotNullOfOrNull { it })
    }

    @Test
    fun `screen shows the frame and the hint, closes and passes the code`() {
        val found = mutableListOf<String>()
        var closed = 0
        compose.setContent {
            PgTheme {
                QrCameraScreen(onFound = { found += it }, onFailed = {}, onClose = { closed++ }, camera = { _, _, _ -> })
            }
        }

        compose.onNodeWithTag("qr.frame").assertIsDisplayed()
        compose.onNodeWithText(context.getString(R.string.android_import_qr_camera_title)).assertIsDisplayed()
        compose.onNodeWithTag("qr.camera.close").performClick()
        assertEquals(1, closed)
        assertEquals(emptyList(), found)
    }

    @Test
    fun `code from the camera and a camera that did not open go back`() {
        val found = mutableListOf<String>()
        var failed = 0
        compose.setContent {
            PgTheme {
                QrCameraScreen(
                    onFound = { found += it },
                    onFailed = { failed++ },
                    onClose = {},
                    camera = { onFound, onFailed, _ ->
                        LaunchedEffect(Unit) {
                            onFound(link)
                            onFailed()
                        }
                    },
                )
            }
        }

        compose.waitForIdle()
        assertEquals(listOf(link), found)
        assertEquals(1, failed)
    }

    private companion object {
        const val WIDTH = 1280
        const val HEIGHT = 720
    }
}

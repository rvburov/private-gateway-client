package com.privategateway.client.ui.qr

import android.content.Context
import android.graphics.Bitmap
import android.graphics.ImageDecoder
import android.net.Uri
import androidx.compose.ui.graphics.toArgb
import com.privategateway.client.core.qr.QrCode
import com.privategateway.client.core.qr.QrImageSize
import com.privategateway.client.ui.theme.PgColor
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import javax.inject.Inject
import javax.inject.Singleton

/** Что нашлось на картинке: текст QR-кода, кода нет или картинку не прочитать. */
sealed interface QrRead {
    data class Found(val text: String) : QrRead

    data object None : QrRead

    data object Failed : QrRead
}

/** QR-код с картинки из галереи; в тестах — подделка. */
fun interface QrImages {
    suspend fun read(image: Uri): QrRead
}

/**
 * Картинка — системным декодером (ImageDecoder, в обычной памяти), большое фото уменьшается до 8 Мп (QrImageSize), код
 * ищет [QrCode] тем же разбором, что у десктопа. Не в главном потоке: до четырех проходов по 8 Мп.
 */
@Singleton
class DeviceQrImages @Inject constructor(@param:ApplicationContext private val context: Context) : QrImages {
    override suspend fun read(image: Uri): QrRead = withContext(Dispatchers.Default) {
        try {
            val source = ImageDecoder.createSource(context.contentResolver, image)
            val bitmap = ImageDecoder.decodeBitmap(source) { decoder, info, _ ->
                decoder.allocator = ImageDecoder.ALLOCATOR_SOFTWARE
                val (width, height) = QrImageSize.target(info.size.width, info.size.height)
                decoder.setTargetSize(width, height)
            }
            val width = bitmap.width
            val height = bitmap.height
            val pixels = IntArray(width * height)
            bitmap.getPixels(pixels, 0, width, 0, 0, width, height)
            bitmap.recycle()
            QrCode.decode(width, height, pixels)?.let { QrRead.Found(it) } ?: QrRead.None
        } catch (e: CancellationException) {
            throw e
        } catch (_: Exception) {
            QrRead.Failed
        }
    }
}

/**
 * QR-код ссылки для «Поделиться»: модули — цветом текста макета на белом, поле — 4 модуля (так его читают камеры), по
 * [scale] пикселей на модуль.
 */
fun qrBitmap(modules: Array<BooleanArray>, scale: Int): Bitmap {
    val quiet = 4
    val side = (modules.size + quiet * 2) * scale
    val dark = PgColor.Text.Primary.toArgb()
    val light = PgColor.Surface.Solid.toArgb()
    val pixels = IntArray(side * side) { light }
    for (row in modules.indices) {
        for (column in modules[row].indices) {
            if (!modules[row][column]) {
                continue
            }

            for (dy in 0 until scale) {
                val start = ((row + quiet) * scale + dy) * side + (column + quiet) * scale
                pixels.fill(dark, start, start + scale)
            }
        }
    }

    return Bitmap.createBitmap(pixels, side, side, Bitmap.Config.ARGB_8888)
}

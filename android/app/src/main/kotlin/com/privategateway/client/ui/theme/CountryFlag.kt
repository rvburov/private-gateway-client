package com.privategateway.client.ui.theme

import android.content.Context
import android.graphics.BitmapFactory
import android.util.LruCache
import androidx.compose.foundation.Image
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.testTag
import java.io.IOException

/**
 * Квадратный флаг страны с рамкой — как CountryFlag десктопа и .country-glyph макета: layout.countryFlag.size,
 * radius.countryFlag, рамка space.hairline цвета line.strong. Без кода или без картинки — Земля (UNKNOWN.png). Картинки —
 * из assets/countries/flags (shared/countries/flags), без сети и эмодзи шрифта.
 */
@Composable
fun CountryFlag(code: String, modifier: Modifier = Modifier) {
    val context = LocalContext.current
    val key = CountryFlags.key(code)
    val image = remember(key) { CountryFlags.image(context, key) }
    val shape = RoundedCornerShape(PgRadius.CountryFlag)
    val frame = modifier
        .size(PgLayout.CountryFlag.Size)
        .clip(shape)
        .border(PgSpace.Hairline, PgColor.Line.Strong, shape)
        .testTag("flag.$key")
    if (image != null) {
        Image(image, contentDescription = null, modifier = frame, contentScale = ContentScale.Crop)
    }
}

/** Картинки флагов: код → «XX.png» в assets, неизвестный — «UNKNOWN»; уже открытые держатся в памяти. */
object CountryFlags {
    private const val UNKNOWN = "UNKNOWN"
    private val cache = LruCache<String, ImageBitmap>(64)

    /** Ключ картинки: две латинские буквы — код, иначе Земля. */
    fun key(code: String): String = if (code.length == 2 && code.all { it in 'A'..'Z' || it in 'a'..'z' }) code.uppercase() else UNKNOWN

    fun image(context: Context, key: String): ImageBitmap? {
        cache.get(key)?.let { return it }
        val image = load(context, key) ?: if (key != UNKNOWN) load(context, UNKNOWN) else null
        image?.let { cache.put(key, it) }
        return image
    }

    private fun load(context: Context, key: String): ImageBitmap? = try {
        context.assets.open("countries/flags/$key.png").use { BitmapFactory.decodeStream(it) }?.asImageBitmap()
    } catch (_: IOException) {
        null
    }
}

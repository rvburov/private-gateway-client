package com.privategateway.client.ui.qr

import android.util.Size
import androidx.activity.compose.BackHandler
import androidx.camera.core.CameraSelector
import androidx.camera.core.ImageAnalysis
import androidx.camera.core.ImageProxy
import androidx.camera.core.Preview
import androidx.camera.core.UseCase
import androidx.camera.core.resolutionselector.ResolutionSelector
import androidx.camera.core.resolutionselector.ResolutionStrategy
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.view.PreviewView
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.content.ContextCompat
import androidx.lifecycle.compose.LocalLifecycleOwner
import com.privategateway.client.R
import com.privategateway.client.core.qr.QrScanner
import com.privategateway.client.ui.theme.PgBackground
import com.privategateway.client.ui.theme.PgCard
import com.privategateway.client.ui.theme.PgColor
import com.privategateway.client.ui.theme.PgRadius
import com.privategateway.client.ui.theme.PgSpace
import com.privategateway.client.ui.theme.PgType
import java.nio.ByteBuffer
import java.util.concurrent.Executors

/**
 * Кадры камеры для [QrScanner]: плоскость Y кадра YUV_420_888 (шаг пикселя в ней всегда 1) копируется в один и тот же
 * массив — новый на каждый кадр нагружал бы сборщик мусора 30 раз в секунду. Кадры — из одного потока анализа.
 */
class QrFrames(private val scanner: QrScanner = QrScanner()) {
    private var bytes = ByteArray(0)

    /** Текст кода или null; плоскость читается от ее позиции, сама позиция не меняется. */
    fun read(width: Int, height: Int, plane: ByteBuffer, rowStride: Int): String? {
        val data = plane.duplicate()
        if (bytes.size != data.remaining()) {
            bytes = ByteArray(data.remaining())
        }

        data.get(bytes)
        return scanner.offer(width, height, bytes, rowStride)
    }
}

/** Анализатор CameraX: кадр — в [frames], найденный текст — в [found] (в потоке анализа); кадр закрывается всегда. */
class QrAnalyzer(private val frames: QrFrames, private val found: (String) -> Unit) : ImageAnalysis.Analyzer {
    override fun analyze(image: ImageProxy) {
        image.use {
            val plane = it.planes[0]
            frames.read(it.width, it.height, plane.buffer, plane.rowStride)?.let(found)
        }
    }
}

/**
 * Сканер QR камерой: изображение камеры на весь экран, рамка — центральный квадрат [QrScanner.FRAME_SHARE] от меньшей
 * стороны (на экране она не больше квадрата, где ищется код: предпросмотр заполняет экран с обрезкой), внизу — подсказка
 * и «Закрыть». Код прочитан — [onFound]; камера не открылась — [onFailed]; «Назад» — [onClose]. [camera] — в тестах
 * подделка: в Robolectric камеры нет.
 */
@Composable
fun QrCameraScreen(
    onFound: (String) -> Unit,
    onFailed: () -> Unit,
    onClose: () -> Unit,
    camera: @Composable (found: (String) -> Unit, failed: () -> Unit, modifier: Modifier) -> Unit = { found, failed, modifier ->
        CameraPreview(found, failed, modifier)
    },
) {
    BackHandler(onBack = onClose)
    PgBackground(Modifier.testTag("qr.camera")) {
        camera(onFound, onFailed, Modifier.fillMaxSize())
        BoxWithConstraints(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
            val side = minOf(maxWidth, maxHeight) * QrScanner.FRAME_SHARE.toFloat()
            Box(Modifier.size(side).border(3.dp, PgColor.Accent.Base, RoundedCornerShape(PgRadius.Card)).testTag("qr.frame"))
        }

        PgCard(
            Modifier
                .align(Alignment.BottomCenter)
                .navigationBarsPadding()
                .padding(PgSpace.WindowSideMobile)
                .fillMaxWidth(),
        ) {
            Text(stringResource(R.string.android_import_qr_camera_title), style = PgType.BodyStrong)
            Text(stringResource(R.string.android_import_qr_camera_text), color = PgColor.Text.Secondary, style = PgType.Caption)
            Spacer(Modifier.size(PgSpace.S))
            Text(
                stringResource(R.string.android_import_qr_camera_close),
                color = PgColor.Accent.Text,
                style = PgType.BodyStrong,
                modifier = Modifier
                    .clip(RoundedCornerShape(PgRadius.Button))
                    .clickable(role = Role.Button, onClick = onClose)
                    .padding(vertical = PgSpace.S)
                    .testTag("qr.camera.close"),
            )
        }
    }
}

/**
 * Камера: задняя (нет — передняя, как у планшетов), предпросмотр и анализ кадров около 1280×720 (только последний кадр:
 * пока идет разбор, старые отбрасываются). Привязана к жизненному циклу экрана — окно свернули, камера выключилась.
 * Код — в [onFound] в главном потоке и один раз; камера не открылась (занята, ее нет) — [onFailed].
 */
@Composable
private fun CameraPreview(onFound: (String) -> Unit, onFailed: () -> Unit, modifier: Modifier) {
    val context = LocalContext.current
    val owner = LocalLifecycleOwner.current
    val found by rememberUpdatedState(onFound)
    val failed by rememberUpdatedState(onFailed)
    val view = remember { PreviewView(context).apply { scaleType = PreviewView.ScaleType.FILL_CENTER } }
    AndroidView({ view }, modifier)
    DisposableEffect(owner) {
        val main = ContextCompat.getMainExecutor(context)
        val worker = Executors.newSingleThreadExecutor()
        val future = ProcessCameraProvider.getInstance(context)
        var bound: Pair<ProcessCameraProvider, List<UseCase>>? = null
        var done = false
        future.addListener(
            {
                if (done) {
                    return@addListener
                }

                try {
                    val cameras = future.get()
                    val selector = if (cameras.hasCamera(CameraSelector.DEFAULT_BACK_CAMERA)) CameraSelector.DEFAULT_BACK_CAMERA else CameraSelector.DEFAULT_FRONT_CAMERA
                    val preview = Preview.Builder().build()
                    preview.setSurfaceProvider(view.surfaceProvider)
                    val size = ResolutionStrategy(Size(1280, 720), ResolutionStrategy.FALLBACK_RULE_CLOSEST_HIGHER_THEN_LOWER)
                    val analysis = ImageAnalysis.Builder()
                        .setResolutionSelector(ResolutionSelector.Builder().setResolutionStrategy(size).build())
                        .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST)
                        .build()
                    analysis.setAnalyzer(
                        worker,
                        QrAnalyzer(QrFrames()) { text ->
                            main.execute {
                                if (!done) {
                                    done = true
                                    found(text)
                                }
                            }
                        },
                    )
                    cameras.bindToLifecycle(owner, selector, preview, analysis)
                    bound = cameras to listOf(preview, analysis)
                } catch (_: Exception) {
                    done = true
                    failed()
                }
            },
            main,
        )

        onDispose {
            done = true
            bound?.let { (cameras, cases) -> cameras.unbind(*cases.toTypedArray()) }
            worker.shutdown()
        }
    }
}

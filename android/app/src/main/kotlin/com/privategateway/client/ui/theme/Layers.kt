package com.privategateway.client.ui.theme

import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxScope
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.SideEffect
import androidx.compose.runtime.Stable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.semantics.LiveRegionMode
import androidx.compose.ui.semantics.liveRegion
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import androidx.compose.ui.window.DialogWindowProvider
import kotlinx.coroutines.delay

/*
  Слои поверх окна, как в app.css и MainWindow десктопа: затемнение overlay.scrim, нижний лист (.sheet), диалог
  подтверждения (.dialog) и подсказка сверху (.toast). Лист и диалог — в своем окне от края до края (Dialog), затемнение у
  них свое, системное снято: так оно того же цвета, что в макете, и ложится и под строку состояния.
*/

/** Тон значка диалога (.dialog-icon): плитка — tint тона, значок — его text. */
enum class PgTone(val tint: Color, val ink: Color) {
    Accent(PgColor.Accent.Tint, PgColor.Accent.Text),
    Warning(PgColor.Status.Warning.Tint, PgColor.Status.Warning.Text),
    Caution(PgColor.Status.Caution.Tint, PgColor.Status.Caution.Text),
    Danger(PgColor.Status.Danger.Tint, PgColor.Status.Danger.Text),
}

/** Окно слоя от края до края: затемнение overlay.scrim, нажатие на него и «Назад» зовут [onDismiss]. */
@Composable
private fun PgLayer(onDismiss: () -> Unit, tag: String, alignment: Alignment, content: @Composable BoxScope.() -> Unit) {
    val dismiss by rememberUpdatedState(onDismiss)
    Dialog(onDismissRequest = onDismiss, properties = DialogProperties(usePlatformDefaultWidth = false, decorFitsSystemWindows = false)) {
        val window = (LocalView.current.parent as? DialogWindowProvider)?.window
        SideEffect { window?.setDimAmount(0f) }
        Box(
            Modifier
                .fillMaxSize()
                .background(PgColor.Overlay.Scrim)
                .pointerInput(Unit) { detectTapGestures { dismiss() } }
                .testTag("$tag.scrim"),
            contentAlignment = alignment,
            content = content,
        )
    }
}

/** Слой не пропускает нажатия на свое место к затемнению: нажатие на карточку диалога или лист его не закрывает. */
private fun Modifier.keepTaps(): Modifier = pointerInput(Unit) { detectTapGestures { } }

/**
 * Диалог подтверждения (.dialog, DialogView десктопа, доска 18): значок 26 в плитке 46 тоном [tone], заголовок t-status,
 * текст t-small, две кнопки во всю ширину — основная ([primary], безопасное действие или само удаление) и второстепенная
 * ([secondary]); любая из них может быть «опасной» — красной. Нажатие на затемнение и «Назад» — [onDismiss]. Теги: диалог —
 * [tag], кнопки — «[tag].primary» и «[tag].secondary», затемнение — «[tag].scrim».
 */
@Composable
fun PgDialog(
    title: String,
    text: String?,
    icon: String,
    tone: PgTone,
    primary: String,
    onPrimary: () -> Unit,
    secondary: String,
    onSecondary: () -> Unit,
    onDismiss: () -> Unit,
    tag: String,
    detail: String? = null,
    primaryDanger: Boolean = false,
    secondaryDanger: Boolean = false,
) {
    PgLayer(onDismiss, tag, Alignment.Center) {
        val shape = RoundedCornerShape(PgRadius.Card)
        Column(
            Modifier
                .padding(horizontal = 24.dp)
                .fillMaxWidth()
                .pgShadow(PgShadow.Dialog, shape)
                .clip(shape)
                .background(PgColor.Surface.Solid)
                .keepTaps()
                .padding(20.dp)
                .testTag(tag),
        ) {
            Box(
                Modifier
                    .size(46.dp)
                    .clip(RoundedCornerShape(PgRadius.Button))
                    .background(tone.tint),
                contentAlignment = Alignment.Center,
            ) {
                PgIcon(icon, null, color = tone.ink, size = 26.dp, strokeWidth = 1.9f)
                if (detail != null) {
                    PgIcon(detail, null, color = tone.ink, size = 26.dp, strokeWidth = 1.9f)
                }
            }

            Spacer(Modifier.height(PgSpace.L))
            Text(title, color = PgColor.Text.Primary, style = PgType.Status)
            if (!text.isNullOrEmpty()) {
                Spacer(Modifier.height(PgSpace.L))
                Text(text, color = PgColor.Text.Secondary, style = PgType.Small.copy(lineHeight = PgType.Body.lineHeight))
            }

            Spacer(Modifier.height(18.dp))
            PgPrimaryButton(primary, "$tag.primary", onPrimary, Modifier.fillMaxWidth(), danger = primaryDanger, height = 48.dp)
            Spacer(Modifier.height(PgSpace.S))
            PgSecondaryButton(secondary, "$tag.secondary", onSecondary, Modifier.fillMaxWidth(), danger = secondaryDanger, height = 46.dp)
        }
    }
}

/**
 * Нижний лист (.sheet с .grabber): белый, сверху скругление radius.panel и тень shadow.sheet, начинается на 92 ниже строки
 * состояния и идет до низа экрана; ручка control.grabber 40 × 5. Содержимое прокручивается; лист закрывают нажатие на
 * затемнение и «Назад» (в макете так же — протягивания нет). Теги: лист — [tag], затемнение — «[tag].scrim».
 *
 * [scroll] = false — лист сам не прокручивается: содержимое во всю его высоту раскладывает себя само (карточка сервера:
 * заголовок и кнопки стоят, прокручивается середина — Modifier.weight). [toast] — подсказки окна поверх листа: окно листа
 * закрывает собой окно приложения, где подсказка показывается обычно (пока лист открыт, окно свою не показывает).
 */
@Composable
fun PgSheet(
    onDismiss: () -> Unit,
    tag: String,
    toast: PgToastState? = null,
    scroll: Boolean = true,
    content: @Composable ColumnScope.() -> Unit,
) {
    PgLayer(onDismiss, tag, Alignment.BottomCenter) {
        val shape = RoundedCornerShape(topStart = PgRadius.Panel, topEnd = PgRadius.Panel)
        Column(
            Modifier
                .fillMaxSize()
                .statusBarsPadding()
                .padding(top = 92.dp)
                .pgShadow(PgShadow.Sheet, shape)
                .clip(shape)
                .background(PgColor.Surface.Solid)
                .keepTaps()
                .testTag(tag),
        ) {
            Box(
                Modifier
                    .padding(top = 10.dp, bottom = 10.dp)
                    .size(40.dp, 5.dp)
                    .clip(RoundedCornerShape(3.dp))
                    .background(PgColor.Control.Grabber)
                    .align(Alignment.CenterHorizontally),
            )
            Column(
                Modifier
                    .fillMaxWidth()
                    .weight(1f)
                    .then(if (scroll) Modifier.verticalScroll(rememberScrollState()) else Modifier)
                    .padding(horizontal = PgSpace.WindowSideMobile)
                    .navigationBarsPadding()
                    .imePadding()
                    .padding(bottom = 20.dp),
                content = content,
            )
        }

        if (toast != null) {
            PgToast(toast, Modifier.align(Alignment.TopCenter))
        }
    }
}

/**
 * Подсказка сверху окна (.toast, ToastViewModel десктопа): темная плашка на 1,8 с после действия («IP скопирован») или на
 * 6 с о событии ([long]: «Заголовок · текст», его надо успеть прочесть). Новая подсказка заменяет прежнюю, и отсчет
 * начинается заново. Показывает ее [PgToast]; с любого экрана — через [LocalPgToast].
 */
@Stable
class PgToastState {
    /** Подсказка на экране; каждый показ — новый объект, даже с тем же текстом. */
    internal var current by mutableStateOf<Shown?>(null)
        private set

    fun show(text: String, long: Boolean = false) {
        current = Shown(text, if (long) LONG_MILLIS else SHORT_MILLIS)
    }

    internal fun hide(shown: Shown) {
        if (current === shown) {
            current = null
        }
    }

    internal class Shown(val text: String, val millis: Long)

    companion object {
        const val SHORT_MILLIS = 1_800L
        const val LONG_MILLIS = 6_000L
    }
}

/** Подсказки окна: дает AppScreen, где на экране и сама подсказка; без него вызов — ошибка, а не потерянное сообщение. */
val LocalPgToast = staticCompositionLocalOf<PgToastState> { error("Подсказки окна не заданы: нужен LocalPgToast и PgToast (AppScreen)") }

/**
 * Сама подсказка: по центру, на 56 ниже строки состояния, не шире окна без 20 с каждой стороны; заливка text.primary,
 * текст surface.solid 13/500, тень shadow.popup. Тег — «toast».
 */
@Composable
fun PgToast(state: PgToastState, modifier: Modifier = Modifier) {
    val shown = state.current ?: return
    LaunchedEffect(shown) {
        delay(shown.millis)
        state.hide(shown)
    }

    val shape = RoundedCornerShape(PgRadius.Input)
    Text(
        shown.text,
        modifier
            .statusBarsPadding()
            .padding(top = 56.dp, start = 20.dp, end = 20.dp)
            .pgShadow(PgShadow.Popup, shape)
            .clip(shape)
            .background(PgColor.Text.Primary)
            .padding(horizontal = 14.dp, vertical = PgSpace.S)
            .semantics { liveRegion = LiveRegionMode.Polite }
            .testTag("toast"),
        color = PgColor.Surface.Solid,
        style = PgType.Small.copy(fontWeight = FontWeight.Medium),
        textAlign = TextAlign.Center,
    )
}

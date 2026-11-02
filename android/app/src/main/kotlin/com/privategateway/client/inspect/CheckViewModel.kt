package com.privategateway.client.inspect

import android.content.Context
import android.net.Uri
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.privategateway.client.R
import com.privategateway.client.core.vpn.TunnelState
import com.privategateway.client.ui.Message
import com.privategateway.client.vpn.DelayBreakdown
import com.privategateway.client.vpn.JournalEntry
import com.privategateway.client.vpn.TunnelDelay
import com.privategateway.client.vpn.TunnelJournal
import com.privategateway.client.vpn.TunnelMonitor
import dagger.hilt.android.lifecycle.HiltViewModel
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.IOException
import javax.inject.Inject

/** Запись отчета в файл, выбранный в системном окне «Сохранить»; в тестах — подделка. Записано — true. */
fun interface ReportFiles {
    fun write(file: Uri, text: String): Boolean
}

/** Отчет — в документ, который вернуло системное окно (ACTION_CREATE_DOCUMENT): приложению — только этот файл. */
class DeviceReportFiles @Inject constructor(@param:ApplicationContext private val context: Context) : ReportFiles {
    override fun write(file: Uri, text: String): Boolean = try {
        context.contentResolver.openOutputStream(file)?.use { stream ->
            stream.write(text.toByteArray(Charsets.UTF_8))
            true
        } ?: false
    } catch (_: IOException) {
        false
    } catch (_: SecurityException) {
        false
    }
}

/**
 * Что показывает «Проверка» сверх состояния защиты (доска 7): идет ли проверка, что на телефоне может мешать (null — еще не
 * читали), последний замер задержки и идет ли он, журнал туннеля; [focusDelay] — экран открыт плиткой «Пинг»: прокрутить к
 * «Задержке» (экран забирает признак сам).
 */
data class CheckUi(
    val running: Boolean = false,
    val hints: PhoneHints? = null,
    val delay: DelayBreakdown? = null,
    val measuringDelay: Boolean = false,
    val journal: List<JournalEntry> = emptyList(),
    val focusDelay: Boolean = false,
)

/**
 * «Проверка» телефона (CheckPageViewModel десктопа): строки проверок считаются по состоянию туннеля и замеру внешнего IP
 * (их ведет MainViewModel), а здесь — то, что проверка меряет сама: подсказки Android о прокси, чужом VPN и частном DNS и
 * задержку через туннель. Проверки только читают состояние. Повторное нажатие во время проверки — еще одна проверка сразу
 * после текущей. Отчет пишется в файл, выбранный пользователем; итог — подсказкой «Отчет сохранен».
 */
@HiltViewModel
class CheckViewModel @Inject constructor(
    private val monitor: TunnelMonitor,
    private val hints: PhoneHintSource,
    private val meter: TunnelDelay,
    journal: TunnelJournal,
    private val files: ReportFiles,
) : ViewModel() {
    private val state = MutableStateFlow(CheckUi())

    val ui: StateFlow<CheckUi> = combine(state, journal.entries) { current, entries -> current.copy(journal = entries) }
        .stateIn(viewModelScope, SharingStarted.Eagerly, CheckUi())

    private val notices = MutableSharedFlow<Message>(extraBufferCapacity = 4)

    /** Подсказки окна: «Отчет сохранен». */
    val messages: SharedFlow<Message> = notices.asSharedFlow()

    private var rerun = false

    /** «Проверить» и открытие экрана: что мешает, затем задержка (если еще не меряется). */
    fun run() {
        if (state.value.running) {
            rerun = true
            return
        }

        state.update { it.copy(running = true) }
        viewModelScope.launch {
            try {
                do {
                    rerun = false
                    val up = when (monitor.status.value.state) {
                        TunnelState.Connecting, TunnelState.Connected, TunnelState.Reconnecting, TunnelState.Stopping -> true
                        TunnelState.Off, TunnelState.Error -> false
                    }
                    val found = withContext(Dispatchers.IO) { readHints(up) }
                    state.update { it.copy(hints = found) }
                    if (!state.value.measuringDelay) {
                        measure()
                    }
                } while (rerun)
            } finally {
                state.update { it.copy(running = false) }
            }
        }
    }

    /** «Обновить» у «Задержки»: замер заново (несколько секунд); уже меряется — нажатие не считается. */
    fun measureDelay() {
        if (state.value.measuringDelay) {
            return
        }

        viewModelScope.launch { measure() }
    }

    /** Плитка «Пинг» главной: прокрутить к «Задержке» и померить свежую. */
    fun focusDelay() {
        state.update { it.copy(focusDelay = true) }
        measureDelay()
    }

    /** Экран прокрутил к «Задержке». */
    fun delayShown() {
        state.update { it.copy(focusDelay = false) }
    }

    /** Записать отчет в выбранный файл; записан — «Отчет сохранен». */
    fun save(file: Uri, text: String) {
        viewModelScope.launch {
            val saved = withContext(Dispatchers.IO) { files.write(file, text) }
            if (saved) {
                notices.emit(Message(R.string.diag_exported))
            }
        }
    }

    private fun readHints(up: Boolean): PhoneHints = try {
        hints.read(up)
    } catch (_: RuntimeException) {
        // Android не ответил (нет доступа к сведениям о сети) — мешающего не нашли, а не «ошибка проверки».
        PhoneHints()
    }

    /** Задержка меряется только под защитой: без нее прежнего замера нет — числа были бы о другом подключении. */
    private suspend fun measure() {
        if (monitor.status.value.state != TunnelState.Connected) {
            state.update { it.copy(delay = null) }
            return
        }

        state.update { it.copy(measuringDelay = true) }
        try {
            val result = try {
                meter.measure()
            } catch (e: CancellationException) {
                throw e
            } catch (_: Exception) {
                null
            }

            state.update { it.copy(delay = result) }
        } finally {
            state.update { it.copy(measuringDelay = false) }
        }
    }
}

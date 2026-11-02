package com.privategateway.client.ui.settings

import android.content.ContentResolver
import android.net.Uri
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.privategateway.client.R
import com.privategateway.client.core.backup.BackupError
import com.privategateway.client.core.backup.BackupException
import com.privategateway.client.core.backup.MobileBackup
import com.privategateway.client.data.BackupParts
import com.privategateway.client.data.BackupRepository
import com.privategateway.client.data.BackupRestored
import com.privategateway.client.ui.Message
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.IOException
import java.time.Instant
import javax.inject.Inject

/** Лист «Восстановить из копии»: файл прочитан — нужен пароль; копия открыта — что в ней и «Восстановить». */
sealed interface RestoreStep {
    data class Password(val text: String, val error: Int? = null, val busy: Boolean = false) : RestoreStep

    data class Ready(val copy: MobileBackup, val busy: Boolean = false) : RestoreStep
}

/**
 * «Резервная копия» (доска 14): что сохранить (серверы и подписки, настройки, ключи — только с серверами), пароль не короче
 * [MIN_PASSWORD] знаков, сохранение в выбранный файл; восстановление — пароль, что в копии, «Восстановить»; последняя автокопия.
 */
data class BackupUi(
    val parts: BackupParts = BackupParts(servers = true, settings = true, keys = false),
    val password: String = "",
    val tried: Boolean = false,
    val saving: Boolean = false,
    val lastAuto: Instant? = null,
    val restore: RestoreStep? = null,
    /** Итог последнего восстановления — строкой под карточкой. */
    val restored: BackupRestored? = null,
) {
    val passwordShort: Boolean get() = password.length < MIN_PASSWORD
    val nothing: Boolean get() = !parts.servers && !parts.settings

    companion object {
        const val MIN_PASSWORD = 8
    }
}

@HiltViewModel
class BackupViewModel @Inject constructor(private val backups: BackupRepository) : ViewModel() {
    private val state = MutableStateFlow(BackupUi())
    val ui: StateFlow<BackupUi> = state.asStateFlow()

    private val notices = MutableSharedFlow<Message>(extraBufferCapacity = 4)
    val messages: SharedFlow<Message> = notices.asSharedFlow()

    init {
        refreshAuto()
    }

    fun parts(next: BackupParts) {
        // Ключи — только вместе с серверами: без серверов ключи не к чему приложить.
        state.update { it.copy(parts = if (next.servers) next else next.copy(keys = false)) }
    }

    fun password(text: String) {
        state.update { it.copy(password = text, tried = false) }
    }

    /** «Сохранить файл…» нажата: пароль короткий или сохранять нечего — подсказка; иначе окно «Сохранить» Android. */
    fun canSave(): Boolean {
        val current = state.value
        state.update { it.copy(tried = true) }
        if (current.nothing) {
            notices.tryEmit(Message(R.string.backup_save_nothing))
            return false
        }

        return !current.passwordShort
    }

    fun save(resolver: ContentResolver, file: Uri) {
        val current = state.value
        state.update { it.copy(saving = true) }
        viewModelScope.launch {
            try {
                val text = backups.create(current.parts, current.password)
                withContext(Dispatchers.IO) {
                    resolver.openOutputStream(file, "wt")?.use { it.write(text.toByteArray()) } ?: throw IOException("Нет потока записи")
                }
                state.update { it.copy(password = "", tried = false) }
                notices.emit(Message(R.string.backup_saved))
            } catch (_: IOException) {
                notices.emit(Message(R.string.backup_error_file))
            } catch (_: SecurityException) {
                notices.emit(Message(R.string.backup_error_file))
            } finally {
                state.update { it.copy(saving = false) }
            }
        }
    }

    /** Выбран файл копии: прочитать текст и спросить пароль. */
    fun chosen(resolver: ContentResolver, file: Uri) {
        viewModelScope.launch {
            val text = try {
                withContext(Dispatchers.IO) { resolver.openInputStream(file)?.use { it.readBytes().toString(Charsets.UTF_8) } }
            } catch (_: IOException) {
                null
            } catch (_: SecurityException) {
                null
            }

            if (text == null) {
                notices.emit(Message(R.string.backup_error_file))
            } else {
                state.update { it.copy(restore = RestoreStep.Password(text), restored = null) }
            }
        }
    }

    /** «Открыть» в листе: пароль подошел — что в копии; нет — ошибка в листе. */
    fun open(password: String) {
        val step = state.value.restore as? RestoreStep.Password ?: return
        state.update { it.copy(restore = step.copy(busy = true, error = null)) }
        viewModelScope.launch {
            state.update {
                it.copy(
                    restore = try {
                        RestoreStep.Ready(backups.open(step.text, password))
                    } catch (e: BackupException) {
                        step.copy(busy = false, error = errorText(e.error))
                    },
                )
            }
        }
    }

    fun restore() {
        val step = state.value.restore as? RestoreStep.Ready ?: return
        state.update { it.copy(restore = step.copy(busy = true)) }
        viewModelScope.launch {
            val done = backups.restore(step.copy)
            state.update { it.copy(restore = null, restored = done) }
            refreshAuto()
        }
    }

    /** «Вернуть» в окне «Вернуть автокопию?». */
    fun restoreAuto() {
        viewModelScope.launch {
            try {
                val done = backups.restoreAutoCopy()
                state.update { it.copy(restored = done) }
            } catch (_: BackupException) {
                notices.emit(Message(R.string.backup_error_autocopy))
            }

            refreshAuto()
        }
    }

    fun closeRestore() {
        state.update { it.copy(restore = null) }
    }

    private fun refreshAuto() {
        viewModelScope.launch { state.update { it.copy(lastAuto = backups.lastAutoCopy()) } }
    }

    private fun errorText(error: BackupError): Int = when (error) {
        BackupError.WrongPassword -> R.string.backup_error_password
        BackupError.NewerVersion -> R.string.backup_error_newer
        BackupError.FromComputer -> R.string.backup_error_fromComputer
        BackupError.NotBackup -> R.string.backup_error_notBackup
    }
}

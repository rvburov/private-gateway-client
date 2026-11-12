package com.privategateway.client.ui.account

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.privategateway.client.account.AccountRepository
import com.privategateway.client.account.AccountState
import com.privategateway.client.core.account.AccountError
import com.privategateway.client.core.account.LoginResult
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import javax.inject.Inject

/**
 * Форма входа: почта, пароль, код 2FA или резервный код ([recovery]), идет ли запрос и почему вход не выполнен ([error],
 * у «Слишком много попыток» — минуты [retryMinutes]). Пароль и код живут только здесь, в памяти окна.
 */
data class LoginUi(
    val email: String = "",
    val password: String = "",
    val code: String = "",
    val recovery: Boolean = false,
    val busy: Boolean = false,
    val error: AccountError? = null,
    val retryMinutes: Int = 0,
) {
    /** Поля заполнены: код 2FA — ровно [CODE_LENGTH] цифр, резервный — не пустой. */
    val ready: Boolean
        get() = email.isNotBlank() && password.isNotEmpty() && (if (recovery) code.isNotBlank() else code.length == CODE_LENGTH)

    companion object {
        const val CODE_LENGTH = 6

        /** Длина резервного кода у сайта. */
        const val RECOVERY_MAX = 64
    }
}

/** Вход и выход (доска входа, «Настройки» → «Аккаунт»): состояние входа — из [AccountRepository], форма — своя. */
@HiltViewModel
class AccountViewModel @Inject constructor(private val account: AccountRepository) : ViewModel() {
    /** Вход; null — еще не прочитан: окно ничего не показывает, чтобы экран входа не мелькал. */
    val state: StateFlow<AccountState?> = account.state

    private val form = MutableStateFlow(LoginUi())
    val ui: StateFlow<LoginUi> = form.asStateFlow()

    fun email(text: String) = form.update { it.copy(email = text.take(EMAIL_MAX)) }

    fun password(text: String) = form.update { it.copy(password = text.take(PASSWORD_MAX)) }

    /** Код 2FA — только цифры и не больше шести; резервный — как введен, до длины, которую принимает сайт. */
    fun code(text: String) = form.update {
        it.copy(code = if (it.recovery) text.take(LoginUi.RECOVERY_MAX) else text.filter(Char::isDigit).take(LoginUi.CODE_LENGTH))
    }

    /** «Войти по резервному коду» ↔ «Войти по коду 2FA»: поле кода очищается. */
    fun toggleRecovery() = form.update { it.copy(recovery = !it.recovery, code = "", error = null) }

    /** «Войти» и Enter в последнем поле; запрос уже идет или поля не заполнены — ничего. */
    fun login() {
        val current = form.value
        if (current.busy || !current.ready) {
            return
        }

        form.update { it.copy(busy = true, error = null) }
        viewModelScope.launch {
            val result = account.login(
                current.email,
                current.password,
                current.code.takeIf { !current.recovery },
                current.code.takeIf { current.recovery },
            )
            val failed = result as? LoginResult.Failed
            // После ответа пароль и код стираются, почта остается.
            form.update {
                it.copy(busy = false, password = "", code = "", error = failed?.error, retryMinutes = failed?.retryMinutes ?: 0)
            }
        }
    }

    /** «Выйти» в «Настройках»: защита выключается, вход стирается, экран входа — с пустыми полями. */
    fun logout() {
        form.value = LoginUi()
        viewModelScope.launch { account.logout() }
    }

    private companion object {
        const val EMAIL_MAX = 320
        const val PASSWORD_MAX = 4096
    }
}

package com.privategateway.client.ui

import androidx.datastore.preferences.core.PreferenceDataStoreFactory
import androidx.lifecycle.viewModelScope
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.privategateway.client.FakeBox
import com.privategateway.client.account.AccountRepository
import com.privategateway.client.core.account.AccountDevice
import com.privategateway.client.core.account.AccountError
import com.privategateway.client.core.account.AccountSession
import com.privategateway.client.core.account.AccountSource
import com.privategateway.client.core.account.AccountUser
import com.privategateway.client.core.account.DeviceInfo
import com.privategateway.client.core.account.LoginResult
import com.privategateway.client.core.account.StatusResult
import com.privategateway.client.ui.account.AccountViewModel
import com.privategateway.client.ui.account.LoginUi
import com.privategateway.client.vpn.TunnelControl
import com.privategateway.client.vpn.TunnelJournal
import com.privategateway.client.vpn.TunnelMonitor
import com.privategateway.client.waitForAsync
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.delay
import kotlinx.coroutines.job
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.test.UnconfinedTestDispatcher
import kotlinx.coroutines.test.resetMain
import kotlinx.coroutines.test.setMain
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import org.junit.runner.RunWith
import java.time.Instant
import java.util.concurrent.CopyOnWriteArrayList
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * Модель формы входа: два шага (почта и пароль, а администратору — код), код только цифрами, одно нажатие — один запрос,
 * после ответа код стерт всегда, пароль — на первом шаге и когда второй кончился.
 */
@RunWith(AndroidJUnit4::class)
class AccountViewModelTest {
    @get:Rule
    val folder = TemporaryFolder()

    /** Сайт без сети для модели формы: каждый вход ждет, пока тест не отдаст ответ из [pending]. */
    private class SlowSource : AccountSource {
        val pending = Channel<CompletableDeferred<LoginResult>>(Channel.UNLIMITED)
        val calls = CopyOnWriteArrayList<Triple<String, String?, String?>>()

        override suspend fun login(email: String, password: String, code: String?, recovery: String?, device: DeviceInfo): LoginResult {
            calls += Triple(password, code, recovery)
            val answer = CompletableDeferred<LoginResult>()
            pending.send(answer)
            return answer.await()
        }

        override suspend fun status(token: String): StatusResult = StatusResult.Unreachable

        override suspend fun logout(token: String) = Unit
    }

    private object NoControl : TunnelControl {
        override fun connect() = Unit

        override fun disconnect() = Unit
    }

    private val session =
        AccountSession("tok-secret-1", Instant.now().plusSeconds(86_400), AccountUser("admin@example.com", "Админ"), AccountDevice(42, "Pixel"))

    /** Модель на настоящем репозитории с подделкой сайта; после теста все области остановлены. */
    @OptIn(ExperimentalCoroutinesApi::class)
    private fun withModel(test: (SlowSource, AccountViewModel, AccountRepository) -> Unit) {
        Dispatchers.setMain(UnconfinedTestDispatcher())
        val scope = CoroutineScope(Dispatchers.IO + SupervisorJob())
        val source = SlowSource()
        val repository = AccountRepository(
            PreferenceDataStoreFactory.create(scope = scope) { folder.root.resolve("account.preferences_pb") },
            FakeBox(),
            source,
            NoControl,
            TunnelMonitor(),
            TunnelJournal(),
        )
        val model = AccountViewModel(repository)
        try {
            test(source, model, repository)
        } finally {
            runBlocking { model.viewModelScope.coroutineContext.job.cancelAndJoin() }
            repository.close()
            runBlocking { scope.coroutineContext.job.cancelAndJoin() }
            Dispatchers.resetMain()
        }
    }

    /** Отдать ответ сайта на запрос, который ждет, и дождаться формы после него. */
    private fun answer(source: SlowSource, model: AccountViewModel, result: LoginResult): LoginUi {
        waitForAsync { source.pending.receive() }.complete(result)
        return waitForAsync {
            while (model.ui.value.busy) delay(10)
            model.ui.value
        }
    }

    @Test
    fun `member signs in with email and password only and the password is cleared`() = withModel { source, model, repository ->
        model.email("user@example.com")
        assertFalse(model.ui.value.ready, "пароля нет")
        model.password("pw")
        assertTrue(model.ui.value.ready, "первый шаг — без кода: 2FA только у администраторов")
        // Цифры других письменностей (арабско-индийские, полноширинные) Char.isDigit пропустил бы, а сайт их не примет.
        model.code("١٢٣٤٥٦１２")
        assertEquals("", model.ui.value.code, "код 2FA — только цифры 0-9")
        model.code("12a34-56789")
        assertEquals("123456", model.ui.value.code, "код 2FA — только цифры, не больше шести")
        model.login()
        assertTrue(model.ui.value.busy)
        model.login()

        val after = answer(source, model, LoginResult.Failed(AccountError.Invalid))
        assertEquals(LoginUi(email = "user@example.com", error = AccountError.Invalid), after)
        assertEquals(listOf(Triple<String, String?, String?>("pw", null, null)), source.calls, "второе нажатие во время запроса — ничего; кода нет")

        model.toggleRecovery()
        model.code(" abcd-efgh ")
        assertEquals(" abcd-efgh ", model.ui.value.code, "резервный код — как введен")
        assertNull(model.ui.value.error)

        model.logout()
        assertEquals(LoginUi(), model.ui.value, "после выхода — пустые поля")
        // Выход идет в области репозитория: окно его не ждет.
        waitForAsync {
            while (repository.allowed()) {
                delay(10)
            }
        }
    }

    @Test
    fun `administrator enters the code on a second step while the password waits`() = withModel { source, model, repository ->
        model.email("admin@example.com")
        model.password("pw")
        model.login()

        var after = answer(source, model, LoginResult.CodeRequired)
        assertEquals(LoginUi(email = "admin@example.com", password = "pw", codeStep = true), after, "«нужен код» — второй шаг без ошибки")
        assertFalse(after.ready, "кода еще нет")
        model.code("12345")
        assertFalse(model.ui.value.ready, "пять цифр — мало")
        model.code("111111")
        assertTrue(model.ui.value.ready)
        model.login()

        after = answer(source, model, LoginResult.Failed(AccountError.Code))
        assertEquals(LoginUi(email = "admin@example.com", password = "pw", codeStep = true, error = AccountError.Code), after,
            "неверный код: код стерт, пароль ждет следующего")

        model.toggleRecovery()
        model.code(" abcd-efgh ")
        model.login()
        model.back()
        assertTrue(model.ui.value.codeStep, "пока идет запрос, назад нельзя")
        after = answer(source, model, LoginResult.Ok(session))
        assertEquals(LoginUi(email = "admin@example.com"), after, "вошли — пароль больше не нужен")
        assertEquals(
            listOf(Triple<String, String?, String?>("pw", null, null), Triple("pw", "111111", null), Triple("pw", null, " abcd-efgh ")),
            source.calls,
        )
        assertTrue(waitForAsync { repository.allowed() })
    }

    @Test
    fun `another account and a changed password return to the first step`() = withModel { source, model, _ ->
        model.email("admin@example.com")
        model.password("pw")
        model.login()
        answer(source, model, LoginResult.CodeRequired)
        model.toggleRecovery()

        model.back()
        assertEquals(LoginUi(email = "admin@example.com"), model.ui.value, "почта остается, пароль и код стерты")

        model.password("pw")
        model.login()
        answer(source, model, LoginResult.CodeRequired)
        model.code("123456")
        model.login()
        val after = answer(source, model, LoginResult.Failed(AccountError.Invalid))
        assertEquals(LoginUi(email = "admin@example.com", error = AccountError.Invalid), after, "пароль сменили, пока вводили код")
    }
}

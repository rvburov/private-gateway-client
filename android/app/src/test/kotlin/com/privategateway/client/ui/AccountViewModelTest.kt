package com.privategateway.client.ui

import androidx.datastore.preferences.core.PreferenceDataStoreFactory
import androidx.lifecycle.viewModelScope
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.privategateway.client.FakeBox
import com.privategateway.client.account.AccountRepository
import com.privategateway.client.core.account.AccountError
import com.privategateway.client.core.account.AccountSource
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
import java.util.concurrent.CopyOnWriteArrayList
import kotlin.test.assertEquals
import kotlin.test.assertNull
import kotlin.test.assertTrue

/** Модель формы входа: код только цифрами, одно нажатие — один запрос, после ответа пароль и код стерты. */
@RunWith(AndroidJUnit4::class)
class AccountViewModelTest {
    @get:Rule
    val folder = TemporaryFolder()

    /** Сайт без сети для модели формы: ответ на вход ждет, пока тест его не отдаст. */
    private class SlowSource : AccountSource {
        val answer = CompletableDeferred<LoginResult>()
        val codes = CopyOnWriteArrayList<Pair<String?, String?>>()

        override suspend fun login(email: String, password: String, code: String?, recovery: String?, device: DeviceInfo): LoginResult {
            codes += code to recovery
            return answer.await()
        }

        override suspend fun status(token: String): StatusResult = StatusResult.Unreachable

        override suspend fun logout(token: String) = Unit
    }

    private object NoControl : TunnelControl {
        override fun connect() = Unit

        override fun disconnect() = Unit
    }

    @OptIn(ExperimentalCoroutinesApi::class)
    @Test
    fun `form clears the password and the code after the answer and keeps the email`() {
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
            model.email("user@example.com")
            model.password("pw")
            model.code("12a34-56789")
            assertEquals("123456", model.ui.value.code, "код 2FA — только цифры, не больше шести")
            model.login()
            assertTrue(model.ui.value.busy)
            model.login()
            waitForAsync { while (source.codes.isEmpty()) delay(10) }

            source.answer.complete(LoginResult.Failed(AccountError.Code))
            val after = waitForAsync {
                while (model.ui.value.busy) delay(10)
                model.ui.value
            }
            assertEquals(LoginUi(email = "user@example.com", error = AccountError.Code), after)
            assertEquals(listOf<Pair<String?, String?>>("123456" to null), source.codes, "второе нажатие во время запроса — ничего")

            model.toggleRecovery()
            model.code(" abcd-efgh ")
            assertEquals(" abcd-efgh ", model.ui.value.code, "резервный код — как введен")
            assertNull(model.ui.value.error)

            model.logout()
            assertEquals(LoginUi(), model.ui.value, "после выхода — пустые поля")
            assertEquals(false, waitForAsync { repository.allowed() })
        } finally {
            runBlocking { model.viewModelScope.coroutineContext.job.cancelAndJoin() }
            repository.close()
            runBlocking { scope.coroutineContext.job.cancelAndJoin() }
            Dispatchers.resetMain()
        }
    }
}

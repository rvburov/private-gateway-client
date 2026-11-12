package com.privategateway.client.account

import androidx.datastore.preferences.core.PreferenceDataStoreFactory
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.privategateway.client.FakeBox
import com.privategateway.client.core.account.AccountDevice
import com.privategateway.client.core.account.AccountError
import com.privategateway.client.core.account.AccountSession
import com.privategateway.client.core.account.AccountSource
import com.privategateway.client.core.account.AccountUser
import com.privategateway.client.core.account.DeviceInfo
import com.privategateway.client.core.account.LoginResult
import com.privategateway.client.core.account.StatusResult
import com.privategateway.client.core.vpn.TunnelState
import com.privategateway.client.core.vpn.TunnelStatus
import com.privategateway.client.data.SecretBox
import com.privategateway.client.vpn.TunnelControl
import com.privategateway.client.vpn.TunnelJournal
import com.privategateway.client.vpn.TunnelMonitor
import com.privategateway.client.waitForAsync
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.job
import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import org.junit.runner.RunWith
import java.time.Duration
import java.time.Instant
import java.util.concurrent.CopyOnWriteArrayList
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/** Вход в аккаунт сайта на телефоне: хранение токена, uid, проверка доступа и выход — без сети и Keystore. */
@RunWith(AndroidJUnit4::class)
class AccountRepositoryTest {
    @get:Rule
    val folder = TemporaryFolder()

    private val scopes = mutableListOf<CoroutineScope>()
    private val repositories = mutableListOf<AccountRepository>()
    private val now = Instant.parse("2026-11-12T10:00:00Z")
    private val monitor = TunnelMonitor()
    private val control = FakeControl()
    private val journal = TunnelJournal()
    private val source = FakeSource()
    private val box = FakeBox()

    private class FakeControl : TunnelControl {
        @Volatile var disconnects = 0

        override fun connect() = Unit

        override fun disconnect() {
            disconnects++
        }
    }

    /** Сайт без сети: ответы задает тест; что спросили — в [calls]. */
    private class FakeSource : AccountSource {
        var login: LoginResult = LoginResult.Failed(AccountError.Network)
        var status: StatusResult = StatusResult.Unreachable
        val calls = CopyOnWriteArrayList<String>()
        val devices = CopyOnWriteArrayList<DeviceInfo>()
        val tokens = CopyOnWriteArrayList<String>()

        override suspend fun login(email: String, password: String, code: String?, recovery: String?, device: DeviceInfo): LoginResult {
            calls += "login"
            devices += device
            return login
        }

        override suspend fun status(token: String): StatusResult {
            calls += "status"
            tokens += token
            return status
        }

        override suspend fun logout(token: String) {
            calls += "logout"
            tokens += token
        }
    }

    /** Хранилище ключей, которое не работает (Keystore телефона сломан или недоступен). */
    private class BrokenBox : SecretBox {
        override fun seal(plain: ByteArray): ByteArray = throw java.security.KeyStoreException("нет ключа")

        override fun open(sealed: ByteArray): ByteArray = throw java.security.KeyStoreException("нет ключа")
    }

    private val file by lazy { folder.root.resolve("account.preferences_pb") }

    /** Хранилище и репозиторий «процесса»; новый вызов на том же файле — как перезапуск приложения. */
    private fun repository(secrets: SecretBox = box): AccountRepository {
        val scope = CoroutineScope(Dispatchers.IO + SupervisorJob()).also { scopes += it }
        val store = PreferenceDataStoreFactory.create(scope = scope) { file }
        return AccountRepository(store, secrets, source, control, monitor, journal).also {
            it.clock = { now }
            repositories += it
        }
    }

    @After
    fun close() {
        repositories.forEach { it.close() }
        runBlocking { scopes.forEach { it.coroutineContext.job.cancelAndJoin() } }
    }

    private fun session(expires: Instant = now.plus(Duration.ofDays(30))) =
        AccountSession("tok-secret-1", expires, AccountUser("user@example.com", "Участник"), AccountDevice(42, "Google Pixel 8"))

    private fun <T> wait(block: suspend () -> T): T = waitForAsync(block)

    private fun state(repository: AccountRepository) = wait { repository.state.first { it != null }!! }

    @Test
    fun `token is stored only sealed and the sign-in survives a restart`() {
        val first = repository()
        assertFalse(state(first).signedIn, "до входа входа нет")
        source.login = LoginResult.Ok(session())

        assertEquals(LoginResult.Ok(session()), wait { first.login("user@example.com", "pw", "123456", null) })
        assertTrue(box.sealed > 0, "токен прошел через ящик")
        val raw = file.readBytes().toString(Charsets.ISO_8859_1)
        assertFalse("tok-secret-1" in raw, "открытого токена в файле нет")
        val info = assertNotNull(wait { first.state.first { it?.signedIn == true } }?.info)
        assertEquals(AccountInfo("user@example.com", "Участник", "Google Pixel 8", now.plus(Duration.ofDays(30)), null), info)
        assertTrue(wait { first.allowed() })

        // Перезапуск: тот же файл, новый процесс — вход на месте, сайт получает тот же токен.
        first.close()
        runBlocking { scopes.first().coroutineContext.job.cancelAndJoin() }
        val again = repository()
        assertTrue(state(again).signedIn)
        source.status = StatusResult.Ok(now.plus(Duration.ofDays(31)), AccountUser("user@example.com", "Новое имя"), AccountDevice(42, "Pixel"))
        wait { again.refresh() }
        assertEquals(listOf("tok-secret-1"), source.tokens)
        val confirmed = wait { again.state.first { it?.info?.displayName == "Новое имя" } }!!.info!!
        assertEquals(now.plus(Duration.ofDays(31)), confirmed.expiresAt, "сайт продлил срок")
        assertEquals(now, confirmed.checkedAt)
        assertEquals("Pixel", confirmed.deviceName)
    }

    @Test
    fun `device uid is created once and sent with the sign-in`() {
        val first = repository()
        val uid = wait { first.deviceUid() }
        assertTrue(DeviceInfo.validUid(uid), uid)
        assertEquals(uid, wait { first.deviceUid() })

        source.login = LoginResult.Ok(session())
        wait { first.login("user@example.com", "pw", null, "abcd-efgh") }
        val device = source.devices.single()
        assertEquals(uid, device.uid)
        assertEquals("android", device.platform)
        assertTrue(device.name.isNotBlank() && device.name.length <= DeviceInfo.NAME_MAX)
        assertTrue(device.appVersion.isNotBlank())

        // Выход стирает вход, но не uid: повторный вход того же телефона места на сайте не тратит.
        wait { first.logout() }
        first.close()
        runBlocking { scopes.first().coroutineContext.job.cancelAndJoin() }
        assertEquals(uid, wait { repository().deviceUid() })
    }

    @Test
    fun `failed sign-in keeps nothing`() {
        val repository = repository()
        source.login = LoginResult.Failed(AccountError.RateLimited, 3)
        assertEquals(LoginResult.Failed(AccountError.RateLimited, 3), wait { repository.login("user@example.com", "pw", "123456", null) })
        assertFalse(state(repository).signedIn)
        assertFalse(wait { repository.allowed() })
    }

    @Test
    fun `without a working key store the sign-in is not attempted`() {
        val repository = repository(BrokenBox())
        source.login = LoginResult.Ok(session())
        assertEquals(LoginResult.Failed(AccountError.Store), wait { repository.login("user@example.com", "pw", "123456", null) })
        assertEquals(emptyList(), source.calls, "без хранилища на сайт не ходим")
        assertFalse(state(repository).signedIn)
    }

    @Test
    fun `401 revokes access, erases the token and stops protection`() {
        val repository = repository()
        source.login = LoginResult.Ok(session())
        wait { repository.login("user@example.com", "pw", "123456", null) }
        monitor.set(TunnelStatus(TunnelState.Connected, serverId = "nl"))

        source.status = StatusResult.Revoked
        wait { repository.refresh() }

        val after = wait { repository.state.first { it?.signedIn == false } }!!
        assertEquals(AccountNotice.Revoked, after.notice)
        assertEquals(1, control.disconnects, "защита выключена")
        assertFalse(wait { repository.allowed() })
        assertFalse(repository.allowedNow())
        assertFalse(file.readBytes().toString(Charsets.ISO_8859_1).contains("token"), "токен стерт")
    }

    @Test
    fun `no answer keeps access until the confirmed term and then signs out`() {
        val repository = repository()
        source.login = LoginResult.Ok(session(expires = now.plus(Duration.ofHours(1))))
        wait { repository.login("user@example.com", "pw", "123456", null) }
        monitor.set(TunnelStatus(TunnelState.Connected, serverId = "nl"))

        source.status = StatusResult.Unreachable
        wait { repository.refresh() }
        assertTrue(state(repository).signedIn, "сайт не ответил — вход остается")
        assertTrue(wait { repository.allowed() })
        assertEquals(0, control.disconnects, "без ответа сайта защита не выключается")

        // Срок прошел без подтверждения — выход как при истечении.
        repository.clock = { now.plus(Duration.ofHours(2)) }
        assertFalse(wait { repository.allowed() }, "срок прошел — подключаться нельзя и до проверки")
        wait { repository.refresh() }
        assertEquals(AccountNotice.Expired, wait { repository.state.first { it?.signedIn == false } }!!.notice)
        assertEquals(1, control.disconnects)
    }

    @Test
    fun `revocation with protection off does not wake the service`() {
        val repository = repository()
        source.login = LoginResult.Ok(session())
        wait { repository.login("user@example.com", "pw", "123456", null) }
        source.status = StatusResult.Revoked
        wait { repository.refresh() }
        assertFalse(state(repository).signedIn)
        assertEquals(0, control.disconnects)
    }

    @Test
    fun `sign-out erases the sign-in even without network and tells the site`() {
        val repository = repository()
        source.login = LoginResult.Ok(session())
        wait { repository.login("user@example.com", "pw", "123456", null) }
        monitor.set(TunnelStatus(TunnelState.Connected, serverId = "nl"))

        wait { repository.logout() }

        val after = state(repository)
        assertFalse(after.signedIn)
        assertNull(after.notice, "выход сам — без сообщения об отзыве")
        assertEquals(1, control.disconnects)
        assertEquals(listOf("login", "logout"), source.calls)
        assertEquals(listOf("tok-secret-1"), source.tokens, "сайт получил тот же токен")
    }

    @Test
    fun `seeded sign-in for device tests is not checked with the site`() {
        val repository = repository()
        wait { repository.seedForTests(session()) }
        assertTrue(wait { repository.allowed() })
        wait { repository.refresh() }
        assertEquals(emptyList(), source.calls)
        assertNotEquals(0, box.sealed)
    }

    @Test
    fun `window shown right after the start does not ask the site twice`() {
        val repository = repository()
        source.login = LoginResult.Ok(session())
        source.status = StatusResult.Ok(now.plus(Duration.ofDays(30)), AccountUser("user@example.com", "Участник"), AccountDevice(42, "Pixel"))
        wait { repository.login("user@example.com", "pw", "123456", null) }
        repository.checkNow()
        Thread.sleep(200)
        assertEquals(listOf("login"), source.calls, "сразу после входа сайт уже ответил")

        repository.clock = { now.plus(Duration.ofMinutes(2)) }
        repository.checkNow()
        wait {
            while ("status" !in source.calls) {
                kotlinx.coroutines.delay(10)
            }
        }
    }
}

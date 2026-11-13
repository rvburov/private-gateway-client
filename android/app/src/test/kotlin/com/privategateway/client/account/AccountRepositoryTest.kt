package com.privategateway.client.account

import androidx.datastore.core.DataStore
import androidx.datastore.preferences.core.Preferences
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
import com.privategateway.client.core.account.UpdatesFeed
import com.privategateway.client.core.vpn.TunnelErrors
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
import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.emitAll
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.job
import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import org.junit.runner.RunWith
import java.io.IOException
import java.security.KeyStoreException
import java.security.ProviderException
import java.time.Duration
import java.time.Instant
import java.util.concurrent.CopyOnWriteArrayList
import javax.crypto.AEADBadTagException
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
    private val journal = TunnelJournal()
    private val source = FakeSource()
    private val control = FakeControl(source.calls)
    private val box = FakeBox()

    /** Служба без Android: команда выключить защиту — в [log] рядом с запросами к сайту, чтобы был виден порядок. */
    private class FakeControl(private val log: MutableList<String>) : TunnelControl {
        @Volatile var disconnects = 0

        /** Что сделать в момент команды (тест смотрит, стерт ли уже вход). */
        @Volatile var onDisconnect: () -> Unit = {}

        override fun connect() = Unit

        override fun disconnect() {
            disconnects++
            log += "disconnect"
            onDisconnect()
        }
    }

    /** Сайт без сети: ответы задает тест; что спросили — в [calls]. */
    private class FakeSource : AccountSource {
        var login: LoginResult = LoginResult.Failed(AccountError.Network)
        var status: StatusResult = StatusResult.Unreachable
        var feed: UpdatesFeed = UpdatesFeed.Unreachable
        val channels = CopyOnWriteArrayList<String>()
        val calls = CopyOnWriteArrayList<String>()
        val devices = CopyOnWriteArrayList<DeviceInfo>()
        val tokens = CopyOnWriteArrayList<String>()

        /** Сайт не отвечает на выход (обрыв под защитой, медленная сеть). */
        @Volatile var logoutHangs = false

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
            if (logoutHangs) {
                awaitCancellation()
            }
        }

        override suspend fun updates(token: String, channel: String): UpdatesFeed {
            calls += "updates"
            tokens += token
            channels += channel
            return feed
        }
    }

    /** Ящик, который по желанию теста не открывает: [failure] — что бросает Keystore. Запечатывает как [FakeBox]. */
    private class FlakyBox(private val inner: FakeBox = FakeBox()) : SecretBox {
        @Volatile var failure: Exception? = null

        override fun seal(plain: ByteArray): ByteArray = inner.seal(plain)

        override fun open(sealed: ByteArray): ByteArray {
            failure?.let { throw it }
            return inner.open(sealed)
        }
    }

    /** Хранилище, которое по желанию теста не читается или не пишется (файл не открыть, диск полон). */
    private class FlakyStore(private val real: DataStore<Preferences>) : DataStore<Preferences> {
        @Volatile var failReads = false

        @Volatile var failWrites = false

        override val data: Flow<Preferences> = flow {
            if (failReads) {
                throw IOException("файл не читается")
            }

            emitAll(real.data)
        }

        override suspend fun updateData(transform: suspend (t: Preferences) -> Preferences): Preferences {
            if (failWrites) {
                throw IOException("диск полон")
            }

            return real.updateData(transform)
        }
    }

    /** Хранилище ключей, которое не работает (Keystore телефона сломан или недоступен). */
    private class BrokenBox : SecretBox {
        override fun seal(plain: ByteArray): ByteArray = throw java.security.KeyStoreException("нет ключа")

        override fun open(sealed: ByteArray): ByteArray = throw java.security.KeyStoreException("нет ключа")
    }

    private val file by lazy { folder.root.resolve("account.preferences_pb") }

    /** Хранилище (как в приложении — с заменой испорченного файла) в своей области; новый вызов на том же файле — перезапуск. */
    private fun store(): DataStore<Preferences> {
        val scope = CoroutineScope(Dispatchers.IO + SupervisorJob()).also { scopes += it }
        return accountDataStore(scope) { file }
    }

    /** Репозиторий «процесса» на [store]. */
    private fun repository(secrets: SecretBox = box, store: DataStore<Preferences> = store()): AccountRepository {
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
        // Проба хранилища ключей перед входом — одна печать, токен — вторая.
        assertTrue(box.sealed >= 2, "токен прошел через ящик")
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
    fun `administrator without a code is asked for one and nothing is saved`() {
        // 0.2.1: первый шаг — без кода; «нужен код» — входа еще нет, в журнале — только итог, без почты и пароля.
        val repository = repository()
        source.login = LoginResult.CodeRequired
        assertEquals(LoginResult.CodeRequired, wait { repository.login("admin@example.com", "pw-secret", null, null) })
        assertFalse(state(repository).signedIn)
        assertFalse(wait { repository.allowed() })
        val messages = journal.entries.value.map { it.message }
        assertTrue(messages.any { "code required" in it }, messages.toString())
        assertFalse(messages.any { "admin@example.com" in it || "pw-secret" in it }, messages.toString())

        source.login = LoginResult.Ok(session())
        assertEquals(LoginResult.Ok(session()), wait { repository.login("admin@example.com", "pw-secret", "123456", null) })
        assertTrue(wait { repository.allowed() })
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
        assertTrue(wait { repository.state.first { it?.signedIn == true } }!!.signedIn, "сайт не ответил — вход остается")
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
        assertFalse(wait { repository.allowed() })
        assertFalse(wait { repository.state.first { it?.signedIn == false } }!!.signedIn)
        assertEquals(0, control.disconnects)
    }

    @Test
    fun `sign-out erases the sign-in even without network and tells the site`() {
        val repository = repository()
        source.login = LoginResult.Ok(session())
        wait { repository.login("user@example.com", "pw", "123456", null) }
        monitor.set(TunnelStatus(TunnelState.Connected, serverId = "nl"))

        var allowedAtStop: Boolean? = null
        control.onDisconnect = { allowedAtStop = runBlocking { repository.allowed() } }

        wait { repository.logout() }

        val after = wait { repository.state.first { it?.signedIn == false } }!!
        assertNull(after.notice, "выход сам — без сообщения об отзыве")
        assertEquals(1, control.disconnects)
        // Сайту — пока защита идет (запрос идет через туннель), и только потом защита выключается.
        assertEquals(listOf("login", "logout", "disconnect"), source.calls)
        assertEquals(false, allowedAtStop, "служба, получив команду, уже не пустит подключение")
        assertEquals(listOf("tok-secret-1"), source.tokens, "сайт получил тот же токен")
    }

    @Test
    fun `sign-out does not wait long for the site and still stops protection`() {
        val repository = repository()
        source.login = LoginResult.Ok(session())
        wait { repository.login("user@example.com", "pw", "123456", null) }
        monitor.set(TunnelStatus(TunnelState.Connected, serverId = "nl"))
        source.logoutHangs = true
        repository.logoutWaitMs = 200

        val started = System.nanoTime()
        wait { repository.logout() }
        val tookMs = (System.nanoTime() - started) / 1_000_000

        assertTrue(tookMs < 5_000, "выход ждал сайт $tookMs мс")
        assertEquals(1, control.disconnects, "сайт не ответил — защита все равно выключена")
        assertFalse(wait { repository.allowed() })
        assertTrue(journal.entries.value.any { "site did not answer" in it.message })
    }

    @Test
    fun `window sign-out runs in the repository scope`() {
        val repository = repository()
        source.login = LoginResult.Ok(session())
        wait { repository.login("user@example.com", "pw", "123456", null) }

        repository.signOut()
        wait { repository.state.first { it?.signedIn == false } }
        wait {
            while ("logout" !in source.calls) {
                delay(10)
            }
        }
    }

    @Test
    fun `revocation erases the sign-in before it stops protection`() {
        val repository = repository()
        source.login = LoginResult.Ok(session())
        wait { repository.login("user@example.com", "pw", "123456", null) }
        monitor.set(TunnelStatus(TunnelState.Connected, serverId = "nl"))
        var allowedAtStop: Boolean? = null
        control.onDisconnect = { allowedAtStop = runBlocking { repository.allowed() } }

        source.status = StatusResult.Revoked
        wait { repository.refresh() }

        assertEquals(1, control.disconnects)
        assertEquals(false, allowedAtStop, "подключение, которое монитор еще не показал, служба отклонит сама")
    }

    @Test
    fun `protection running without a sign-in is stopped by the check`() {
        val repository = repository()
        assertFalse(state(repository).signedIn)
        // Подключение разминулось с выходом: туннель поднялся, а входа уже нет.
        monitor.set(TunnelStatus(TunnelState.Connected, serverId = "nl"))
        wait { repository.refresh() }
        assertEquals(1, control.disconnects)
        assertEquals(emptyList(), source.calls.filter { it != "disconnect" }, "без входа сайт не спрашиваем")

        // Выключено — службу не будить.
        monitor.set(TunnelStatus())
        wait { repository.refresh() }
        assertEquals(1, control.disconnects)
    }

    @Test
    fun `sign-in clears the no-access error from the home screen and keeps other errors`() {
        val repository = repository()
        source.login = LoginResult.Ok(session())
        monitor.set(TunnelStatus(TunnelState.Error, error = TunnelErrors.NO_ACCESS))
        wait { repository.login("user@example.com", "pw", "123456", null) }
        assertEquals(TunnelStatus(), monitor.status.value, "вход есть — «Нет доступа» больше не показывается")

        wait { repository.logout() }
        monitor.set(TunnelStatus(TunnelState.Error, error = TunnelErrors.SERVER_NOT_RESPONDING))
        wait { repository.login("user@example.com", "pw", "123456", null) }
        assertEquals(TunnelErrors.SERVER_NOT_RESPONDING, monitor.status.value.error, "другая ошибка входом не снимается")
    }

    @Test
    fun `key store that does not answer keeps the sign-in, a lost key signs out`() {
        val flaky = FlakyBox()
        val repository = repository(flaky)
        source.login = LoginResult.Ok(session())
        source.status = StatusResult.Ok(now.plus(Duration.ofDays(30)), AccountUser("user@example.com", "Участник"), AccountDevice(42, "Pixel"))
        wait { repository.login("user@example.com", "pw", "123456", null) }
        monitor.set(TunnelStatus(TunnelState.Connected, serverId = "nl"))

        // «keystore busy» сразу после загрузки — временный сбой: вход и защита остаются, сайт не спрашиваем (токен не открыть).
        flaky.failure = ProviderException("keystore busy")
        wait { repository.refresh() }
        assertTrue(wait { repository.allowed() })
        assertEquals(0, control.disconnects)
        assertEquals(listOf("login"), source.calls)
        assertTrue(journal.entries.value.any { "key store unavailable" in it.message })

        // Тег не сошелся — ключ пропал (новый на его месте): вход не вернуть, войти заново.
        flaky.failure = AEADBadTagException("tag mismatch")
        wait { repository.refresh() }
        assertFalse(wait { repository.allowed() })
        assertEquals(1, control.disconnects)
    }

    @Test
    fun `key store failure after the confirmed term signs out as expired`() {
        val flaky = FlakyBox()
        val repository = repository(flaky)
        source.login = LoginResult.Ok(session(expires = now.plus(Duration.ofHours(1))))
        wait { repository.login("user@example.com", "pw", "123456", null) }

        flaky.failure = KeyStoreException("keystore busy")
        repository.clock = { now.plus(Duration.ofHours(2)) }
        wait { repository.refresh() }
        assertEquals(AccountNotice.Expired, wait { repository.state.first { it?.signedIn == false } }!!.notice)
    }

    @Test
    fun `damaged storage file is replaced and the sign-in works`() {
        // Обрезанная запись protobuf: без замены каждое чтение бросало бы CorruptionException.
        file.writeBytes(byteArrayOf(0x0A, 0x7F, 0x01))
        val repository = repository()
        assertFalse(state(repository).signedIn)
        source.login = LoginResult.Ok(session())
        assertEquals(LoginResult.Ok(session()), wait { repository.login("user@example.com", "pw", "123456", null) })
        assertTrue(wait { repository.allowed() })
    }

    @Test
    fun `storage that cannot be read shows the sign-in screen and recovers`() {
        val flaky = FlakyStore(store()).apply { failReads = true }
        val repository = repository(store = flaky)
        assertFalse(state(repository).signedIn, "не прочитано ничего — экран входа, а не пустое окно")
        wait { repository.refresh() }
        assertFalse(wait { repository.allowed() })
        assertTrue(journal.entries.value.any { "account storage read failed" in it.message })

        source.login = LoginResult.Ok(session())
        assertEquals(LoginResult.Ok(session()), wait { repository.login("user@example.com", "pw", "123456", null) })
        flaky.failReads = false
        assertTrue(wait { repository.state.first { it?.signedIn == true } }!!.signedIn, "чтение вернулось — вход виден")
    }

    @Test
    fun `storage write failures do not crash the checks, the sign-in or the sign-out`() {
        val flaky = FlakyStore(store())
        val repository = repository(store = flaky)
        source.login = LoginResult.Ok(session())
        wait { repository.login("user@example.com", "pw", "123456", null) }
        monitor.set(TunnelStatus(TunnelState.Connected, serverId = "nl"))
        flaky.failWrites = true

        // Сайт подтвердил доступ, а записать нельзя: вход остается, проверка не падает.
        source.status = StatusResult.Ok(now.plus(Duration.ofDays(31)), AccountUser("user@example.com", "Участник"), AccountDevice(42, "Pixel"))
        wait { repository.refresh() }
        assertTrue(wait { repository.allowed() })
        assertTrue(journal.entries.value.any { "account check  failed IOException" in it.message })

        // Вход: uid не записать — «хранилище не работает», сайт не спрашиваем.
        source.calls.clear()
        assertEquals(LoginResult.Failed(AccountError.Store), wait { repository.login("user@example.com", "pw", "123456", null) })
        assertEquals(emptyList(), source.calls)

        // Выход: вход стереть не удалось, но защита выключена и выход не упал.
        wait { repository.logout() }
        assertEquals(1, control.disconnects)
        assertTrue(journal.entries.value.any { "account sign-out  storage error" in it.message })
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
                delay(10)
            }
        }
    }

    @Test
    fun `update feed goes out with the stored token and comes back as is`() {
        val repository = repository()
        assertEquals(UpdatesFeed.SignedOut, wait { repository.updates("stable") }, "без входа — без запроса")
        assertEquals(emptyList(), source.calls)

        source.login = LoginResult.Ok(session())
        wait { repository.login("user@example.com", "pw", "123456", null) }
        val feed = UpdatesFeed.Ok("""{"releases":[]}""")
        source.feed = feed
        assertEquals(feed, wait { repository.updates("beta") })
        assertEquals(listOf("login", "updates"), source.calls)
        assertEquals(listOf("tok-secret-1"), source.tokens, "токен открыт из хранилища и ушел только сайту")
        assertEquals(listOf("beta"), source.channels)
        assertTrue(wait { repository.allowed() }, "лента не трогает вход")
        assertFalse(journal.entries.value.any { "tok-secret-1" in it.message })
    }

    @Test
    fun `401 on the update feed revokes access like the access check`() {
        val repository = repository()
        source.login = LoginResult.Ok(session())
        wait { repository.login("user@example.com", "pw", "123456", null) }
        monitor.set(TunnelStatus(TunnelState.Connected, serverId = "nl"))
        var allowedAtStop: Boolean? = null
        control.onDisconnect = { allowedAtStop = runBlocking { repository.allowed() } }

        source.feed = UpdatesFeed.Revoked
        assertEquals(UpdatesFeed.Revoked, wait { repository.updates("stable") })

        val after = wait { repository.state.first { it?.signedIn == false } }!!
        assertEquals(AccountNotice.Revoked, after.notice)
        assertEquals(1, control.disconnects, "защита выключена")
        assertEquals(false, allowedAtStop, "вход стерт раньше, чем выключена защита")
        assertFalse(file.readBytes().toString(Charsets.ISO_8859_1).contains("token"), "токен стерт")
        assertTrue(journal.entries.value.any { "account updates  access revoked" in it.message })
    }

    @Test
    fun `other feed answers keep the sign-in`() {
        val repository = repository()
        source.login = LoginResult.Ok(session())
        wait { repository.login("user@example.com", "pw", "123456", null) }
        source.feed = UpdatesFeed.Unreachable
        assertEquals(UpdatesFeed.Unreachable, wait { repository.updates("stable") })
        assertTrue(wait { repository.allowed() })
        assertEquals(0, control.disconnects)
    }

    @Test
    fun `update feed is not asked with a seeded sign-in`() {
        val seeded = repository()
        wait { seeded.seedForTests(session()) }
        assertEquals(UpdatesFeed.Unreachable, wait { seeded.updates("stable") }, "поддельный токен сайт отозвал бы")
        assertEquals(emptyList(), source.calls)
        assertTrue(wait { seeded.allowed() })
    }

    @Test
    fun `update feed is not asked while the key store does not answer`() {
        val flaky = FlakyBox()
        val repository = repository(flaky)
        source.login = LoginResult.Ok(session())
        wait { repository.login("user@example.com", "pw", "123456", null) }
        flaky.failure = ProviderException("keystore busy")
        assertEquals(UpdatesFeed.Unreachable, wait { repository.updates("stable") })
        assertEquals(listOf("login"), source.calls, "токен не открыть — сайт не спрашиваем")
        assertTrue(wait { repository.allowed() }, "временный сбой хранилища ключей вход не стирает")
    }
}

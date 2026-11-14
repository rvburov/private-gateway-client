package com.privategateway.client.updates

import android.content.Context
import android.content.Intent
import android.content.pm.PackageInstaller
import android.provider.Settings
import androidx.datastore.core.DataStore
import androidx.datastore.preferences.core.PreferenceDataStoreFactory
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.intPreferencesKey
import androidx.datastore.preferences.core.longPreferencesKey
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.privategateway.client.account.UpdatesFeedSource
import com.privategateway.client.core.account.UpdatesFeed
import com.privategateway.client.data.SettingsRepository
import com.privategateway.client.data.UpdateInstallation
import com.privategateway.client.vpn.TunnelJournal
import com.privategateway.client.waitForAsync
import com.sun.net.httpserver.HttpServer
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.job
import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Before
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import org.junit.runner.RunWith
import java.io.File
import java.io.IOException
import java.net.InetAddress
import java.net.InetSocketAddress
import java.security.KeyPair
import java.security.KeyPairGenerator
import java.security.MessageDigest
import java.security.Signature
import java.security.spec.ECGenParameterSpec
import java.util.Base64
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.CopyOnWriteArraySet
import java.util.concurrent.atomic.AtomicInteger
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * Обновление приложения: проверка по ленте сайта (подпись тестовым ключом), не чаще раза в 12 часов при показе окна, загрузка
 * по билету с локального сервера 127.0.0.1 со сверкой размера и SHA-256, разрешение «Установка неизвестных приложений» и сессия
 * установщика Android — без сети и устройства.
 */
@RunWith(AndroidJUnit4::class)
class AppUpdatesTest {
    @get:Rule
    val folder = TemporaryFolder()

    private val context = ApplicationProvider.getApplicationContext<Context>()
    private val scope = CoroutineScope(Dispatchers.IO + SupervisorJob())
    private val server = HttpServer.create(InetSocketAddress(InetAddress.getLoopbackAddress(), 0), 0).apply { start() }
    private val files = "http://127.0.0.1:${server.address.port}/api/app/updates/files/"
    private val key: KeyPair = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }.generateKeyPair()
    private val pem = "-----BEGIN PUBLIC KEY-----\n" + Base64.getEncoder().encodeToString(key.public.encoded) + "\n-----END PUBLIC KEY-----\n"
    private val apk = ByteArray(300 * 1024) { (it * 7 + 3).toByte() }
    private val journal = TunnelJournal()
    private val site = Site()
    private val installer = Installer()
    private val instances = mutableListOf<AppUpdates>()
    private lateinit var store: DataStore<Preferences>
    private lateinit var settings: SettingsRepository

    /** Сервер файлов: что он отдает по билету, сколько раз спросили и какие билеты уже истекли. */
    @Volatile
    private var served: ByteArray = apk
    private val downloads = AtomicInteger()
    private val expired = CopyOnWriteArraySet<String>()
    private val tickets = AtomicInteger()

    @Volatile
    private var allowed = true

    @Volatile
    private var now = 1_800_000_000_000L

    /** Лента сайта без сети: ответ задает тест, что спросили (канал) — в [channels]. */
    private class Site : UpdatesFeedSource {
        @Volatile
        var answer: suspend () -> UpdatesFeed = { UpdatesFeed.Unreachable }
        val channels = CopyOnWriteArrayList<String>()

        override suspend fun updates(channel: String): UpdatesFeed {
            channels += channel
            return answer()
        }
    }

    /** Установщик Android без Android: номера сессий, записанные APK и брошенные сессии. */
    private class Installer : ApkInstaller {
        val sessions = CopyOnWriteArrayList<Int>()
        val committed = CopyOnWriteArrayList<ByteArray>()
        val abandoned = CopyOnWriteArrayList<Int>()
        val active = CopyOnWriteArraySet<Int>()
        val resumed = CopyOnWriteArrayList<Int>()

        @Volatile
        var failure: Exception? = null

        override fun open(size: Long): Int = (41 + sessions.size).also { sessions += it; active += it }

        override fun commit(id: Int, apk: File) {
            failure?.let { throw it }
            committed += apk.readBytes()
        }

        override fun abandon(id: Int) {
            abandoned += id
            active -= id
        }

        override fun owns(id: Int): Boolean = id in active

        override fun resume(id: Int) {
            check(owns(id))
            resumed += id
        }
    }

    @Before
    fun create() {
        store = PreferenceDataStoreFactory.create(scope = scope) { folder.root.resolve("settings.preferences_pb") }
        settings = SettingsRepository(store)
        server.createContext("/api/app/updates/files/") { exchange ->
            downloads.incrementAndGet()
            val ticket = exchange.requestURI.path.substringAfterLast('/')
            val (status, body) = if (ticket in expired) 403 to """{"error":{"code":"ticket_expired"}}""".toByteArray() else 200 to served
            exchange.sendResponseHeaders(status, body.size.toLong())
            exchange.responseBody.use { it.write(body) }
        }
    }

    @After
    fun close() {
        instances.forEach { it.close() }
        server.stop(0)
        runBlocking { scope.coroutineContext.job.cancelAndJoin() }
    }

    /** Обновления «процесса» на общих настройках: новый вызов — перезапуск приложения. */
    private fun updates(repository: SettingsRepository = settings): AppUpdates = AppUpdates(context, site, repository, installer, journal).also {
        it.clock = { now }
        it.installed = "0.2.1"
        it.keys = { listOf(pem) }
        it.files = files
        it.canInstall = { allowed }
        instances += it
    }

    private fun sha256(bytes: ByteArray) = MessageDigest.getInstance("SHA-256").digest(bytes).joinToString("") { "%02x".format(it) }

    /** Лента с одним выпуском: манифест подписан тестовым ключом, у каждого запроса — новый билет на APK. */
    private fun feed(version: String = "0.3.0", rollout: Int = 100, revision: Long = 1): UpdatesFeed {
        val manifest = ("""{"version":"$version","channel":"stable","rollout":$rollout,"revision":$revision,"published":"2026-11-13T00:00:00Z",""" +
            """"notes":"https://private-gateway.duckdns.org/downloads","highlights":["Обновления с сайта"],""" +
            """"assets":{"android-apk":{"file":"PrivateGateway-$version-android.apk","sha256":"${sha256(apk)}","size":${apk.size}}}}""").toByteArray()
        val signature = Base64.getEncoder().encodeToString(Signature.getInstance("SHA256withECDSA").run { initSign(key.private); update(manifest); sign() })
        val ticket = "ticket-" + tickets.incrementAndGet()
        return UpdatesFeed.Ok(
            """{"releases":[{"version":"$version","channel":"stable","manifest":"${Base64.getEncoder().encodeToString(manifest)}",""" +
                """"signature":"$signature","files":{"android-apk":{"url":"$files$ticket","expires_at":"2026-11-13T10:30:00Z"}}}]}""",
        )
    }

    private fun <T> wait(block: suspend () -> T): T = waitForAsync(block)

    private val cached: File get() = File(context.cacheDir, "updates/update.apk")

    @Test
    fun `download checked against the signed manifest goes to the android installer`() {
        site.answer = { feed() }
        val updates = updates()
        wait { updates.check()!!.join() }
        val offer = updates.state.value
        assertEquals(UpdateStatus.Available, offer.status)
        assertEquals("0.3.0", offer.version)
        assertEquals(listOf("Обновления с сайта"), offer.highlights)
        assertEquals("https://private-gateway.duckdns.org/downloads", offer.notes)

        wait { updates.install()!!.join() }
        val view = updates.state.value
        assertEquals(InstallStep.Installing, view.step)
        assertEquals(apk.size.toLong(), view.total)
        assertContentEquals(apk, installer.committed.single())
        assertEquals(2, site.channels.size, "перед загрузкой — свежая лента: билет живет 30 минут")
        assertFalse(cached.exists(), "APK уже в сессии Android — копии в cacheDir не остается")
        assertFalse(journal.entries.value.any { "ticket-" in it.message }, "билетов в журнале нет")

        // Android просит подтверждения — его откроет окно, когда оно на экране.
        val confirm = Intent("android.content.pm.action.CONFIRM_INSTALL")
        val session = installer.sessions.single()
        wait { updates.installStatus(session, PackageInstaller.STATUS_PENDING_USER_ACTION, confirm, null).join() }
        assertEquals(confirm, updates.prompts.value)
        updates.prompted(confirm)
        assertNull(updates.prompts.value)

        // Отказ в окне Android — не ошибка: обновление снова предлагается.
        wait { updates.installStatus(session, PackageInstaller.STATUS_FAILURE_ABORTED, null, null).join() }
        assertEquals(InstallStep.None, updates.state.value.step)
        assertEquals(UpdateStatus.Available, updates.state.value.status)
    }

    @Test
    fun `a file that does not match the manifest is deleted and never installed`() {
        site.answer = { feed() }
        served = apk.copyOf().also { it[100] = (it[100] + 1).toByte() }
        val updates = updates()
        wait { updates.check()!!.join() }
        wait { updates.install()!!.join() }
        assertEquals(InstallStep.BadFile, updates.state.value.step)
        assertEquals(emptyList(), installer.committed.toList())
        assertFalse(cached.exists())
        assertTrue(journal.entries.value.any { "file does not match the manifest: sha256" in it.message })
    }

    @Test
    fun `without the install permission android settings open and nothing is downloaded`() {
        site.answer = { feed() }
        allowed = false
        val updates = updates()
        wait { updates.check()!!.join() }
        assertNull(updates.install(), "без разрешения загрузка не начинается")
        assertEquals(InstallStep.Allow, updates.state.value.step)
        val screen = updates.prompts.value!!
        assertEquals(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, screen.action)
        assertEquals("package:" + context.packageName, screen.dataString)
        assertEquals(0, downloads.get())
        assertEquals(1, site.channels.size)

        // Пользователь разрешил в настройках Android и нажал «Скачать и установить» еще раз.
        updates.prompted(screen)
        allowed = true
        wait { updates.install()!!.join() }
        assertEquals(InstallStep.Installing, updates.state.value.step)
        assertEquals(1, downloads.get())
    }

    @Test
    fun `window shown checks at most once in 12 hours and the result survives a restart`() {
        site.answer = { feed() }
        val first = updates()
        wait { first.windowShown().join() }
        assertEquals(UpdateStatus.Available, first.state.value.status)
        assertEquals(listOf("stable"), site.channels.toList())

        now += 11 * HOUR
        wait { first.windowShown().join() }
        assertEquals(1, site.channels.size, "через 11 часов — без запроса")

        // Новый процесс: прошлый итог из настроек — сразу и без запроса.
        first.close()
        val again = updates()
        wait { again.windowShown().join() }
        assertEquals(1, site.channels.size)
        assertEquals(UpdateStatus.Available, again.state.value.status)
        assertEquals("0.3.0", again.state.value.version)

        now += 2 * HOUR
        wait { again.windowShown().join() }
        assertEquals(2, site.channels.size, "прошло 13 часов — проверка снова")

        wait { again.check()!!.join() }
        assertEquals(3, site.channels.size, "«Проверить» — сразу")
    }

    @Test
    fun `a failed check waits 30 minutes, a check without a sign-in waits for nothing`() {
        site.answer = { UpdatesFeed.Unreachable }
        val updates = updates()
        wait { updates.windowShown().join() }
        assertEquals(UpdateStatus.Failed, updates.state.value.status)
        now += 10 * MINUTE
        wait { updates.windowShown().join() }
        assertEquals(1, site.channels.size, "после неудачи — не на каждом показе окна")

        now += 25 * MINUTE
        site.answer = { UpdatesFeed.SignedOut }
        wait { updates.windowShown().join() }
        assertEquals(2, site.channels.size)
        assertEquals(UpdateStatus.SignedOut, updates.state.value.status, "после отзыва доступа карточка просит войти")

        site.answer = { feed() }
        wait { updates.windowShown().join() }
        assertEquals(3, site.channels.size, "без входа сайт не спрашивали — вошли, и проверка сразу")
        assertEquals(UpdateStatus.Available, updates.state.value.status)
    }

    @Test
    fun `the check by itself follows the rollout, the button does not`() {
        runBlocking { store.edit { it[intPreferencesKey("update_bucket")] = 50 } }
        site.answer = { feed(rollout = 50) }
        val updates = updates()
        wait { updates.windowShown().join() }
        assertEquals(UpdateStatus.UpToDate, updates.state.value.status, "раскатка 50 % до номера 50 еще не дошла")
        wait { updates.check()!!.join() }
        assertEquals(UpdateStatus.Available, updates.state.value.status, "по кнопке — видно")

        site.answer = { feed(rollout = 51, revision = 2) }
        now += 13 * HOUR
        wait { updates.windowShown().join() }
        assertEquals(UpdateStatus.Available, updates.state.value.status)
        assertEquals(3, site.channels.size)
    }

    @Test
    fun `rollout stopped right before the download installs nothing`() {
        site.answer = { feed() }
        val updates = updates()
        wait { updates.check()!!.join() }
        site.answer = { feed(rollout = 0, revision = 2) }
        wait { updates.install()!!.join() }
        assertEquals(UpdateStatus.UpToDate, updates.state.value.status)
        assertEquals(InstallStep.None, updates.state.value.step)
        assertEquals(0, downloads.get())
        assertEquals(emptyList(), installer.committed.toList())
    }

    @Test
    fun `an expired ticket takes the feed again once`() {
        site.answer = { feed() }
        val updates = updates()
        wait { updates.check()!!.join() }
        // Билет второй ленты (перед загрузкой) истек — третья лента дает новый.
        expired += "ticket-2"
        wait { updates.install()!!.join() }
        assertEquals(InstallStep.Installing, updates.state.value.step)
        assertEquals(2, downloads.get())
        assertEquals(3, site.channels.size)
        assertContentEquals(apk, installer.committed.single())
    }

    @Test
    fun `tickets expiring twice in a row fail without a third try`() {
        site.answer = { feed() }
        val updates = updates()
        wait { updates.check()!!.join() }
        expired += listOf("ticket-2", "ticket-3")
        wait { updates.install()!!.join() }
        assertEquals(InstallStep.Failed, updates.state.value.step)
        assertEquals(2, downloads.get())
        assertEquals(emptyList(), installer.committed.toList())
    }

    @Test
    fun `android refusing the session is a failure and the file does not stay`() {
        site.answer = { feed() }
        installer.failure = IOException("нет места")
        val updates = updates()
        wait { updates.check()!!.join() }
        wait { updates.install()!!.join() }
        assertEquals(InstallStep.Failed, updates.state.value.step)
        assertFalse(cached.exists())
        assertTrue(journal.entries.value.any { "update install  failed IOException" in it.message })
    }

    @Test
    fun `pressing again abandons the waiting session and its late answer is ignored`() {
        site.answer = { feed() }
        val updates = updates()
        wait { updates.check()!!.join() }
        wait { updates.install()!!.join() }
        val first = installer.sessions.single()
        wait { updates.install()!!.join() }
        assertEquals(listOf(first), installer.abandoned.toList())

        wait { updates.installStatus(first, PackageInstaller.STATUS_FAILURE, null, "брошена").join() }
        assertEquals(InstallStep.Installing, updates.state.value.step, "итог брошенной сессии не считается")
        wait { updates.installStatus(installer.sessions.last(), PackageInstaller.STATUS_FAILURE_CONFLICT, null, "INSTALL_FAILED_UPDATE_INCOMPATIBLE").join() }
        assertEquals(InstallStep.Failed, updates.state.value.step)
        assertTrue(journal.entries.value.any { "INSTALL_FAILED_UPDATE_INCOMPATIBLE" in it.message })
    }

    @Test
    fun `a stale install callback finishes without waiting for a new download`() {
        site.answer = { feed() }
        val updates = updates()
        wait { updates.install()!!.join() }
        val old = installer.sessions.single()
        val started = CompletableDeferred<Unit>()
        val release = CompletableDeferred<Unit>()
        site.answer = {
            started.complete(Unit)
            release.await()
            feed()
        }
        val retry = updates.install()!!
        try {
            wait { started.await() }
            val callback = updates.installStatus(old, PackageInstaller.STATUS_PENDING_USER_ACTION, Intent("old confirmation"), null)
            // Общий mutex занят новой загрузкой. Старый BroadcastReceiver должен завершиться до ее разблокировки.
            wait { callback.join() }
            assertFalse(retry.isCompleted)
            assertNull(updates.prompts.value)
        } finally {
            release.complete(Unit)
            wait { retry.join() }
        }
    }

    @Test
    fun `cold receiver restores the committed session before showing a new system confirmation`() {
        site.answer = { feed() }
        val first = updates()
        wait { first.install()!!.join() }
        val id = installer.sessions.single()
        assertEquals(UpdateInstallation(id, "0.3.0", committed = true), wait { settings.updateInstallation.first() })
        first.close()

        // Процесс создан приемником, до показа Activity и windowShown.
        val again = updates()
        val confirm = Intent("android.content.pm.action.CONFIRM_INSTALL")
        wait { again.installStatus(id, PackageInstaller.STATUS_PENDING_USER_ACTION, confirm, null).join() }
        assertEquals(confirm, again.prompts.value)
        assertEquals(InstallStep.Installing, again.state.value.step)
        assertEquals("0.3.0", again.state.value.version)
        wait { again.windowShown().join() }
        assertEquals(1, site.channels.size, "показ окна не сбрасывает восстанавливаемую установку новой проверкой")

        again.prompted(confirm)
        wait { again.installStatus(id, PackageInstaller.STATUS_PENDING_USER_ACTION, Intent("duplicate confirmation"), null).join() }
        assertNull(again.prompts.value, "повтор callback не открывает подтверждение второй раз")
        wait { again.installStatus(id, PackageInstaller.STATUS_FAILURE_ABORTED, null, null).join() }
        assertNull(wait { settings.updateInstallation.first() })
        assertNull(again.prompts.value)
        assertEquals(InstallStep.None, again.state.value.step)
    }

    @Test
    fun `startup restores own session without reusing an old confirmation intent`() {
        site.answer = { feed() }
        val first = updates()
        wait { first.install()!!.join() }
        val id = installer.sessions.single()
        val confirm = Intent("android.content.pm.action.CONFIRM_INSTALL")
        wait { first.installStatus(id, PackageInstaller.STATUS_PENDING_USER_ACTION, confirm, null).join() }
        first.close()

        val again = updates()
        wait { again.windowShown().join() }
        assertEquals(InstallStep.Installing, again.state.value.step)
        assertNull(again.prompts.value, "старый Intent не сериализуется и не воспроизводится")
        assertEquals(listOf(id), installer.resumed.toList(), "система должна заново прислать подтверждение той же сессии")
        val fresh = Intent("fresh system confirmation")
        wait { again.installStatus(id, PackageInstaller.STATUS_PENDING_USER_ACTION, fresh, null).join() }
        assertEquals(fresh, again.prompts.value)
        wait { again.install()!!.join() }
        assertEquals(listOf(id), installer.abandoned.toList(), "повторная попытка бросает прежнюю собственную сессию")
        wait { again.installStatus(id, PackageInstaller.STATUS_PENDING_USER_ACTION, confirm, null).join() }
        assertNull(again.prompts.value, "позднее подтверждение брошенной сессии не открывается")
    }

    @Test
    fun `process death before commit leaves a retry instead of pretending the package is ready`() {
        val id = installer.open(apk.size.toLong())
        wait { settings.setUpdateInstallation(UpdateInstallation(id, "0.3.0", committed = false)) }
        val again = updates()
        wait { again.windowShown().join() }
        assertEquals(InstallStep.Failed, again.state.value.step)
        assertEquals(listOf(id), installer.abandoned.toList())
        assertTrue(installer.resumed.isEmpty(), "недописанный APK нельзя передавать Android как готовый")
        assertTrue(installer.committed.isEmpty())
        assertNull(wait { settings.updateInstallation.first() })
    }

    @Test
    fun `a new pending action proves commit after death before committed phase was saved`() {
        val id = installer.open(apk.size.toLong())
        wait { settings.setUpdateInstallation(UpdateInstallation(id, "0.3.0", committed = false)) }
        val again = updates()
        val confirmation = Intent("system confirmation")
        wait { again.installStatus(id, PackageInstaller.STATUS_PENDING_USER_ACTION, confirmation, null).join() }
        assertEquals(confirmation, again.prompts.value)
        assertEquals(UpdateInstallation(id, "0.3.0", committed = true), wait { settings.updateInstallation.first() })
        assertEquals(InstallStep.Installing, again.state.value.step)
    }

    @Test
    fun `a missing session is an interrupted install unless the target is already installed`() {
        site.answer = { feed() }
        val first = updates()
        wait { first.install()!!.join() }
        first.close()
        installer.active.clear()
        val again = updates()
        wait { again.windowShown().join() }
        assertEquals(InstallStep.Failed, again.state.value.step)
        assertEquals("0.3.0", again.state.value.version)
        assertNull(wait { settings.updateInstallation.first() })

        wait { settings.setUpdateInstallation(UpdateInstallation(987, "0.3.0")) }
        again.close()
        val upgraded = updates().also { it.installed = "0.3.0" }
        wait { upgraded.windowShown().join() }
        assertEquals(UpdateStatus.UpToDate, upgraded.state.value.status)
        assertEquals(InstallStep.None, upgraded.state.value.step)
        assertNull(wait { settings.updateInstallation.first() })
    }

    @Test
    fun `a saved session that is no longer owned cannot open a pending confirmation`() {
        site.answer = { feed() }
        val first = updates()
        wait { first.install()!!.join() }
        val id = installer.sessions.single()
        first.close()
        installer.active.clear()
        val again = updates()
        wait { again.installStatus(id, PackageInstaller.STATUS_PENDING_USER_ACTION, Intent("confirmation"), null).join() }
        assertNull(again.prompts.value)
        assertEquals(InstallStep.Failed, again.state.value.step)
        assertNull(wait { settings.updateInstallation.first() })
    }

    @Test
    fun `terminal success after process death clears the durable session and repeated callbacks are ignored`() {
        site.answer = { feed() }
        val first = updates()
        wait { first.install()!!.join() }
        val id = installer.sessions.single()
        first.close()
        installer.active.clear()
        val again = updates()
        wait { again.installStatus(id, PackageInstaller.STATUS_SUCCESS, null, null).join() }
        assertEquals(UpdateStatus.UpToDate, again.state.value.status)
        assertNull(wait { settings.updateInstallation.first() })
        wait { again.installStatus(id, PackageInstaller.STATUS_FAILURE_ABORTED, null, null).join() }
        assertEquals(UpdateStatus.UpToDate, again.state.value.status)
    }

    @Test
    fun `session storage failure prevents commit and abandons the newly opened session`() {
        val failing = object : DataStore<Preferences> {
            override val data: Flow<Preferences> get() = store.data
            override suspend fun updateData(transform: suspend (t: Preferences) -> Preferences): Preferences = store.updateData { before ->
                val after = transform(before)
                if (after[intPreferencesKey("update_session")] != before[intPreferencesKey("update_session")]) throw IOException("запись недоступна")
                after
            }
        }
        site.answer = { feed() }
        val updates = updates(SettingsRepository(failing))
        wait { updates.install()!!.join() }
        assertEquals(InstallStep.Failed, updates.state.value.step)
        assertTrue(installer.committed.isEmpty())
        assertEquals(installer.sessions.toList(), installer.abandoned.toList())
        assertTrue(installer.active.isEmpty())
    }

    @Test
    fun `signed stop survives restart and an old valid signature cannot restore the offer`() {
        val old = feed(revision = 1)
        site.answer = { old }
        val first = updates()
        wait { first.check()!!.join() }
        site.answer = { feed(rollout = 0, revision = 2) }
        wait { first.check()!!.join() }
        assertEquals(UpdateStatus.UpToDate, first.state.value.status)
        first.close()

        site.answer = { old }
        val again = updates()
        wait { again.check()!!.join() }
        assertEquals(UpdateStatus.Failed, again.state.value.status)
        assertNull(wait { settings.updateMemo.first().checkedAt }, "ошибка не оставляет успешный кеш на 12 часов")
        wait { again.install()!!.join() }
        assertTrue(installer.committed.isEmpty())
        assertEquals(0, downloads.get())

        site.answer = { feed(revision = 3) }
        wait { again.install()!!.join() }
        assertEquals(InstallStep.Installing, again.state.value.step)
    }

    @Test
    fun `bad signature is a failure and replaces a previously successful cached check`() {
        site.answer = { UpdatesFeed.Ok("""{"releases":[]}""") }
        val first = updates()
        wait { first.check()!!.join() }
        assertEquals(UpdateStatus.UpToDate, first.state.value.status)

        val valid = (feed() as UpdatesFeed.Ok).json
        site.answer = { UpdatesFeed.Ok(valid.replace(Regex("\"signature\":\"[^\"]+\""), "\"signature\":\"invalid\"")) }
        wait { first.check()!!.join() }
        assertEquals(UpdateStatus.Failed, first.state.value.status)
        assertTrue(journal.entries.value.any { "manifest or signature rejected" in it.message })
        assertEquals(null, wait { settings.updateMemo.first().checkedAt })
        first.close()
        val again = updates()
        wait { again.windowShown().join() }
        assertEquals(UpdateStatus.Failed, again.state.value.status, "после перезапуска ошибка не превращается в последнюю версию")
    }

    @Test
    fun `revoked access is shown even when clearing the old successful memo fails`() {
        val faulty = object : DataStore<Preferences> {
            override val data: Flow<Preferences> get() = store.data
            override suspend fun updateData(transform: suspend (t: Preferences) -> Preferences): Preferences = store.updateData { before ->
                val after = transform(before)
                val key = longPreferencesKey("update_checked_at")
                if (before[key] != null && after[key] == null) throw IOException("очистка кеша недоступна")
                after
            }
        }
        val updates = updates(SettingsRepository(faulty))
        site.answer = { feed() }
        wait { updates.check()!!.join() }
        site.answer = { UpdatesFeed.Revoked }
        wait { updates.check()!!.join() }
        assertEquals(UpdateStatus.SignedOut, updates.state.value.status)
        assertNull(updates.state.value.version)
        assertTrue(journal.entries.value.any { "memo not cleared IOException" in it.message })
        wait { updates.install()!!.join() }
        assertEquals(UpdateStatus.SignedOut, updates.state.value.status)
        assertEquals(0, downloads.get())

        site.answer = { feed() }
        wait { updates.windowShown().join() }
        assertEquals(UpdateStatus.Available, updates.state.value.status, "после нового входа старый неочищенный кеш не задерживает проверку")
        assertEquals(0, downloads.get(), "показ окна проверяет ленту, но не устанавливает обновление")
    }

    @Test
    fun `without keys in the app the site is not asked`() {
        site.answer = { feed() }
        val updates = updates().also { it.keys = { emptyList() } }
        wait { updates.check()!!.join() }
        assertEquals(UpdateStatus.NotSigned, updates.state.value.status)
        assertEquals(emptyList(), site.channels.toList())
    }

    private companion object {
        const val MINUTE = 60_000L
        const val HOUR = 60 * MINUTE
    }
}

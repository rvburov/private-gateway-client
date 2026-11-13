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
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.privategateway.client.account.UpdatesFeedSource
import com.privategateway.client.core.account.UpdatesFeed
import com.privategateway.client.data.SettingsRepository
import com.privategateway.client.vpn.TunnelJournal
import com.privategateway.client.waitForAsync
import com.sun.net.httpserver.HttpServer
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancelAndJoin
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
        var answer: () -> UpdatesFeed = { UpdatesFeed.Unreachable }
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

        @Volatile
        var failure: Exception? = null

        override fun open(size: Long): Int = (41 + sessions.size).also { sessions += it }

        override fun commit(id: Int, apk: File) {
            failure?.let { throw it }
            committed += apk.readBytes()
        }

        override fun abandon(id: Int) {
            abandoned += id
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
    private fun updates(): AppUpdates = AppUpdates(context, site, settings, installer, journal).also {
        it.clock = { now }
        it.installed = "0.2.1"
        it.keys = { listOf(pem) }
        it.files = files
        it.canInstall = { allowed }
        instances += it
    }

    private fun sha256(bytes: ByteArray) = MessageDigest.getInstance("SHA-256").digest(bytes).joinToString("") { "%02x".format(it) }

    /** Лента с одним выпуском: манифест подписан тестовым ключом, у каждого запроса — новый билет на APK. */
    private fun feed(version: String = "0.3.0", rollout: Int = 100): UpdatesFeed {
        val manifest = ("""{"version":"$version","channel":"stable","rollout":$rollout,"published":"2026-11-13T00:00:00Z",""" +
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
        updates.installStatus(session, PackageInstaller.STATUS_PENDING_USER_ACTION, confirm, null)
        assertEquals(confirm, updates.prompts.value)
        updates.prompted(confirm)
        assertNull(updates.prompts.value)

        // Отказ в окне Android — не ошибка: обновление снова предлагается.
        updates.installStatus(session, PackageInstaller.STATUS_FAILURE_ABORTED, null, null)
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
        assertEquals(UpdateStatus.Failed, updates.state.value.status, "сама проверка «Войдите в аккаунт» не показывает")

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

        site.answer = { feed(rollout = 51) }
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
        site.answer = { feed(rollout = 0) }
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

        updates.installStatus(first, PackageInstaller.STATUS_FAILURE, null, "брошена")
        assertEquals(InstallStep.Installing, updates.state.value.step, "итог брошенной сессии не считается")
        updates.installStatus(installer.sessions.last(), PackageInstaller.STATUS_FAILURE_CONFLICT, null, "INSTALL_FAILED_UPDATE_INCOMPATIBLE")
        assertEquals(InstallStep.Failed, updates.state.value.step)
        assertTrue(journal.entries.value.any { "INSTALL_FAILED_UPDATE_INCOMPATIBLE" in it.message })
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

package com.privategateway.client.ui

import android.content.Context
import android.net.Uri
import androidx.datastore.preferences.core.PreferenceDataStoreFactory
import androidx.lifecycle.viewModelScope
import androidx.room.Room
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.privategateway.client.FakeBox
import com.privategateway.client.Fixtures
import com.privategateway.client.waitForAsync
import com.privategateway.client.core.vpn.TunnelState
import com.privategateway.client.core.vpn.TunnelStatus
import com.privategateway.client.data.AppDatabase
import com.privategateway.client.data.ServerRepository
import com.privategateway.client.R
import com.privategateway.client.data.SettingsRepository
import com.privategateway.client.subscriptions.SubscriptionTransport
import com.privategateway.client.subscriptions.SubscriptionUpdater
import com.privategateway.client.ui.qr.QrImages
import com.privategateway.client.ui.qr.QrRead
import com.privategateway.client.core.subscriptions.DownloadFailure
import com.privategateway.client.core.subscriptions.FailureReason
import com.privategateway.client.core.subscriptions.SubscriptionContent
import com.privategateway.client.core.subscriptions.SubscriptionDownloadException
import com.privategateway.client.core.subscriptions.SubscriptionSource
import com.privategateway.client.vpn.CoreEngine
import com.privategateway.client.vpn.LatencyMeter
import com.privategateway.client.vpn.TunnelControl
import com.privategateway.client.vpn.TunnelMonitor
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.job
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.test.UnconfinedTestDispatcher
import kotlinx.coroutines.test.resetMain
import kotlinx.coroutines.test.setMain
import org.junit.After
import org.junit.Before
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import org.junit.runner.RunWith
import java.net.URI
import java.util.concurrent.atomic.AtomicReference
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull

@OptIn(ExperimentalCoroutinesApi::class)
@RunWith(AndroidJUnit4::class)
class MainViewModelTest {
    @get:Rule
    val folder = TemporaryFolder()

    private val context = ApplicationProvider.getApplicationContext<Context>()
    private val database = Room.inMemoryDatabaseBuilder(context, AppDatabase::class.java).build()
    private val scope = CoroutineScope(Dispatchers.IO + SupervisorJob())
    private lateinit var settings: SettingsRepository
    private lateinit var model: MainViewModel
    private val servers by lazy { ServerRepository(database, FakeBox()) }
    private val source = FakeSource()
    private val monitor = TunnelMonitor()
    private val images = FakeImages()

    /** Галерея без галереи: что «нашлось» на картинке, задает тест. */
    private class FakeImages : QrImages {
        @Volatile var result: QrRead = QrRead.None

        override suspend fun read(image: Uri): QrRead = result
    }
    private val control = FakeControl()

    /** Команды службе туннеля — только счет. */
    private class FakeControl : TunnelControl {
        @Volatile var connects = 0

        @Volatile var disconnects = 0

        override fun connect() {
            connects++
        }

        override fun disconnect() {
            disconnects++
        }
    }

    /** Подписка «по сети» без сети: ответ или неудача, которые задал тест. */
    private class FakeSource : SubscriptionSource {
        var content: SubscriptionContent? = null
        var failure: SubscriptionDownloadException? = null
        var entered: CountDownLatch? = null
        var release: CountDownLatch? = null

        override fun download(url: URI): SubscriptionContent {
            val response = failure?.let { throw it } ?: requireNotNull(content)
            entered?.countDown()
            release?.let { check(it.await(3, TimeUnit.SECONDS)) }
            return response
        }
    }

    private class FakeEngine : CoreEngine {
        override fun start(configJson: String, protect: (Int) -> Boolean, dns: String): String? = null

        override fun changeDns(dns: String): String? = null

        override fun stop() = Unit

        override fun ping(request: String, dns: String): String? = null

        override fun version() = "26.10.21"
    }

    @Before
    fun create() {
        Dispatchers.setMain(UnconfinedTestDispatcher())
        settings = SettingsRepository(PreferenceDataStoreFactory.create(scope = scope) { folder.root.resolve("settings.preferences_pb") })
        model = MainViewModel(monitor, servers, settings, FakeEngine(), SubscriptionTransport { uri, _ -> source.download(uri) }, control, LatencyMeter(FakeEngine(), servers, settings, monitor) { emptyList() }, SubscriptionUpdater(SubscriptionTransport { uri, _ -> source.download(uri) }, servers, settings, monitor), images)
    }

    @After
    fun close() {
        // Сначала — работа модели, до конца: ее продолжения идут через Dispatchers.Main, и resetMain() рядом с ними падает
        // («Dispatchers.Main is used concurrently with setting it» — отмена хранилища будила их из потока IO). Потом —
        // хранилище и база. cancel() только начинает отмену DataStore: его IO-акторы еще могут закрывать файл и будить
        // продолжения. Дожидаемся их до закрытия Room, сброса Main и удаления TemporaryFolder следующим правилом JUnit.
        runBlocking { model.viewModelScope.coroutineContext.job.cancelAndJoin() }
        runBlocking { scope.coroutineContext.job.cancelAndJoin() }
        database.close()
        Dispatchers.resetMain()
    }

    // Предел ожидания, а не задержка: тест идет дальше, как только условие выполнилось. 10 с, как в других тестах
    // приложения: на занятой машине CI первая запись в DataStore и Room под Robolectric не уложилась в 5 с (master 395).
    private fun <T> wait(block: suspend () -> T): T = waitForAsync(block)

    private fun waitUntil(condition: () -> Boolean) = wait {
        while (!condition()) {
            delay(10)
        }
    }

    @Test
    fun `first import selects the first new server and a later one keeps the choice`() {
        val links = Fixtures.servers().distinctBy { Fixtures.identity(it) }.take(3)
        val text = links.joinToString("\n") { com.privategateway.client.core.links.LinkWriter.write(it) }

        model.preview(text)
        wait { model.importing.first { it.batch != null } }
        assertEquals(3, model.importing.value.batch?.found)
        model.add()
        wait { model.importing.first { it.added != null } }
        assertEquals(3, model.importing.value.added)
        val ui = wait { model.ui.first { it.servers.size == 3 && it.selectedId != null } }
        assertEquals(ui.servers.first().id, ui.selectedId)
        assertEquals(links.first().name, ui.inUse?.displayName)

        // Второй импорт: выбор не меняется — сервер меняется только нажатием пользователя.
        model.closeImport()
        model.preview(com.privategateway.client.core.links.LinkWriter.write(Fixtures.servers().distinctBy { Fixtures.identity(it) }[3]))
        wait { model.importing.first { it.batch != null } }
        model.add()
        wait { model.importing.first { it.added != null } }
        assertEquals(ui.selectedId, wait { model.ui.first { it.servers.size == 4 } }.selectedId)
    }

    @Test
    fun `duplicates are counted and not added`() {
        val link = com.privategateway.client.core.links.LinkWriter.write(Fixtures.servers().first())
        model.preview(link)
        wait { model.importing.first { it.batch != null } }
        model.add()
        wait { model.importing.first { it.added != null } }
        model.closeImport()

        model.preview(link)
        wait { model.importing.first { it.batch != null } }
        model.add()
        val result = wait { model.importing.first { it.added != null } }
        assertEquals(0, result.added)
        assertEquals(1, result.duplicates)
    }

    @Test
    fun `editing checked text invalidates its servers and subscription address`() {
        val old = "https://example.com/sub/old"
        source.content = SubscriptionContent(com.privategateway.client.core.links.LinkWriter.write(Fixtures.servers().first()), "Старый профиль")
        model.edit(old)
        model.preview(old)
        wait { model.importing.first { it.batch != null } }
        model.edit("https://example.org/sub/new")
        assertNull(model.importing.value.batch)
        assertNull(model.importing.value.url)
        model.add()
        assertEquals(emptyList(), wait { servers.servers.first() })
    }

    @Test
    fun `closing or editing while a subscription loads discards its response`() {
        source.content = SubscriptionContent(com.privategateway.client.core.links.LinkWriter.write(Fixtures.servers().first()), "Старый профиль")
        val entered = CountDownLatch(1)
        val release = CountDownLatch(1)
        source.entered = entered
        source.release = release
        model.preview("https://example.com/sub/old")
        check(entered.await(3, TimeUnit.SECONDS))
        model.closeImport()
        val fresh = com.privategateway.client.core.links.LinkWriter.write(Fixtures.servers().distinctBy { Fixtures.identity(it) }[1])
        model.edit(fresh)
        model.preview(fresh)
        val expected = wait { model.importing.first { it.batch != null } }
        release.countDown()
        // Дождаться всех продолжений модели: старый результат не должен вновь появиться после завершения загрузки.
        waitUntil { model.viewModelScope.coroutineContext.job.children.count() <= 2 }
        assertEquals(expected, model.importing.value)
    }

    @Test
    fun `second tap on add while adding is ignored and the text survives the preview`() {
        val links = Fixtures.servers().distinctBy { Fixtures.identity(it) }.take(3)
        val text = links.joinToString("\n") { com.privategateway.client.core.links.LinkWriter.write(it) }
        model.edit(text)
        model.preview(text)
        wait { model.importing.first { it.batch != null } }
        assertEquals(text, model.importing.value.text)

        // Двойное нажатие: второе добавление нашло бы одни дубли и показало бы «ничего не добавлено».
        model.add()
        assertEquals(true, model.importing.value.adding, "кнопка «Добавить» гаснет сразу")
        model.add()
        val result = wait { model.importing.first { it.added != null } }
        assertEquals(false, result.adding)
        assertEquals(3, result.added)
        assertEquals(0, result.duplicates)
        model.add()
        assertEquals(3, wait { model.ui.first { it.servers.size == 3 } }.servers.size)
        assertEquals(result, model.importing.value)
    }

    @Test
    fun `subscription address is downloaded, named by its title and stored as a group`() {
        val links = Fixtures.servers().distinctBy { Fixtures.identity(it) }.take(2)
        source.content = SubscriptionContent(links.joinToString("\n") { com.privategateway.client.core.links.LinkWriter.write(it) }, "Провайдер")
        model.edit("https://example.com/sub/abcdef")
        model.preview(" https://example.com/sub/abcdef ")
        val ready = wait { model.importing.first { it.batch != null } }
        assertEquals(2, ready.batch?.found)
        assertEquals("Провайдер", ready.name, "название — из Profile-Title")
        assertEquals(URI("https://example.com/sub/abcdef"), ready.url)
        assertEquals(false, ready.insecure)

        model.add()
        wait { model.importing.first { it.added != null } }
        val group = wait { servers.groups.first { it.isNotEmpty() } }.single()
        assertEquals("Провайдер", group.name)
        assertEquals("https://example.com/sub/••••", group.maskedUrl)
        assertEquals(2, group.servers)
    }

    @Test
    fun `failed download says why and http warns about open keys`() {
        source.failure = SubscriptionDownloadException(DownloadFailure.Status, 404)
        model.preview("https://example.com/sub/abcdef")
        assertEquals(FailureReason.NotFound, wait { model.importing.first { it.failure != null } }.failure)

        source.failure = null
        source.content = SubscriptionContent(com.privategateway.client.core.links.LinkWriter.write(Fixtures.servers().first()), null)
        model.preview("http://example.org/feed")
        val ready = wait { model.importing.first { it.batch != null } }
        assertEquals(true, ready.insecure)
        assertEquals(null, ready.failure)
        assertEquals("example.org", ready.name, "без Profile-Title — сервер подписки")
    }

    @Test
    fun `without a name servers go to my servers and the address is not kept`() {
        source.content = SubscriptionContent(com.privategateway.client.core.links.LinkWriter.write(Fixtures.servers().first()), null)
        model.preview("https://example.com/sub/abcdef")
        wait { model.importing.first { it.batch != null } }
        model.name("")
        model.add()
        wait { model.importing.first { it.added != null } }
        assertEquals(1, wait { servers.manualCount.first { it == 1 } })
        assertEquals(emptyList(), wait { servers.groups.first() })
    }

    @Test
    fun `deleting the selected server clears the choice`() {
        model.preview(com.privategateway.client.core.links.LinkWriter.write(Fixtures.servers().first()))
        wait { model.importing.first { it.batch != null } }
        model.add()
        val id = wait { model.ui.first { it.selectedId != null } }.selectedId!!
        model.delete(id)
        val ui = wait { model.ui.first { it.servers.isEmpty() && it.selectedId == null } }
        assertNull(ui.inUse)
    }

    @Test
    fun `notification asks the window to open servers once`() {
        assertNull(model.requestedTab.value)
        model.show(Tab.Servers)
        assertEquals(Tab.Servers, model.requestedTab.value)
        model.tabShown()
        assertNull(model.requestedTab.value, "открытый раздел не открывается снова при повороте экрана")
    }

    @Test
    fun `select changes the choice and core version loads once`() {
        model.select("nl")
        assertEquals("nl", wait { settings.selectedServerId.first { it == "nl" } })
        model.loadCoreVersion()
        assertEquals("26.10.21", wait { model.core.first { it != null } })
    }

    /** Два сервера в списке, первый выбран; номера — в порядке списка. */
    private fun twoServers(): List<String> {
        wait { servers.add(Fixtures.servers().distinctBy { Fixtures.identity(it) }.take(2)) }
        val ids = wait { servers.servers.first { it.size == 2 } }.map { it.id }
        wait { settings.select(ids[0]) }
        return ids
    }

    @Test
    fun `another server under protection reconnects at once and goes to recent`() {
        val (first, second) = twoServers()
        monitor.set(TunnelStatus(TunnelState.Connected, serverId = first))

        model.select(second)
        assertEquals(second, wait { settings.selectedServerId.first { it == second } })
        wait { servers.servers.first { list -> list.single { it.id == second }.lastUsedAt != null } }
        waitUntil { control.connects == 1 }
        assertEquals(0, control.disconnects)
    }

    @Test
    fun `the same server changes nothing`() {
        val (first, _) = twoServers()
        monitor.set(TunnelStatus(TunnelState.Connected, serverId = first))

        model.select(first)
        wait { delay(100) }
        assertEquals(0, control.connects, "тот же сервер — без переподключения")
        assertNull(wait { servers.servers.first() }.single { it.id == first }.lastUsedAt, "и «Недавние» не меняются")
    }

    @Test
    fun `without protection a tap only selects`() {
        val (_, second) = twoServers()
        model.select(second)
        wait { settings.selectedServerId.first { it == second } }
        wait { delay(100) }
        assertEquals(0, control.connects)
    }

    @Test
    fun `connect from the card starts protection after the choice is stored`() {
        val (_, second) = twoServers()
        val startedWith = AtomicReference<String?>()
        model.connectTo(second) { startedWith.set(runBlocking { settings.selectedServerId.first() }) }
        waitUntil { startedWith.get() != null }
        assertEquals(second, startedWith.get(), "служба читает выбранный сервер сама — выбор записан до запуска")
        assertEquals(0, control.connects)
    }

    @Test
    fun `deleting the server in use disconnects first`() {
        val (first, second) = twoServers()
        monitor.set(TunnelStatus(TunnelState.Connected, serverId = first))
        model.delete(second)
        assertEquals(0, control.disconnects, "защита идет через другой сервер")

        assertEquals(listOf(first), wait { servers.servers.first { it.size == 1 } }.map { it.id })

        model.delete(first)
        assertEquals(1, control.disconnects)
        assertNull(wait { settings.selectedServerId.first { it == null } })
        assertEquals(emptyList(), wait { servers.servers.first { it.isEmpty() } })
    }

    @Test
    fun `rename keeps a valid own name and the link name clears it`() {
        val (first, _) = twoServers()
        wait { model.ui.first { it.servers.size == 2 } }
        model.rename(first, "  Мой сервер  ")
        assertEquals("Мой сервер", wait { servers.servers.first { list -> list.single { it.id == first }.customName != null } }.single { it.id == first }.displayName)

        model.rename(first, " ".repeat(3))
        model.rename(first, "x".repeat(65))
        wait { delay(100) }
        assertEquals("Мой сервер", wait { servers.servers.first() }.single { it.id == first }.customName, "пустое и длиннее 64 — не сохраняются")

        wait { model.ui.first { ui -> ui.servers.single { it.id == first }.customName == "Мой сервер" } }
        val link = wait { servers.servers.first() }.single { it.id == first }.linkName
        model.rename(first, link)
        assertNull(wait { servers.servers.first { list -> list.single { it.id == first }.customName == null } }.single { it.id == first }.customName)
    }

    @Test
    fun `qr from an image is parsed like typed text and keeps the field`() {
        val link = com.privategateway.client.core.links.LinkWriter.write(Fixtures.servers().first())
        model.edit("свой текст")
        images.result = QrRead.Found(link)
        model.previewQr(Uri.parse("content://media/picker/0/1"))
        val ready = wait { model.importing.first { it.batch != null } }
        assertEquals(1, ready.batch?.found)
        assertEquals("свой текст", ready.text, "найденное на картинке в поле не попадает")
        assertEquals(false, ready.qrReading)
    }

    @Test
    fun `image without a code says so`() {
        images.result = QrRead.None
        model.previewQr(Uri.parse("content://media/picker/0/2"))
        assertEquals(R.string.android_import_qr_none, wait { model.importing.first { it.qrProblem != null } }.qrProblem)

        images.result = QrRead.Failed
        model.previewQr(Uri.parse("content://media/picker/0/3"))
        assertEquals(R.string.android_import_qr_failed, wait { model.importing.first { it.qrProblem == R.string.android_import_qr_failed } }.qrProblem)
    }

    @Test
    fun `camera problem is said in the qr card and the code from the camera is parsed like typed text`() {
        model.cameraProblem(R.string.android_import_qr_camera_denied)
        assertEquals(R.string.android_import_qr_camera_denied, wait { model.importing.first { it.qrProblem != null } }.qrProblem)

        model.preview(com.privategateway.client.core.links.LinkWriter.write(Fixtures.servers().first()))
        val ready = wait { model.importing.first { it.batch != null } }
        assertEquals(1, ready.batch?.found)
        assertNull(ready.qrProblem, "новый разбор убирает прежнюю подсказку")
    }

    @Test
    fun `share link is the stored one with the link name`() {
        val server = Fixtures.servers().first()
        wait { servers.add(listOf(server)) }
        val id = wait { servers.servers.first { it.size == 1 } }.single().id
        wait { servers.rename(id, "Мой") }
        val link = assertNotNull(wait { model.shareLink(id) })
        assertEquals(server.name, assertNotNull(com.privategateway.client.core.links.LinkParser.parse(link).server).name, "своего названия в ссылке нет")
        assertNull(wait { model.shareLink("gone") })
    }
}

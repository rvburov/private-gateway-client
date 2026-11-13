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
import com.privategateway.client.core.inspect.PhoneNetwork
import com.privategateway.client.core.inspect.PhoneNetworkKind
import com.privategateway.client.core.inspect.PublicAddress
import com.privategateway.client.core.servers.ServerFacts
import com.privategateway.client.core.vpn.TunnelErrors
import com.privategateway.client.core.vpn.TunnelState
import com.privategateway.client.core.vpn.TunnelStatus
import com.privategateway.client.data.AppDatabase
import com.privategateway.client.data.ServerRepository
import com.privategateway.client.R
import com.privategateway.client.account.AccountRepository
import com.privategateway.client.core.account.AccountDevice
import com.privategateway.client.core.account.AccountSession
import com.privategateway.client.core.account.AccountSource
import com.privategateway.client.core.account.AccountUser
import com.privategateway.client.core.account.DeviceInfo
import com.privategateway.client.core.account.LoginResult
import com.privategateway.client.core.account.StatusResult
import com.privategateway.client.core.account.UpdatesFeed
import com.privategateway.client.core.account.AccountError
import com.privategateway.client.vpn.TunnelJournal
import com.privategateway.client.data.SettingsRepository
import com.privategateway.client.inspect.PhoneHints
import com.privategateway.client.inspect.PhoneNetworkSource
import com.privategateway.client.inspect.WebFetch
import com.privategateway.client.subscriptions.SubscriptionTransport
import com.privategateway.client.subscriptions.SubscriptionUpdater
import com.privategateway.client.ui.qr.QrImages
import com.privategateway.client.ui.qr.QrRead
import com.privategateway.client.ui.servers.FileRead
import com.privategateway.client.ui.servers.ImportFile
import com.privategateway.client.ui.servers.ImportFiles
import com.privategateway.client.ui.servers.importDone
import com.privategateway.client.core.subscriptions.DownloadFailure
import com.privategateway.client.core.subscriptions.FailureReason
import com.privategateway.client.core.subscriptions.SubscriptionContent
import com.privategateway.client.core.subscriptions.SubscriptionDownloadException
import com.privategateway.client.core.subscriptions.SubscriptionSource
import com.privategateway.client.vpn.ByteCounters
import com.privategateway.client.vpn.ByteSample
import com.privategateway.client.vpn.CoreEngine
import com.privategateway.client.vpn.LatencyMeter
import com.privategateway.client.vpn.TunnelControl
import com.privategateway.client.vpn.TunnelMonitor
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.job
import kotlinx.coroutines.launch
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
import java.util.concurrent.atomic.AtomicLong
import java.util.concurrent.atomic.AtomicReference
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

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

    /** Окно «Открыть» без окна: что «прочиталось» из файла, задает тест. */
    private class FakeFiles : ImportFiles {
        @Volatile var result: FileRead = FileRead.Failed(null)

        override suspend fun read(file: Uri): FileRead = result
    }

    private val files = FakeFiles()

    /** Есть ли на телефоне VPN другого приложения (подсказки Android). */
    @Volatile
    private var otherVpn = false

    /** Ответы «сети» на запрос внешнего IP (Cloudflare trace): что вернуть, задает тест; ответ может ждать. */
    @Volatile private var answer: suspend () -> String? = { null }
    private val asked = CopyOnWriteArrayList<String>()
    private val web = object : WebFetch {
        override suspend fun get(url: String): String? {
            asked += url
            return answer()
        }
    }

    private val wifi = PhoneNetwork(
        PhoneNetworkKind.Wifi, "wlan0", isDefault = true, addresses = listOf("192.0.2.23/24"), gateways = listOf("192.0.2.1"),
        dns = listOf("192.0.2.1"), privateDns = null, mtu = 1500, metered = false, validated = true, captive = false,
    )

    /** Сети телефона без телефона: список меняет тест. */
    private class Networks(list: List<PhoneNetwork>) : PhoneNetworkSource {
        override val networks = MutableStateFlow(list)

        override fun start() = Unit
    }

    private val networks = Networks(listOf(wifi))

    /** Счетчики байт туннеля: каждое чтение — на 1 с позже, 1 250 000 байт принято и 125 000 отправлено (10 и 1 Мбит/с). */
    private val ticks = AtomicLong(0)
    private val counters = ByteCounters {
        val tick = ticks.incrementAndGet()
        ByteSample(tick * 1_250_000, tick * 125_000, tick * 1_000)
    }

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

    /** Сайт аккаунта без сети: вход засеян, сайт не спрашивается. */
    private object NoSite : AccountSource {
        override suspend fun login(email: String, password: String, code: String?, recovery: String?, device: DeviceInfo): LoginResult =
            LoginResult.Failed(AccountError.Network)

        override suspend fun status(token: String): StatusResult = StatusResult.Unreachable

        override suspend fun logout(token: String) = Unit

        override suspend fun updates(token: String, channel: String): UpdatesFeed = UpdatesFeed.Unreachable
    }

    private lateinit var account: AccountRepository

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
        // Вход в аккаунт сайта есть (засеян, как в проверках на устройстве): без него подключение не начинается (решение 619).
        account = AccountRepository(
            PreferenceDataStoreFactory.create(scope = scope) { folder.root.resolve("account.preferences_pb") },
            FakeBox(),
            NoSite,
            control,
            monitor,
            TunnelJournal(),
        )
        runBlocking {
            account.seedForTests(
                AccountSession("tok", java.time.Instant.now().plusSeconds(86_400), AccountUser("user@example.com", ""), AccountDevice(1, "Pixel")),
            )
        }
        model = MainViewModel(
            monitor,
            servers,
            settings,
            FakeEngine(),
            SubscriptionTransport { uri, _ -> source.download(uri) },
            control,
            LatencyMeter(FakeEngine(), servers, settings, monitor) { emptyList() },
            SubscriptionUpdater(SubscriptionTransport { uri, _ -> source.download(uri) }, servers, settings, monitor),
            images,
            web,
            networks,
            counters,
            files,
            { PhoneHints(otherVpn = otherVpn) },
            account,
        )
    }

    @After
    fun close() {
        // Сначала — работа модели, до конца: ее продолжения идут через Dispatchers.Main, и resetMain() рядом с ними падает
        // («Dispatchers.Main is used concurrently with setting it» — отмена хранилища будила их из потока IO). Потом —
        // хранилище и база. cancel() только начинает отмену DataStore: его IO-акторы еще могут закрывать файл и будить
        // продолжения. Дожидаемся их до закрытия Room, сброса Main и удаления TemporaryFolder следующим правилом JUnit.
        runBlocking { model.viewModelScope.coroutineContext.job.cancelAndJoin() }
        account.close()
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
    fun `preview counts new servers, duplicates and renewals before adding`() {
        val links = Fixtures.servers().distinctBy { Fixtures.identity(it) }.take(3)
        wait { servers.add(links.take(1)) }

        // Сохраненный под другим названием и повтор в самом тексте — дубли; новых — два (ImportPreview десктопа).
        val text = listOf(links[0].copy(name = "Другое название"), links[1], links[1], links[2])
            .joinToString("\n") { com.privategateway.client.core.links.LinkWriter.write(it) }
        model.preview(text)
        val ready = wait { model.importing.first { it.batch != null } }
        assertEquals(4, ready.batch?.found)
        assertEquals(2, ready.duplicates)
        assertEquals(2, ready.fresh)
        assertEquals(0, ready.renewals)
        assertEquals(1, wait { servers.servers.first() }.size, "предпросмотр ничего не добавляет")

        model.add()
        val done = wait { model.importing.first { it.added != null } }
        assertEquals(2, done.added)
        assertEquals(R.string.import_added_few, importDone(done, "ru")?.text, "итог — «Добавлено 2 сервера»")
    }

    @Test
    fun `a chosen source is kept through the preview and a new source drops the old preview and name`() {
        val link = com.privategateway.client.core.links.LinkWriter.write(Fixtures.servers().first())
        model.selectSource(ImportSource.Link)
        model.edit(link)
        model.preview(link)
        val ready = wait { model.importing.first { it.batch != null } }
        assertEquals(ImportSource.Link, ready.source)
        model.name("Свой профиль")

        model.selectSource(ImportSource.Clipboard)
        val switched = model.importing.value
        assertEquals(ImportSource.Clipboard, switched.source)
        assertNull(switched.batch, "прежний предпросмотр не нужен")
        assertEquals("", switched.name, "название — заново, как у десктопа")
        assertEquals(link, switched.text, "текст поля ссылки остается")
    }

    @Test
    fun `clipboard without text says so and with text previews it without touching the field`() {
        model.edit("свой текст")
        model.fromClipboard("   ")
        assertEquals(R.string.import_empty_clipboard, model.importing.value.problem?.text)
        assertEquals(ImportSource.Clipboard, model.importing.value.source)

        model.fromClipboard(com.privategateway.client.core.links.LinkWriter.write(Fixtures.servers().first()))
        val ready = wait { model.importing.first { it.batch != null } }
        assertEquals(1, ready.batch?.found)
        assertNull(ready.problem)
        assertEquals("свой текст", ready.text, "текст из буфера в поле не попадает")
    }

    @Test
    fun `a file is parsed and names the profile by itself, an unreadable one is named in the message`() {
        val links = Fixtures.servers().distinctBy { Fixtures.identity(it) }.take(2)
        files.result = FileRead.Found("work-servers.yaml", links.joinToString("\n") { com.privategateway.client.core.links.LinkWriter.write(it) })
        model.selectSource(ImportSource.File)
        model.previewFile(Uri.parse("content://documents/document/1"))
        val ready = wait { model.importing.first { it.batch != null } }
        assertEquals(ImportSource.File, ready.source)
        assertEquals(2, ready.batch?.found)
        assertEquals("work-servers", ready.name, "название — имя файла без расширения")

        files.result = FileRead.Failed("huge.txt")
        model.previewFile(Uri.parse("content://documents/document/2"))
        val failed = wait { model.importing.first { it.problem != null } }
        assertEquals(R.string.import_file_failed, failed.problem?.text)
        assertEquals(listOf("huge.txt"), failed.problem?.args?.toList())
        assertNull(failed.batch)
    }

    @Test
    fun `profile name from a file drops only the extension`() {
        assertEquals("work-servers", ImportFile.profileName("work-servers.yaml"))
        assertEquals("my.servers", ImportFile.profileName("my.servers.txt"))
        assertEquals("sub", ImportFile.profileName("sub"))
        assertEquals(".hidden", ImportFile.profileName(".hidden"))
    }

    @Test
    fun `adding the same subscription again updates it and says so`() {
        val links = Fixtures.servers().distinctBy { Fixtures.identity(it) }.take(3)
        source.content = SubscriptionContent(links.take(2).joinToString("\n") { com.privategateway.client.core.links.LinkWriter.write(it) }, "Провайдер")
        model.preview("https://example.com/sub/abcdef")
        wait { model.importing.first { it.batch != null } }
        model.add()
        wait { model.importing.first { it.added != null } }
        model.closeImport()
        // Выбранный сервер, пропавший из подписки, остается до другого выбора: здесь выбора нет — пропавший удаляется.
        wait { settings.select(null) }

        // Подписка теперь отдает второй и третий: первый удалится, третий добавится — в той же группе (решение 528).
        source.content = SubscriptionContent(links.drop(1).joinToString("\n") { com.privategateway.client.core.links.LinkWriter.write(it) }, "Провайдер")
        model.preview("https://example.com/sub/abcdef")
        val ready = wait { model.importing.first { it.batch != null } }
        assertEquals("Провайдер", ready.existing)
        assertEquals(1, ready.duplicates)
        assertEquals(1, ready.fresh)

        model.add()
        val done = wait { model.importing.first { it.added != null } }
        assertEquals(1, done.added)
        assertEquals(1, done.removed)
        val message = assertNotNull(importDone(done, "ru"))
        assertEquals(R.string.subs_updated, message.text)
        assertEquals(listOf("Провайдер", "1", "1"), message.args.toList())
        assertEquals(1, wait { servers.groups.first() }.size, "вторая группа не появилась")
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
        // Дождаться всех продолжений модели: старый результат не должен вновь появиться после завершения загрузки. Постоянных
        // корутин у модели пять: состояние экрана (ui), наблюдатель подписчиков скорости (stateIn с WhileSubscribed держит
        // его и без подписчиков), слежение за путем запросов для замера IP, запоминание выхода сервера для карточки (решение
        // 583) и события обновления подписок.
        waitUntil { model.viewModelScope.coroutineContext.job.children.count() <= 5 }
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
        assertEquals("https://example.com/sub/****", group.maskedUrl)
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
    fun `connect on launch only when it is on, protection is off and the phone connects to the selected server`() {
        wait { servers.add(Fixtures.servers().distinctBy { Fixtures.identity(it) }) }
        val list = wait { servers.servers.first { it.isNotEmpty() } }
        val phone = list.first { ServerFacts.coreName(it.protocol) != null }.id
        val other = list.first { ServerFacts.coreName(it.protocol) == null }.id
        wait { settings.select(phone) }
        assertFalse(wait { model.connectOnLaunch() }, "по умолчанию выключено")

        wait { settings.setConnectOnLaunch(true) }
        assertTrue(wait { model.connectOnLaunch() })

        monitor.set(TunnelStatus(TunnelState.Connected, serverId = phone))
        assertFalse(wait { model.connectOnLaunch() }, "защита уже включена")
        monitor.set(TunnelStatus(TunnelState.Error, serverId = phone))
        assertFalse(wait { model.connectOnLaunch() }, "после ошибки подключает только сам пользователь")
        monitor.set(TunnelStatus())

        wait { settings.select(other) }
        assertFalse(wait { model.connectOnLaunch() }, "Hysteria2 и TUIC ядру телефона не по силам")
        wait { settings.select(null) }
        assertFalse(wait { model.connectOnLaunch() }, "сервер выбирает только пользователь")
        assertEquals(0, control.connects, "подключает окно, а не модель")

        // Без входа в аккаунт сайта — сначала экран входа, подключения при запуске нет (решение 619).
        wait { settings.select(phone) }
        assertTrue(wait { model.connectOnLaunch() })
        wait { account.logout() }
        assertFalse(wait { model.connectOnLaunch() }, "без входа в аккаунт не подключается")
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
    fun `deleting says the server is deleted once it is gone`() {
        val (first, second) = twoServers()
        val said = CopyOnWriteArrayList<Int>()
        val listener = CoroutineScope(Dispatchers.Unconfined).launch { model.messages.collect { said += it.text } }
        try {
            model.delete(first)
            waitUntil { said.isNotEmpty() }
            assertEquals(listOf(R.string.card_deleted), said.toList())
            assertEquals(listOf(second), wait { servers.servers.first() }.map { it.id }, "«Сервер удален» — когда его уже нет в списке")
        } finally {
            listener.cancel()
        }
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
    fun `real ip is measured without protection, the exit after connecting, and an equal one is a leak`() {
        // Без защиты запрос идет напрямую — это реальный IP; сайты видят его.
        answer = { "fl=1\nip=198.51.100.7\n" }
        model.verify()
        val off = wait { model.ui.first { it.ip.direct == "198.51.100.7" && !it.ip.measuring } }
        assertEquals(ProtectionView.Off, off.view)
        assertEquals("198.51.100.7", off.webAddress)
        assertEquals(PhoneNetworkKind.Wifi, off.network)

        // Подключились — сам замер выхода через туннель, время проверки — для «Проверено …».
        answer = { "ip=203.0.113.24\n" }
        monitor.set(TunnelStatus(TunnelState.Connected, serverId = "nl", since = 1L))
        val on = wait { model.ui.first { it.ip.exit == "203.0.113.24" && !it.ip.measuring } }
        assertEquals(ProtectionView.Connected, on.view)
        assertEquals("203.0.113.24", on.webAddress)
        assertNotNull(on.ip.checkedAt)
        assertEquals("198.51.100.7", on.ip.direct, "реальный IP остается для сравнения")
        assertEquals(List(asked.size) { PublicAddress.TRACE }, asked.toList(), "тот же запрос, что «Внешний IP» на «Проверке»")

        // «Проверить»: выход совпал с реальным IP — трафик мимо туннеля, «Утечка IP».
        answer = { "ip=198.51.100.7\n" }
        model.verify()
        val leak = wait { model.ui.first { it.leak } }
        assertEquals(ProtectionView.Leak, leak.view)
        assertEquals(ProtectionChip("all", R.string.check_all_title, R.string.check_all_same_ip, ChipLook.Warning, R.string.check_all_same_ip_text), ProtectionChips.of(leak.status.state, leak.status.lockdown, leak.closedWithoutTunnel, leak.leak).first())

        // Новое подключение — выход прежнего не в счет, пока не измерен свой.
        answer = { CompletableDeferred<String?>().await() }
        monitor.set(TunnelStatus(TunnelState.Connected, serverId = "nl", since = 2L))
        val next = wait { model.ui.first { it.status.since == 2L && it.ip.measuring } }
        assertNull(next.webAddress)
        assertEquals(ProtectionView.Connected, next.view)
    }

    @Test
    fun `exit through the server is remembered for its card and the real ip is not`() {
        // Решение 583: карточка сервера «VPS + прокси» показывает, какой IP сайты видели через него.
        runBlocking { servers.add(listOf(Fixtures.servers().first { ':' !in it.host })) }
        val id = wait { servers.servers.first { it.isNotEmpty() }.single().id }
        answer = { "ip=198.51.100.7\n" }
        model.verify()
        wait { model.ui.first { it.ip.direct == "198.51.100.7" && !it.ip.measuring } }

        answer = { "ip=203.0.113.90\n" }
        monitor.set(TunnelStatus(TunnelState.Connected, serverId = id, since = 1L))
        wait { model.ui.first { it.serverExit == (id to "203.0.113.90") } }
        waitUntil { runBlocking { servers.exit(id) } == "203.0.113.90" }

        // Утечка: замер через туннель увидел реальный IP — это не выход сервера, запомненный остается.
        answer = { "ip=198.51.100.7\n" }
        model.verify()
        val leak = wait { model.ui.first { it.leak } }
        assertNull(leak.serverExit)
        assertEquals("203.0.113.90", runBlocking { servers.exit(id) })
    }

    @Test
    fun `an answer that came after the path changed is not trusted`() {
        val gate = CompletableDeferred<String?>()
        answer = { gate.await() }
        monitor.set(TunnelStatus(TunnelState.Connected, serverId = "nl", since = 1L))
        wait { model.ui.first { it.ip.measuring && it.ip.session == 1L } }
        // Связь пропала, пока шел замер: ответ мог прийти другим путем — это был бы не адрес выхода.
        monitor.set(TunnelStatus(TunnelState.Reconnecting, serverId = "nl", attempt = 1))
        gate.complete("ip=198.51.100.7\n")
        val ui = wait { model.ui.first { !it.ip.measuring } }
        assertNull(ui.ip.exit)
        assertNull(ui.ip.checkedAt)
    }

    @Test
    fun `another network forgets the real ip and measures it again, android closing the network does not`() {
        answer = { "ip=198.51.100.7\n" }
        model.verify()
        wait { model.ui.first { it.ip.direct == "198.51.100.7" } }

        answer = { "ip=203.0.113.99\n" }
        val cellular = wifi.copy(kind = PhoneNetworkKind.Cellular, name = "rmnet0", addresses = listOf("198.51.100.200/30"))
        networks.networks.value = listOf(cellular)
        val moved = wait { model.ui.first { it.ip.direct == "203.0.113.99" } }
        assertEquals(PhoneNetworkKind.Cellular, moved.network)

        // Сеть закрыл Android («Блокировать подключения без VPN»): прямого пути нет — не мерить.
        val before = asked.size
        monitor.setBlocked(true)
        model.verify()
        wait { delay(100) }
        assertEquals(before, asked.size)
        assertEquals(ProtectionView.Strict, wait { model.ui.first { it.blocked } }.view)
        monitor.setBlocked(false)
    }

    @Test
    fun `refresh says the desktop result and why it did not measure under protection`() {
        val said = CopyOnWriteArrayList<Int>()
        val listener = CoroutineScope(Dispatchers.Unconfined).launch { model.messages.collect { said += it.text } }
        try {
            model.refreshLatency()
            waitUntil { said.isNotEmpty() }
            assertEquals(listOf(R.string.server_refreshed), said.toList())

            monitor.set(TunnelStatus(TunnelState.Connected, serverId = "nl", since = 1L))
            model.refreshLatency()
            waitUntil { said.size == 2 }
            assertEquals(R.string.android_servers_ping_busy, said[1])
        } finally {
            listener.cancel()
        }
    }

    @Test
    fun `tunnel speed comes from the byte counters only under protection`() {
        monitor.set(TunnelStatus(TunnelState.Connected, serverId = "nl", since = 1L))
        val speed = wait { model.speed.first { it != null } }
        assertEquals(10.0, speed!!.downMbps, 1e-9)
        assertEquals(1.0, speed.upMbps, 1e-9)

        monitor.set(TunnelStatus())
        assertNull(wait { model.speed.first { it == null } })
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

    @Test
    fun `refused vpn consent while another app holds the vpn is the other vpn problem`() {
        assertFalse(model.consentRefused(), "другого VPN нет — просто отказ, подсказка")
        assertEquals(TunnelState.Off, monitor.status.value.state)

        otherVpn = true
        assertTrue(model.otherVpnRunning())
        assertTrue(model.consentRefused())
        assertEquals(TunnelStatus(state = TunnelState.Error, error = TunnelErrors.OTHER_VPN), monitor.status.value)
        otherVpn = false
        assertFalse(model.otherVpnRunning(), "«Я закрыл его — проверить снова» видит, что VPN свободен")
    }
}
